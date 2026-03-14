using System.Security.Cryptography;
using System.Buffers.Binary;

namespace SimpleShadowsocks.Protocol.Crypto;

public static class TunnelCryptoHandshake
{
    private const int NonceLength = 12;
    private const int AlgorithmLength = 1;
    private const int MacLength = 32;
    private const int TimestampLength = sizeof(long);
    private const int CounterLength = sizeof(ulong);
    private const int ClientMagicLength = 4;
    private const int ServerMagicLength = 4;
    private static readonly byte[] ClientMagic = "TSC2"u8.ToArray();
    private static readonly byte[] ServerMagic = "TSS2"u8.ToArray();
    private static readonly byte[] HandshakeMacInfo = "ss-v2-handshake-mac"u8.ToArray();
    private static readonly byte[] TransportKeyInfoPrefix = "ss-v2-transport-key-"u8.ToArray();
    private static readonly byte[] TransportClientNonceInfoPrefix = "ss-v2-transport-cnonce-"u8.ToArray();
    private static readonly byte[] TransportServerNonceInfoPrefix = "ss-v2-transport-snonce-"u8.ToArray();
    private static long _clientHandshakeCounter;

    public static async Task<AeadDuplexStream> AsClientAsync(
        Stream plainStream,
        byte[] key,
        TunnelCryptoPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= TunnelCryptoPolicy.Default;
        var handshakeMacKey = DeriveHandshakeMacKey(key);

        var algorithm = policy.PreferredAlgorithm;
        if (!AeadDuplexStream.IsSupported(algorithm))
        {
            throw new InvalidDataException($"Unsupported preferred AEAD algorithm: {algorithm}.");
        }

        var unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counter = (ulong)Interlocked.Increment(ref _clientHandshakeCounter);
        var clientWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var clientHello = BuildClientHello(handshakeMacKey, algorithm, unixTimeSeconds, counter, clientWriteNonce);
        await plainStream.WriteAsync(clientHello, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        var serverHello = await ReadExactlyAsync(
            plainStream,
            ServerMagicLength + AlgorithmLength + TimestampLength + NonceLength + MacLength,
            cancellationToken);
        var (serverWriteNonce, serverUnixTimeSeconds, serverAlgorithm) =
            ValidateAndExtractServerHello(serverHello, handshakeMacKey, clientHello);
        ValidateClockSkew(serverUnixTimeSeconds, policy.HandshakeMaxClockSkewSeconds);
        if (serverAlgorithm != algorithm)
        {
            throw new InvalidDataException(
                $"Server selected unexpected AEAD algorithm: {serverAlgorithm}. Expected: {algorithm}.");
        }

        var material = DeriveTransportMaterial(key, clientWriteNonce, serverWriteNonce, algorithm);
        return new AeadDuplexStream(
            plainStream,
            algorithm,
            material.Key,
            material.ClientWriteBaseNonce,
            material.ServerWriteBaseNonce,
            leaveOpen: true);
    }

    public static async Task<AeadDuplexStream> AsServerAsync(
        Stream plainStream,
        byte[] key,
        TunnelCryptoPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= TunnelCryptoPolicy.Default;
        var handshakeMacKey = DeriveHandshakeMacKey(key);

        var clientHello = await ReadExactlyAsync(
            plainStream,
            ClientMagicLength + AlgorithmLength + TimestampLength + CounterLength + NonceLength + MacLength,
            cancellationToken);

        var (clientWriteNonce, clientUnixTimeSeconds, handshakeCounter, algorithm) =
            ValidateAndExtractClientHello(clientHello, handshakeMacKey);
        ValidateClockSkew(clientUnixTimeSeconds, policy.HandshakeMaxClockSkewSeconds);
        if (!AeadDuplexStream.IsSupported(algorithm))
        {
            throw new InvalidDataException($"Unsupported client AEAD algorithm: {algorithm}.");
        }

        if (!ReplayProtectionCache.TryRegister(clientWriteNonce, handshakeCounter, policy.ReplayWindowSeconds))
        {
            throw new InvalidDataException("Replay detected for encrypted tunnel handshake.");
        }

        var serverWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var serverUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var serverHello = BuildServerHello(handshakeMacKey, clientHello, algorithm, serverUnixTimeSeconds, serverWriteNonce);
        await plainStream.WriteAsync(serverHello, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        var material = DeriveTransportMaterial(key, clientWriteNonce, serverWriteNonce, algorithm);
        return new AeadDuplexStream(
            plainStream,
            algorithm,
            material.Key,
            material.ServerWriteBaseNonce,
            material.ClientWriteBaseNonce,
            leaveOpen: true);
    }

    private static byte[] BuildClientHello(
        byte[] macKey,
        TunnelCipherAlgorithm algorithm,
        long unixTimeSeconds,
        ulong handshakeCounter,
        byte[] clientNonce)
    {
        var payloadLength = ClientMagicLength + AlgorithmLength + TimestampLength + CounterLength + NonceLength;
        var payload = new byte[payloadLength];
        Buffer.BlockCopy(ClientMagic, 0, payload, 0, ClientMagicLength);
        payload[ClientMagicLength] = (byte)algorithm;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(ClientMagicLength + AlgorithmLength, TimestampLength), unixTimeSeconds);
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(ClientMagicLength + AlgorithmLength + TimestampLength, CounterLength),
            handshakeCounter);
        Buffer.BlockCopy(clientNonce, 0, payload, ClientMagicLength + AlgorithmLength + TimestampLength + CounterLength, NonceLength);

        var mac = ComputeHmacSha256(macKey, payload);
        var hello = new byte[payload.Length + mac.Length];
        Buffer.BlockCopy(payload, 0, hello, 0, payload.Length);
        Buffer.BlockCopy(mac, 0, hello, payload.Length, mac.Length);
        return hello;
    }

    private static byte[] BuildServerHello(
        byte[] macKey,
        byte[] clientHello,
        TunnelCipherAlgorithm algorithm,
        long unixTimeSeconds,
        byte[] serverNonce)
    {
        var payload = new byte[ServerMagicLength + AlgorithmLength + TimestampLength + NonceLength];
        Buffer.BlockCopy(ServerMagic, 0, payload, 0, ServerMagicLength);
        payload[ServerMagicLength] = (byte)algorithm;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(ServerMagicLength + AlgorithmLength, TimestampLength), unixTimeSeconds);
        Buffer.BlockCopy(serverNonce, 0, payload, ServerMagicLength + AlgorithmLength + TimestampLength, NonceLength);

        var macInput = new byte[clientHello.Length + payload.Length];
        Buffer.BlockCopy(clientHello, 0, macInput, 0, clientHello.Length);
        Buffer.BlockCopy(payload, 0, macInput, clientHello.Length, payload.Length);

        var mac = ComputeHmacSha256(macKey, macInput);
        var hello = new byte[payload.Length + mac.Length];
        Buffer.BlockCopy(payload, 0, hello, 0, payload.Length);
        Buffer.BlockCopy(mac, 0, hello, payload.Length, mac.Length);
        return hello;
    }

    private static (byte[] ClientNonce, long UnixTimeSeconds, ulong HandshakeCounter, TunnelCipherAlgorithm Algorithm) ValidateAndExtractClientHello(
        byte[] clientHello,
        byte[] macKey)
    {
        var payloadLength = clientHello.Length - MacLength;
        var payload = clientHello.AsSpan(0, payloadLength);
        var mac = clientHello.AsSpan(payloadLength, MacLength);

        var expectedMac = ComputeHmacSha256(macKey, payload);
        if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac))
        {
            throw new InvalidDataException("Invalid client handshake MAC.");
        }

        if (!payload.Slice(0, ClientMagicLength).SequenceEqual(ClientMagic))
        {
            throw new InvalidDataException("Invalid client handshake magic.");
        }

        var algorithm = (TunnelCipherAlgorithm)payload[ClientMagicLength];
        var unixTime = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(ClientMagicLength + AlgorithmLength, TimestampLength));
        var counter = BinaryPrimitives.ReadUInt64BigEndian(
            payload.Slice(ClientMagicLength + AlgorithmLength + TimestampLength, CounterLength));
        var nonce = payload.Slice(ClientMagicLength + AlgorithmLength + TimestampLength + CounterLength, NonceLength).ToArray();
        return (nonce, unixTime, counter, algorithm);
    }

    private static (byte[] ServerNonce, long UnixTimeSeconds, TunnelCipherAlgorithm Algorithm) ValidateAndExtractServerHello(
        byte[] serverHello,
        byte[] macKey,
        byte[] clientHello)
    {
        var payloadLength = serverHello.Length - MacLength;
        var payload = serverHello.AsSpan(0, payloadLength);
        var mac = serverHello.AsSpan(payloadLength, MacLength);

        if (!payload.Slice(0, ServerMagicLength).SequenceEqual(ServerMagic))
        {
            throw new InvalidDataException("Invalid server handshake magic.");
        }

        var macInput = new byte[clientHello.Length + payloadLength];
        Buffer.BlockCopy(clientHello, 0, macInput, 0, clientHello.Length);
        payload.CopyTo(macInput.AsSpan(clientHello.Length));

        var expectedMac = ComputeHmacSha256(macKey, macInput);
        if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac))
        {
            throw new InvalidDataException("Invalid server handshake MAC.");
        }

        var algorithm = (TunnelCipherAlgorithm)payload[ServerMagicLength];
        var unixTime = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(ServerMagicLength + AlgorithmLength, TimestampLength));
        var nonce = payload.Slice(ServerMagicLength + AlgorithmLength + TimestampLength, NonceLength).ToArray();
        return (nonce, unixTime, algorithm);
    }

    private static byte[] ComputeHmacSha256(byte[] key, ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data.ToArray());
    }

    private static byte[] DeriveHandshakeMacKey(byte[] psk)
    {
        var prk = HkdfExtract(Array.Empty<byte>(), psk);
        return HkdfExpand(prk, HandshakeMacInfo, 32);
    }

    private static (byte[] Key, byte[] ClientWriteBaseNonce, byte[] ServerWriteBaseNonce) DeriveTransportMaterial(
        byte[] psk,
        byte[] clientNonce,
        byte[] serverNonce,
        TunnelCipherAlgorithm algorithm)
    {
        var salt = new byte[clientNonce.Length + serverNonce.Length];
        Buffer.BlockCopy(clientNonce, 0, salt, 0, clientNonce.Length);
        Buffer.BlockCopy(serverNonce, 0, salt, clientNonce.Length, serverNonce.Length);
        var prk = HkdfExtract(salt, psk);
        var keyInfo = BuildAlgorithmInfo(TransportKeyInfoPrefix, algorithm);
        var key = HkdfExpand(prk, keyInfo, 32);

        var nonceLength = AeadDuplexStream.GetNonceSize(algorithm);
        var clientNonceInfo = BuildAlgorithmInfo(TransportClientNonceInfoPrefix, algorithm);
        var serverNonceInfo = BuildAlgorithmInfo(TransportServerNonceInfoPrefix, algorithm);
        var clientWriteBaseNonce = HkdfExpand(prk, clientNonceInfo, nonceLength);
        var serverWriteBaseNonce = HkdfExpand(prk, serverNonceInfo, nonceLength);
        return (key, clientWriteBaseNonce, serverWriteBaseNonce);
    }

    private static byte[] BuildAlgorithmInfo(byte[] prefix, TunnelCipherAlgorithm algorithm)
    {
        var info = new byte[prefix.Length + 1];
        Buffer.BlockCopy(prefix, 0, info, 0, prefix.Length);
        info[^1] = (byte)algorithm;
        return info;
    }

    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        var effectiveSalt = salt.Length == 0 ? new byte[32] : salt;
        using var hmac = new HMACSHA256(effectiveSalt);
        return hmac.ComputeHash(ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        var hashLen = 32;
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;

        while (offset < length)
        {
            using var hmac = new HMACSHA256(prk);
            var input = new byte[previous.Length + info.Length + 1];
            Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
            Buffer.BlockCopy(info, 0, input, previous.Length, info.Length);
            input[^1] = counter++;

            previous = hmac.ComputeHash(input);
            var toCopy = Math.Min(hashLen, length - offset);
            Buffer.BlockCopy(previous, 0, output, offset, toCopy);
            offset += toCopy;
        }

        return output;
    }

    private static void ValidateClockSkew(long peerUnixTimeSeconds, int maxClockSkewSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skew = Math.Abs(now - peerUnixTimeSeconds);
        if (skew > maxClockSkewSeconds)
        {
            throw new InvalidDataException($"Handshake clock skew is too large: {skew} seconds.");
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int size, CancellationToken cancellationToken)
    {
        var buffer = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, size - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading crypto handshake data.");
            }

            offset += read;
        }

        return buffer;
    }
}
