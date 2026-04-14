namespace SimpleShadowsocks.Protocol;

public static class ProtocolConstants
{
    public const byte LegacyVersion = 1;
    public const byte Version2 = 2;
    public const byte Version3 = 3;
    public const byte Version = 4;
    public const int HeaderSizeV1 = 18;
    public const int HeaderSizeV2 = 19;
    public const int HeaderSize = HeaderSizeV2;
    public const int MaxPayloadLength = 1024 * 1024;

    public static bool IsSupported(byte version) => version is LegacyVersion or Version2 or Version3 or Version;
    public static bool UsesExtendedHeader(byte version) => version is Version2 or Version3 or Version;
    public static bool SupportsCompression(byte version) => version is Version2 or Version3 or Version;
    public static bool SupportsHttpRelay(byte version) => version is Version3 or Version;
    public static bool SupportsSelectiveRecovery(byte version) => version == Version;
}

public static class ProtocolFlags
{
    public const byte None = 0x00;
    public const byte PayloadCompressed = 0x01;
    public const byte CompressionEnabled = 0x02;
    public const byte CompressionAlgorithmMask = 0x0C;
    public const int CompressionAlgorithmShift = 2;
}

public enum FrameType : byte
{
    Connect = 1,
    Data = 2,
    Close = 3,
    Ping = 4,
    Pong = 5,
    UdpAssociate = 6,
    UdpData = 7,
    HttpRequest = 8,
    HttpRequestEnd = 9,
    HttpResponse = 10,
    ReverseHttpRequest = 11,
    ReverseHttpRequestEnd = 12,
    ReverseHttpResponse = 13,
    Ack = 14,
    Recover = 15
}

public enum AddressType : byte
{
    IPv4 = 1,
    Domain = 3,
    IPv6 = 4
}

public readonly record struct ConnectRequest(AddressType AddressType, string Address, ushort Port);
public readonly record struct DataChunk(uint SessionId, ReadOnlyMemory<byte> Payload);
public readonly record struct UdpDatagram(AddressType AddressType, string Address, ushort Port, ReadOnlyMemory<byte> Payload);
public readonly record struct ProtocolFrame(FrameType Type, uint SessionId, ulong Sequence, ReadOnlyMemory<byte> Payload);
public readonly record struct ProtocolReadResult(ProtocolFrame Frame, byte Version, byte Flags);
public readonly record struct HttpHeader(string Name, string Value);
public readonly record struct HttpRequestStart(
    string Method,
    string Scheme,
    string Authority,
    string PathAndQuery,
    byte VersionMajor,
    byte VersionMinor,
    IReadOnlyList<HttpHeader> Headers);
public readonly record struct HttpResponseStart(
    ushort StatusCode,
    string ReasonPhrase,
    byte VersionMajor,
    byte VersionMinor,
    IReadOnlyList<HttpHeader> Headers);

public enum PayloadCompressionAlgorithm : byte
{
    Deflate = 0,
    Gzip = 1,
    Brotli = 2
}

public sealed class ProtocolWriteOptions
{
    public static ProtocolWriteOptions V2NoCompression { get; } = new()
    {
        Version = ProtocolConstants.Version,
        EnableCompression = false
    };

    public byte Version { get; init; } = ProtocolConstants.Version;
    public bool EnableCompression { get; init; }
    public PayloadCompressionAlgorithm CompressionAlgorithm { get; init; } = PayloadCompressionAlgorithm.Deflate;
    public int CompressionMinBytes { get; init; } = 256;
    public int CompressionMinSavingsBytes { get; init; } = 16;
}
