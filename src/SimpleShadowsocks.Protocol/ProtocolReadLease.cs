namespace SimpleShadowsocks.Protocol;

internal sealed class ProtocolReadLease : IDisposable
{
    private OwnedPayloadChunk? _ownedPayload;

    public ProtocolReadLease(ProtocolFrame frame, byte version, byte flags, OwnedPayloadChunk? ownedPayload)
    {
        Frame = frame;
        Version = version;
        Flags = flags;
        _ownedPayload = ownedPayload;
    }

    public ProtocolFrame Frame { get; }
    public byte Version { get; }
    public byte Flags { get; }

    public OwnedPayloadChunk TransferPayload()
    {
        if (Frame.Payload.IsEmpty)
        {
            return OwnedPayloadChunk.Empty;
        }

        if (_ownedPayload is not null)
        {
            var ownedPayload = _ownedPayload;
            _ownedPayload = null;
            return ownedPayload;
        }

        return OwnedPayloadChunk.CopyFrom(Frame.Payload);
    }

    public ProtocolReadResult Materialize()
    {
        return new ProtocolReadResult(
            new ProtocolFrame(Frame.Type, Frame.SessionId, Frame.Sequence, MaterializePayload()),
            Version,
            Flags);
    }

    public void Dispose()
    {
        _ownedPayload?.Dispose();
        _ownedPayload = null;
    }

    private byte[] MaterializePayload()
    {
        if (Frame.Payload.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        return Frame.Payload.ToArray();
    }
}
