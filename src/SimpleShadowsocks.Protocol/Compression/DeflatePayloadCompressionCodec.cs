using System.IO.Compression;

namespace SimpleShadowsocks.Protocol.Compression;

internal sealed class DeflatePayloadCompressionCodec : StreamPayloadCompressionCodec
{
    public override PayloadCompressionAlgorithm Algorithm => PayloadCompressionAlgorithm.Deflate;

    protected override Stream CreateCompressionStream(Stream output)
        => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true);

    protected override Stream CreateDecompressionStream(Stream input)
        => new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
}
