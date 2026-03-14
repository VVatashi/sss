using System.Buffers;

namespace SimpleShadowsocks.Protocol.Compression;

internal abstract class StreamPayloadCompressionCodec : IPayloadCompressionCodec
{
    public abstract PayloadCompressionAlgorithm Algorithm { get; }

    public bool TryCompress(ReadOnlySpan<byte> source, byte[] destination, out int bytesWritten)
    {
        try
        {
            using var output = new PooledBufferWriteStream(destination, destination.Length);
            using (var compressor = CreateCompressionStream(output))
            {
                compressor.Write(source);
            }

            bytesWritten = output.WrittenCount;
            return true;
        }
        catch
        {
            bytesWritten = 0;
            return false;
        }
    }

    public byte[] Decompress(byte[] compressed, int compressedLength, int maxOutputLength)
    {
        var rented = ArrayPool<byte>.Shared.Rent(maxOutputLength);
        try
        {
            using var input = new MemoryStream(compressed, 0, compressedLength, writable: false, publiclyVisible: true);
            using var decompressor = CreateDecompressionStream(input);
            using var output = new PooledBufferWriteStream(rented, maxOutputLength);
            decompressor.CopyTo(output);

            var bytesWritten = output.WrittenCount;
            if (bytesWritten < 0 || bytesWritten > maxOutputLength)
            {
                throw new InvalidDataException("Compressed payload cannot be decompressed.");
            }

            var result = new byte[bytesWritten];
            Buffer.BlockCopy(rented, 0, result, 0, bytesWritten);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    protected abstract Stream CreateCompressionStream(Stream output);
    protected abstract Stream CreateDecompressionStream(Stream input);
}
