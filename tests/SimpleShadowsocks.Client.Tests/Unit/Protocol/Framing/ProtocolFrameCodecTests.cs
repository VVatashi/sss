using System.Net;
using System.Runtime.InteropServices;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
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
            Version = ProtocolConstants.Version2,
            EnableCompression = true,
            CompressionMinBytes = 64,
            CompressionMinSavingsBytes = 1
        });

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);
        Assert.True(decoded.HasValue);
        Assert.Equal(ProtocolConstants.Version2, decoded.Value.Version);
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
            Version = ProtocolConstants.Version2,
            EnableCompression = true,
            CompressionAlgorithm = algorithm,
            CompressionMinBytes = 64,
            CompressionMinSavingsBytes = 1
        });

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);
        Assert.True(decoded.HasValue);
        Assert.Equal(ProtocolConstants.Version2, decoded.Value.Version);
        Assert.True((decoded.Value.Flags & ProtocolFlags.CompressionEnabled) != 0);
        Assert.Equal(algorithm, ProtocolFrameCodec.GetCompressionAlgorithm(decoded.Value.Flags));
        Assert.Equal(payload, decoded.Value.Frame.Payload.ToArray());
    }

    [Fact]
    public async Task FrameCodec_LeasedRead_UncompressedPayload_PreservesFields()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(new string('C', 1024));
        var frame = new ProtocolFrame(FrameType.Data, 9, 15, payload);

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, frame, default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = false
        });

        stream.Position = 0;
        var leased = await ProtocolFrameCodec.ReadDetailedLeasedAsync(stream);

        Assert.NotNull(leased);
        using var lease = leased;
        Assert.Equal(ProtocolConstants.Version, lease.Version);
        Assert.Equal(frame.Type, lease.Frame.Type);
        Assert.Equal(frame.SessionId, lease.Frame.SessionId);
        Assert.Equal(frame.Sequence, lease.Frame.Sequence);
        Assert.True(MemoryMarshal.TryGetArray(lease.Frame.Payload, out var segment));
        Assert.NotNull(segment.Array);
        Assert.Equal(payload.Length, segment.Count);

        var materialized = lease.Materialize();
        Assert.Equal(payload, materialized.Frame.Payload.ToArray());
    }

    [Fact]
    public async Task FrameCodec_LeasedRead_CompressedPayload_PreservesFields()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(new string('D', 4096));
        var frame = new ProtocolFrame(FrameType.Data, 10, 16, payload);

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, frame, default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = true,
            CompressionMinBytes = 64,
            CompressionMinSavingsBytes = 1
        });

        stream.Position = 0;
        var leased = await ProtocolFrameCodec.ReadDetailedLeasedAsync(stream);

        Assert.NotNull(leased);
        using var lease = leased;
        Assert.Equal(ProtocolConstants.Version, lease.Version);
        Assert.Equal(payload, lease.Frame.Payload.ToArray());
    }

    [Fact]
    public async Task FrameCodec_LeasedRead_TransferPayload_PreservesData()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(new string('E', 1024));

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, new ProtocolFrame(FrameType.Data, 11, 17, payload), default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = false
        });

        stream.Position = 0;
        var lease = await ProtocolFrameCodec.ReadDetailedLeasedAsync(stream);

        Assert.NotNull(lease);
        using (lease)
        {
            var transferred = lease.TransferPayload();
            using (transferred)
            {
                Assert.Equal(payload, transferred.Memory.ToArray());
                var materialized = lease.Materialize();
                Assert.Equal(payload, materialized.Frame.Payload.ToArray());
            }
        }
    }

    [Fact]
    public async Task FrameCodec_LeasedRead_TransferPayload_CompressedPayload_PreservesData()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(new string('F', 4096));

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, new ProtocolFrame(FrameType.Data, 12, 18, payload), default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = true,
            CompressionMinBytes = 64,
            CompressionMinSavingsBytes = 1
        });

        stream.Position = 0;
        var lease = await ProtocolFrameCodec.ReadDetailedLeasedAsync(stream);

        Assert.NotNull(lease);
        using (lease)
        {
            var transferred = lease.TransferPayload();
            using (transferred)
            {
                Assert.Equal(payload, transferred.Memory.ToArray());
            }
        }
    }

    [Fact]
    public void HttpRequestPayload_RoundTrip()
    {
        var request = new HttpRequestStart(
            "POST",
            "http",
            "example.org:8080",
            "/api/items?id=42",
            1,
            1,
            [new HttpHeader("Host", "example.org:8080"), new HttpHeader("User-Agent", "tests")]);

        var payload = ProtocolPayloadSerializer.SerializeHttpRequestStart(request);
        var decoded = ProtocolPayloadSerializer.DeserializeHttpRequestStart(payload);

        Assert.Equal(request.Method, decoded.Method);
        Assert.Equal(request.Scheme, decoded.Scheme);
        Assert.Equal(request.Authority, decoded.Authority);
        Assert.Equal(request.PathAndQuery, decoded.PathAndQuery);
        Assert.Equal(request.VersionMajor, decoded.VersionMajor);
        Assert.Equal(request.VersionMinor, decoded.VersionMinor);
        Assert.Equal(request.Headers, decoded.Headers);
    }

    [Fact]
    public void HttpResponsePayload_RoundTrip()
    {
        var response = new HttpResponseStart(
            201,
            "Created",
            1,
            1,
            [new HttpHeader("Content-Type", "application/json"), new HttpHeader("ETag", "\"abc\"")]);

        var payload = ProtocolPayloadSerializer.SerializeHttpResponseStart(response);
        var decoded = ProtocolPayloadSerializer.DeserializeHttpResponseStart(payload);

        Assert.Equal(response.StatusCode, decoded.StatusCode);
        Assert.Equal(response.ReasonPhrase, decoded.ReasonPhrase);
        Assert.Equal(response.VersionMajor, decoded.VersionMajor);
        Assert.Equal(response.VersionMinor, decoded.VersionMinor);
        Assert.Equal(response.Headers, decoded.Headers);
    }

    [Fact]
    public async Task FrameCodec_ReverseHttpRequest_RoundTrip()
    {
        var request = new HttpRequestStart(
            "GET",
            "http",
            "app.local",
            "/hello",
            1,
            1,
            [new HttpHeader("Host", "app.local")]);
        var frame = new ProtocolFrame(
            FrameType.ReverseHttpRequest,
            SessionId: 0x80000001,
            Sequence: 0,
            Payload: ProtocolPayloadSerializer.SerializeHttpRequestStart(request));

        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, frame, default, new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version,
            EnableCompression = false
        });

        stream.Position = 0;
        var decoded = await ProtocolFrameCodec.ReadDetailedAsync(stream);

        Assert.True(decoded.HasValue);
        Assert.Equal(FrameType.ReverseHttpRequest, decoded.Value.Frame.Type);
        var decodedRequest = ProtocolPayloadSerializer.DeserializeHttpRequestStart(decoded.Value.Frame.Payload.Span);
        Assert.Equal(request.Method, decodedRequest.Method);
        Assert.Equal(request.Scheme, decodedRequest.Scheme);
        Assert.Equal(request.Authority, decodedRequest.Authority);
        Assert.Equal(request.PathAndQuery, decodedRequest.PathAndQuery);
        Assert.Equal(request.VersionMajor, decodedRequest.VersionMajor);
        Assert.Equal(request.VersionMinor, decodedRequest.VersionMinor);
        Assert.Equal(request.Headers, decodedRequest.Headers);
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
