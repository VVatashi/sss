using System.Security.Cryptography;

namespace SimpleShadowsocks.Protocol.Crypto;

public static class TunnelCryptoHandshake
{
    private const int NonceLength = 12;

    public static async Task<ChaCha20DuplexStream> AsClientAsync(
        Stream plainStream,
        byte[] key,
        CancellationToken cancellationToken = default)
    {
        var clientWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        await plainStream.WriteAsync(clientWriteNonce, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        var serverWriteNonce = await ReadExactlyAsync(plainStream, NonceLength, cancellationToken);
        return new ChaCha20DuplexStream(plainStream, key, clientWriteNonce, serverWriteNonce, leaveOpen: true);
    }

    public static async Task<ChaCha20DuplexStream> AsServerAsync(
        Stream plainStream,
        byte[] key,
        CancellationToken cancellationToken = default)
    {
        var clientWriteNonce = await ReadExactlyAsync(plainStream, NonceLength, cancellationToken);
        var serverWriteNonce = RandomNumberGenerator.GetBytes(NonceLength);
        await plainStream.WriteAsync(serverWriteNonce, cancellationToken);
        await plainStream.FlushAsync(cancellationToken);

        return new ChaCha20DuplexStream(plainStream, key, serverWriteNonce, clientWriteNonce, leaveOpen: true);
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
