namespace SimpleShadowsocks.Protocol.Compression;

internal static class PayloadCompressionCodecFactory
{
    private static readonly IPayloadCompressionCodec Deflate = new DeflatePayloadCompressionCodec();
    private static readonly IPayloadCompressionCodec Gzip = new GzipPayloadCompressionCodec();
    private static readonly IPayloadCompressionCodec Brotli = new BrotliPayloadCompressionCodec();

    public static IPayloadCompressionCodec Resolve(PayloadCompressionAlgorithm algorithm)
    {
        return algorithm switch
        {
            PayloadCompressionAlgorithm.Deflate => Deflate,
            PayloadCompressionAlgorithm.Gzip => Gzip,
            PayloadCompressionAlgorithm.Brotli => Brotli,
            _ => throw new InvalidDataException($"Unsupported compression algorithm: {(byte)algorithm}.")
        };
    }

    public static PayloadCompressionAlgorithm FromFlags(byte flags)
    {
        var encoded = (flags & ProtocolFlags.CompressionAlgorithmMask) >> ProtocolFlags.CompressionAlgorithmShift;
        return encoded switch
        {
            0 => PayloadCompressionAlgorithm.Deflate,
            1 => PayloadCompressionAlgorithm.Gzip,
            2 => PayloadCompressionAlgorithm.Brotli,
            _ => throw new InvalidDataException($"Unsupported compression algorithm id in flags: {encoded}.")
        };
    }

    public static byte ToFlags(PayloadCompressionAlgorithm algorithm)
    {
        return algorithm switch
        {
            PayloadCompressionAlgorithm.Deflate => 0,
            PayloadCompressionAlgorithm.Gzip => (byte)(1 << ProtocolFlags.CompressionAlgorithmShift),
            PayloadCompressionAlgorithm.Brotli => (byte)(2 << ProtocolFlags.CompressionAlgorithmShift),
            _ => throw new InvalidDataException($"Unsupported compression algorithm: {(byte)algorithm}.")
        };
    }
}
