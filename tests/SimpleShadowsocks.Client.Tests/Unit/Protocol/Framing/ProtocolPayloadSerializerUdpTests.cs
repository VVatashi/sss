using System.Runtime.InteropServices;
using System.Text;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class ProtocolPayloadSerializerUdpTests
{
    [Fact]
    public void UdpDatagram_RoundTrip_PreservesEndpointAndPayload()
    {
        var payload = Encoding.ASCII.GetBytes("udp-payload");
        var datagram = new UdpDatagram(AddressType.Domain, "example.com", 5353, payload);

        var serialized = ProtocolPayloadSerializer.SerializeUdpDatagram(datagram);
        var parsed = ProtocolPayloadSerializer.DeserializeUdpDatagram(serialized.AsSpan());

        Assert.Equal(datagram.AddressType, parsed.AddressType);
        Assert.Equal(datagram.Address, parsed.Address);
        Assert.Equal(datagram.Port, parsed.Port);
        Assert.Equal(payload, parsed.Payload.ToArray());
    }

    [Fact]
    public void UdpDatagram_MemoryParse_ReusesOriginalPayloadBuffer()
    {
        var payload = Encoding.ASCII.GetBytes("udp-payload");
        var datagram = new UdpDatagram(AddressType.Domain, "example.com", 5353, payload);
        var serialized = ProtocolPayloadSerializer.SerializeUdpDatagram(datagram);
        var wrapped = new byte[serialized.Length + 8];
        serialized.CopyTo(wrapped.AsMemory(3));

        var parsed = ProtocolPayloadSerializer.DeserializeUdpDatagram(wrapped.AsMemory(3, serialized.Length));

        Assert.Equal(datagram.AddressType, parsed.AddressType);
        Assert.Equal(datagram.Address, parsed.Address);
        Assert.Equal(datagram.Port, parsed.Port);
        Assert.True(MemoryMarshal.TryGetArray(parsed.Payload, out var segment));
        Assert.Same(wrapped, segment.Array);
        Assert.Equal(3 + ProtocolPayloadSerializer.SerializeConnectRequest(new ConnectRequest(AddressType.Domain, "example.com", 5353)).Length, segment.Offset);
        Assert.Equal(payload.Length, segment.Count);
        Assert.True(parsed.Payload.Span.SequenceEqual(payload));
    }
}
