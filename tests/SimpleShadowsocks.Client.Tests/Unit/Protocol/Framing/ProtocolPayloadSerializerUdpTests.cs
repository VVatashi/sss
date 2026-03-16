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
        var parsed = ProtocolPayloadSerializer.DeserializeUdpDatagram(serialized);

        Assert.Equal(datagram.AddressType, parsed.AddressType);
        Assert.Equal(datagram.Address, parsed.Address);
        Assert.Equal(datagram.Port, parsed.Port);
        Assert.Equal(payload, parsed.Payload.ToArray());
    }
}
