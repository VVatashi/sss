using System.IO.Compression;

namespace SimpleShadowsocks.Protocol.Compression;

internal sealed class BrotliPayloadCompressionCodec : StreamPayloadCompressionCodec
{
    public override PayloadCompressionAlgorithm Algorithm => PayloadCompressionAlgorithm.Brotli;

    protected override Stream CreateCompressionStream(Stream output)
        => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true);

    protected override Stream CreateDecompressionStream(Stream input)
        => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);
}
