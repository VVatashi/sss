namespace SimpleShadowsocks.Protocol.Compression;

internal interface IPayloadCompressionCodec
{
    PayloadCompressionAlgorithm Algorithm { get; }
    bool TryCompress(ReadOnlySpan<byte> source, byte[] destination, out int bytesWritten);
    byte[] Decompress(byte[] compressed, int compressedLength, int maxOutputLength);
}
