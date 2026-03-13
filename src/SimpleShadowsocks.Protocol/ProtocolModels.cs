namespace SimpleShadowsocks.Protocol;

public static class ProtocolConstants
{
    public const byte Version = 1;
    public const int HeaderSize = 10;
    public const int MaxPayloadLength = 1024 * 1024;
}

public enum FrameType : byte
{
    Connect = 1,
    Data = 2,
    Close = 3,
    Ping = 4,
    Pong = 5
}

public enum AddressType : byte
{
    IPv4 = 1,
    Domain = 3,
    IPv6 = 4
}

public readonly record struct ConnectRequest(AddressType AddressType, string Address, ushort Port);
public readonly record struct DataChunk(uint SessionId, ReadOnlyMemory<byte> Payload);
public readonly record struct ProtocolFrame(FrameType Type, uint SessionId, ReadOnlyMemory<byte> Payload);
