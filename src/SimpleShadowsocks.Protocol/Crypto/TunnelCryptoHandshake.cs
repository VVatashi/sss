using System.Security.Cryptography;
using System.Buffers.Binary;

namespace SimpleShadowsocks.Protocol.Crypto;

public static class TunnelCryptoHandshake
{
    private const int NonceLength = 12;
    private const int MacLength = 32;
    private const int TimestampLength = sizeof(long);
    private const int CounterLength = sizeof(ulong);
    private const int ClientMagicLength = 4;
    private const int ServerMagicLength = 4;
    private static readonly byte[] ClientMagic = "TSC1"u8.ToArray();
    private static readonly byte[] ServerMagic = "TSS1"u8.ToArray();
    private static readonly byte[] HandshakeMacInfo = "ss-v1-handshake-mac"u8.ToArray();
    private static readonly byte[] TransportKeyInfo = "ss-v1-transport-key"u8.ToArray();
    private static long _clientHandshakeCounter;

    public static async Task<ChaCha20Poly1305DuplexStream> AsClientAsync(
        Stream plainStream,
        byte[] key,
        TunnelCryptoPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= TunnelCryptoPolicy.Default;
        var handshakeMacKey = DeriveHandshakeMacKey(key);

        var unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counter = (ulong)Interlocked.Increment(ref _clientHandshakeCounter);
        var clientWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var clientHello = BuildClientHello(handshakeMacKey, unixTimeSeconds, counter, clientWriteNonce);
        await plainStream.WriteAsync(clientHello, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        var serverHello = await ReadExactlyAsync(plainStream, ServerMagicLength + TimestampLength + NonceLength + MacLength, cancellationToken);
        var (serverWriteNonce, serverUnixTimeSeconds) = ValidateAndExtractServerHello(serverHello, handshakeMacKey, clientHello);
        ValidateClockSkew(serverUnixTimeSeconds, policy.HandshakeMaxClockSkewSeconds);

        var transportKey = DeriveTransportKey(key, clientWriteNonce, serverWriteNonce);
        return new ChaCha20Poly1305DuplexStream(
            plainStream,
            transportKey,
            clientWriteNonce,
            serverWriteNonce,
            leaveOpen: true);
    }

    public static async Task<ChaCha20Poly1305DuplexStream> AsServerAsync(
        Stream plainStream,
        byte[] key,
        TunnelCryptoPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= TunnelCryptoPolicy.Default;
        var handshakeMacKey = DeriveHandshakeMacKey(key);

        var clientHello = await ReadExactlyAsync(
            plainStream,
            ClientMagicLength + TimestampLength + CounterLength + NonceLength + MacLength,
            cancellationToken);

        var (clientWriteNonce, clientUnixTimeSeconds, handshakeCounter) = ValidateAndExtractClientHello(clientHello, handshakeMacKey);
        ValidateClockSkew(clientUnixTimeSeconds, policy.HandshakeMaxClockSkewSeconds);

        if (!ReplayProtectionCache.TryRegister(clientWriteNonce, handshakeCounter, policy.ReplayWindowSeconds))
        {
            throw new InvalidDataException("Replay detected for encrypted tunnel handshake.");
        }

        var serverWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var serverUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var serverHello = BuildServerHello(handshakeMacKey, clientHello, serverUnixTimeSeconds, serverWriteNonce);
        await plainStream.WriteAsync(serverHello, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        var transportKey = DeriveTransportKey(key, clientWriteNonce, serverWriteNonce);
        return new ChaCha20Poly1305DuplexStream(
            plainStream,
            transportKey,
            serverWriteNonce,
            clientWriteNonce,
            leaveOpen: true);
    }

    private static byte[] BuildClientHello(byte[] macKey, long unixTimeSeconds, ulong handshakeCounter, byte[] clientNonce)
    {
        var payloadLength = ClientMagicLength + TimestampLength + CounterLength + NonceLength;
        var payload = new byte[payloadLength];
        Buffer.BlockCopy(ClientMagic, 0, payload, 0, ClientMagicLength);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(ClientMagicLength, TimestampLength), unixTimeSeconds);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(ClientMagicLength + TimestampLength, CounterLength), handshakeCounter);
        Buffer.BlockCopy(clientNonce, 0, payload, ClientMagicLength + TimestampLength + CounterLength, NonceLength);

        var mac = ComputeHmacSha256(macKey, payload);
        var hello = new byte[payload.Length + mac.Length];
        Buffer.BlockCopy(payload, 0, hello, 0, payload.Length);
        Buffer.BlockCopy(mac, 0, hello, payload.Length, mac.Length);
        return hello;
    }

    private static byte[] BuildServerHello(byte[] macKey, byte[] clientHello, long unixTimeSeconds, byte[] serverNonce)
    {
        var payload = new byte[ServerMagicLength + TimestampLength + NonceLength];
        Buffer.BlockCopy(ServerMagic, 0, payload, 0, ServerMagicLength);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(ServerMagicLength, TimestampLength), unixTimeSeconds);
        Buffer.BlockCopy(serverNonce, 0, payload, ServerMagicLength + TimestampLength, NonceLength);

        var macInput = new byte[clientHello.Length + payload.Length];
        Buffer.BlockCopy(clientHello, 0, macInput, 0, clientHello.Length);
        Buffer.BlockCopy(payload, 0, macInput, clientHello.Length, payload.Length);

        var mac = ComputeHmacSha256(macKey, macInput);
        var hello = new byte[payload.Length + mac.Length];
        Buffer.BlockCopy(payload, 0, hello, 0, payload.Length);
        Buffer.BlockCopy(mac, 0, hello, payload.Length, mac.Length);
        return hello;
    }

    private static (byte[] ClientNonce, long UnixTimeSeconds, ulong HandshakeCounter) ValidateAndExtractClientHello(
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

        var unixTime = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(ClientMagicLength, TimestampLength));
        var counter = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(ClientMagicLength + TimestampLength, CounterLength));
        var nonce = payload.Slice(ClientMagicLength + TimestampLength + CounterLength, NonceLength).ToArray();
        return (nonce, unixTime, counter);
    }

    private static (byte[] ServerNonce, long UnixTimeSeconds) ValidateAndExtractServerHello(
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

        var unixTime = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(ServerMagicLength, TimestampLength));
        var nonce = payload.Slice(ServerMagicLength + TimestampLength, NonceLength).ToArray();
        return (nonce, unixTime);
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

    private static byte[] DeriveTransportKey(byte[] psk, byte[] clientNonce, byte[] serverNonce)
    {
        var salt = new byte[clientNonce.Length + serverNonce.Length];
        Buffer.BlockCopy(clientNonce, 0, salt, 0, clientNonce.Length);
        Buffer.BlockCopy(serverNonce, 0, salt, clientNonce.Length, serverNonce.Length);
        var prk = HkdfExtract(salt, psk);
        return HkdfExpand(prk, TransportKeyInfo, 32);
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
