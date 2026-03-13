using System.Net;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tests;

public sealed class ProtocolFrameCodecTests
{
    [Fact]
    public async Task FrameCodec_RoundTrip_PreservesFields()
    {
        var frame = new ProtocolFrame(
            FrameType.Data,
            SessionId: 42,
            Sequence: 7,
            Payload: new byte[] { 1, 2, 3, 4, 5 });

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, frame);

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadAsync(stream);

        Assert.True(decoded.HasValue);
        Assert.Equal(frame.Type, decoded.Value.Type);
        Assert.Equal(frame.SessionId, decoded.Value.SessionId);
        Assert.Equal(frame.Sequence, decoded.Value.Sequence);
        Assert.Equal(frame.Payload.ToArray(), decoded.Value.Payload.ToArray());
    }

    [Fact]
    public async Task FrameCodec_AtEndOfStream_ReturnsNull()
    {
        await using var stream = new MemoryStream();
        var decoded = await ProtocolFrameCodec.ReadAsync(stream);
        Assert.Null(decoded);
    }

    [Fact]
    public async Task FrameCodec_InvalidVersion_ThrowsInvalidDataException()
    {
        var raw = new byte[ProtocolConstants.HeaderSize];
        raw[0] = 99;
        raw[1] = (byte)FrameType.Ping;

        await using var stream = new MemoryStream(raw);
        await Assert.ThrowsAsync<InvalidDataException>(async () => _ = await ProtocolFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public void ConnectPayload_Domain_RoundTrip()
    {
        var request = new ConnectRequest(AddressType.Domain, "example.org", 443);
        var payload = ProtocolPayloadSerializer.SerializeConnectRequest(request);
        var decoded = ProtocolPayloadSerializer.DeserializeConnectRequest(payload);

        Assert.Equal(request.AddressType, decoded.AddressType);
        Assert.Equal(request.Address, decoded.Address);
        Assert.Equal(request.Port, decoded.Port);
    }

    [Fact]
    public void ConnectPayload_IPv6_RoundTrip()
    {
        var request = new ConnectRequest(AddressType.IPv6, "::1", 8443);
        var payload = ProtocolPayloadSerializer.SerializeConnectRequest(request);
        var decoded = ProtocolPayloadSerializer.DeserializeConnectRequest(payload);

        Assert.Equal(request.AddressType, decoded.AddressType);
        Assert.Equal(IPAddress.Parse(request.Address), IPAddress.Parse(decoded.Address));
        Assert.Equal(request.Port, decoded.Port);
    }

    [Fact]
    public void HeartbeatPayload_RoundTrip()
    {
        const ulong nonce = 0x0102030405060708;
        var payload = ProtocolPayloadSerializer.SerializeHeartbeat(nonce);
        var decoded = ProtocolPayloadSerializer.DeserializeHeartbeat(payload);
        Assert.Equal(nonce, decoded);
    }

    [Fact]
    public void ClosePayload_InvalidLength_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ProtocolPayloadSerializer.DeserializeClose([1, 2]));
    }
}
