using System.Security.Cryptography;
using System.Buffers.Binary;

namespace SimpleShadowsocks.Protocol.Crypto;

public static class TunnelCryptoHandshake
{
    private const int NonceLength = 12;
    private const int MacLength = 32;
    private const int ClientMagicLength = 4;
    private const int ServerMagicLength = 4;
    private static readonly byte[] ClientMagic = "TSC1"u8.ToArray();
    private static readonly byte[] ServerMagic = "TSS1"u8.ToArray();
    private static long _clientHandshakeCounter;

    public static async Task<ChaCha20Poly1305DuplexStream> AsClientAsync(
        Stream plainStream,
        byte[] key,
        TunnelCryptoPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= TunnelCryptoPolicy.Default;

        var unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counter = (ulong)Interlocked.Increment(ref _clientHandshakeCounter);
        var clientWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var clientHello = BuildClientHello(key, unixTimeSeconds, counter, clientWriteNonce);
        await plainStream.WriteAsync(clientHello, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        var serverHello = await ReadExactlyAsync(plainStream, ServerMagicLength + NonceLength + MacLength, cancellationToken);
        var serverWriteNonce = ValidateAndExtractServerNonce(serverHello, key, clientHello);

        ValidateClockSkew(unixTimeSeconds, policy.HandshakeMaxClockSkewSeconds);
        return new ChaCha20Poly1305DuplexStream(plainStream, key, clientWriteNonce, serverWriteNonce, leaveOpen: true);
    }

    public static async Task<ChaCha20Poly1305DuplexStream> AsServerAsync(
        Stream plainStream,
        byte[] key,
        TunnelCryptoPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= TunnelCryptoPolicy.Default;

        var clientHello = await ReadExactlyAsync(
            plainStream,
            ClientMagicLength + sizeof(long) + sizeof(ulong) + NonceLength + MacLength,
            cancellationToken);

        var (clientWriteNonce, clientUnixTimeSeconds, handshakeCounter) = ValidateAndExtractClientHello(clientHello, key);
        ValidateClockSkew(clientUnixTimeSeconds, policy.HandshakeMaxClockSkewSeconds);

        if (!ReplayProtectionCache.TryRegister(clientWriteNonce, handshakeCounter, policy.ReplayWindowSeconds))
        {
            throw new InvalidDataException("Replay detected for encrypted tunnel handshake.");
        }

        var serverWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var serverHello = BuildServerHello(key, clientHello, serverWriteNonce);
        await plainStream.WriteAsync(serverHello, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        return new ChaCha20Poly1305DuplexStream(plainStream, key, serverWriteNonce, clientWriteNonce, leaveOpen: true);
    }

    private static byte[] BuildClientHello(byte[] key, long unixTimeSeconds, ulong handshakeCounter, byte[] clientNonce)
    {
        var payloadLength = ClientMagicLength + sizeof(long) + sizeof(ulong) + NonceLength;
        var payload = new byte[payloadLength];
        Buffer.BlockCopy(ClientMagic, 0, payload, 0, ClientMagicLength);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(ClientMagicLength, sizeof(long)), unixTimeSeconds);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(ClientMagicLength + sizeof(long), sizeof(ulong)), handshakeCounter);
        Buffer.BlockCopy(clientNonce, 0, payload, ClientMagicLength + sizeof(long) + sizeof(ulong), NonceLength);

        var mac = ComputeHmacSha256(key, payload);
        var hello = new byte[payload.Length + mac.Length];
        Buffer.BlockCopy(payload, 0, hello, 0, payload.Length);
        Buffer.BlockCopy(mac, 0, hello, payload.Length, mac.Length);
        return hello;
    }

    private static byte[] BuildServerHello(byte[] key, byte[] clientHello, byte[] serverNonce)
    {
        var payload = new byte[ServerMagicLength + NonceLength];
        Buffer.BlockCopy(ServerMagic, 0, payload, 0, ServerMagicLength);
        Buffer.BlockCopy(serverNonce, 0, payload, ServerMagicLength, NonceLength);

        var macInput = new byte[clientHello.Length + payload.Length];
        Buffer.BlockCopy(clientHello, 0, macInput, 0, clientHello.Length);
        Buffer.BlockCopy(payload, 0, macInput, clientHello.Length, payload.Length);

        var mac = ComputeHmacSha256(key, macInput);
        var hello = new byte[payload.Length + mac.Length];
        Buffer.BlockCopy(payload, 0, hello, 0, payload.Length);
        Buffer.BlockCopy(mac, 0, hello, payload.Length, mac.Length);
        return hello;
    }

    private static (byte[] ClientNonce, long UnixTimeSeconds, ulong HandshakeCounter) ValidateAndExtractClientHello(
        byte[] clientHello,
        byte[] key)
    {
        var payloadLength = clientHello.Length - MacLength;
        var payload = clientHello.AsSpan(0, payloadLength);
        var mac = clientHello.AsSpan(payloadLength, MacLength);

        var expectedMac = ComputeHmacSha256(key, payload);
        if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac))
        {
            throw new InvalidDataException("Invalid client handshake MAC.");
        }

        if (!payload.Slice(0, ClientMagicLength).SequenceEqual(ClientMagic))
        {
            throw new InvalidDataException("Invalid client handshake magic.");
        }

        var unixTime = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(ClientMagicLength, sizeof(long)));
        var counter = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(ClientMagicLength + sizeof(long), sizeof(ulong)));
        var nonce = payload.Slice(ClientMagicLength + sizeof(long) + sizeof(ulong), NonceLength).ToArray();
        return (nonce, unixTime, counter);
    }

    private static byte[] ValidateAndExtractServerNonce(byte[] serverHello, byte[] key, byte[] clientHello)
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

        var expectedMac = ComputeHmacSha256(key, macInput);
        if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac))
        {
            throw new InvalidDataException("Invalid server handshake MAC.");
        }

        return payload.Slice(ServerMagicLength, NonceLength).ToArray();
    }

    private static byte[] ComputeHmacSha256(byte[] key, ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data.ToArray());
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
