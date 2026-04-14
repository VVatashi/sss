using System.Buffers;
using System.Runtime.InteropServices;

namespace SimpleShadowsocks.Protocol;

internal readonly struct ProtocolReadLease : IDisposable
{
    private readonly byte[]? _pooledPayload;

    public ProtocolReadLease(ProtocolFrame frame, byte version, byte flags, byte[]? pooledPayload)
    {
        Frame = frame;
        Version = version;
        Flags = flags;
        _pooledPayload = pooledPayload;
    }

    public ProtocolFrame Frame { get; }
    public byte Version { get; }
    public byte Flags { get; }

    public ProtocolReadResult Materialize()
    {
        return new ProtocolReadResult(
            new ProtocolFrame(Frame.Type, Frame.SessionId, Frame.Sequence, MaterializePayload()),
            Version,
            Flags);
    }

    public void Dispose()
    {
        if (_pooledPayload is not null)
        {
            ArrayPool<byte>.Shared.Return(_pooledPayload);
        }
    }

    private byte[] MaterializePayload()
    {
        if (Frame.Payload.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        if (_pooledPayload is not null)
        {
            return Frame.Payload.ToArray();
        }

        if (MemoryMarshal.TryGetArray(Frame.Payload, out var segment)
            && segment.Array is not null
            && segment.Offset == 0
            && segment.Count == segment.Array.Length)
        {
            return segment.Array;
        }

        return Frame.Payload.ToArray();
    }
}
