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
        await ProtocolFrameCodec.WriteAsync(stream, frame, default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = false
        });

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);

        Assert.True(decoded.HasValue);
        Assert.Equal(ProtocolConstants.Version, decoded.Value.Version);
        Assert.Equal(frame.Type, decoded.Value.Frame.Type);
        Assert.Equal(frame.SessionId, decoded.Value.Frame.SessionId);
        Assert.Equal(frame.Sequence, decoded.Value.Frame.Sequence);
        Assert.Equal(frame.Payload.ToArray(), decoded.Value.Frame.Payload.ToArray());
    }

    [Fact]
    public async Task FrameCodec_AtEndOfStream_ReturnsNull()
    {
        await using var stream = new MemoryStream();
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);
        Assert.Null(decoded);
    }

    [Fact]
    public async Task FrameCodec_InvalidVersion_ThrowsInvalidDataException()
    {
        var raw = new byte[ProtocolConstants.HeaderSizeV2];
        raw[0] = 99;
        raw[1] = (byte)FrameType.Ping;

        await using var stream = new MemoryStream(raw);
        await Assert.ThrowsAsync<InvalidDataException>(async () => _ = await ProtocolFrameCodec.ReadDetailedAsync(stream));
    }

    [Fact]
    public async Task FrameCodec_InvalidFrameType_ThrowsInvalidDataException()
    {
        var raw = new byte[ProtocolConstants.HeaderSizeV2];
        raw[0] = ProtocolConstants.Version;
        raw[1] = 0xFF;

        await using var stream = new MemoryStream(raw);
        await Assert.ThrowsAsync<InvalidDataException>(async () => _ = await ProtocolFrameCodec.ReadDetailedAsync(stream));
    }

    [Fact]
    public async Task FrameCodec_LegacyV1_RoundTrip_IsSupported()
    {
        var frame = new ProtocolFrame(FrameType.Ping, 1, 2, new byte[] { 9, 8, 7 });
        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, frame, default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.LegacyVersion,
            EnableCompression = false
        });

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);
        Assert.True(decoded.HasValue);
        Assert.Equal(ProtocolConstants.LegacyVersion, decoded.Value.Version);
        Assert.Equal(frame.Payload.ToArray(), decoded.Value.Frame.Payload.ToArray());
    }

    [Fact]
    public async Task FrameCodec_V2_WithCompression_RoundTrip()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(new string('A', 4096));
        var frame = new ProtocolFrame(FrameType.Data, 5, 10, payload);

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, frame, default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = true,
            CompressionMinBytes = 64,
            CompressionMinSavingsBytes = 1
        });

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);
        Assert.True(decoded.HasValue);
        Assert.Equal(ProtocolConstants.Version, decoded.Value.Version);
        Assert.True((decoded.Value.Flags & ProtocolFlags.CompressionEnabled) != 0);
        Assert.Equal(payload, decoded.Value.Frame.Payload.ToArray());
    }

    [Theory]
    [InlineData(PayloadCompressionAlgorithm.Deflate)]
    [InlineData(PayloadCompressionAlgorithm.Gzip)]
    [InlineData(PayloadCompressionAlgorithm.Brotli)]
    public async Task FrameCodec_V2_WithCompressionAlgorithm_RoundTrip(PayloadCompressionAlgorithm algorithm)
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(new string('B', 4096));
        var frame = new ProtocolFrame(FrameType.Data, 7, 11, payload);

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, frame, default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = true,
            CompressionAlgorithm = algorithm,
            CompressionMinBytes = 64,
            CompressionMinSavingsBytes = 1
        });

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);
        Assert.True(decoded.HasValue);
        Assert.Equal(ProtocolConstants.Version, decoded.Value.Version);
        Assert.True((decoded.Value.Flags & ProtocolFlags.CompressionEnabled) != 0);
        Assert.Equal(algorithm, ProtocolFrameCodec.GetCompressionAlgorithm(decoded.Value.Flags));
        Assert.Equal(payload, decoded.Value.Frame.Payload.ToArray());
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
