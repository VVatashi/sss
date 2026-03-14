using System.IO.Compression;

namespace SimpleShadowsocks.Protocol.Compression;

internal sealed class GzipPayloadCompressionCodec : StreamPayloadCompressionCodec
{
    public override PayloadCompressionAlgorithm Algorithm => PayloadCompressionAlgorithm.Gzip;

    protected override Stream CreateCompressionStream(Stream output)
        => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);

    protected override Stream CreateDecompressionStream(Stream input)
        => new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
}
