using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class Socks5ServerTests
{
    [Fact]
    public async Task UdpAssociate_WithoutTunnelBackend_ReturnsGeneralFailure()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var udpAssociateRequest = TestNetwork.BuildUdpAssociateRequestIPv4(IPAddress.Any, 0);
        await stream.WriteAsync(udpAssociateRequest);
        var associateReply = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x01, associateReply.ReplyCode);
        Assert.NotNull(associateReply.BoundEndPoint);
        Assert.Equal(IPAddress.Any, associateReply.BoundEndPoint!.Address);
        Assert.Equal(0, associateReply.BoundEndPoint.Port);
    }

    [Fact]
    public async Task UdpAssociate_ViaTunnel_RelaysDatagrams()
    {
        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var udpAssociateRequest = TestNetwork.BuildUdpAssociateRequestIPv4(IPAddress.Any, 0);
        await stream.WriteAsync(udpAssociateRequest);
        var associateReply = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x00, associateReply.ReplyCode);
        Assert.NotNull(associateReply.BoundEndPoint);

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.ASCII.GetBytes("udp-through-tunnel");
        var udpPacket = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEcho.Port, payload);
        await udpClient.SendAsync(udpPacket, associateReply.BoundEndPoint!, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var echoed = await udpClient.ReceiveAsync(timeoutCts.Token);
        var parsed = TestNetwork.ParseSocks5UdpDatagram(echoed.Buffer);
        Assert.Equal(IPAddress.Loopback, parsed.SourceAddress);
        Assert.Equal(udpEcho.Port, parsed.SourcePort);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public async Task UdpAssociate_ViaTunnel_RelaysDatagrams_ToDomainTarget()
    {
        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var udpAssociateRequest = TestNetwork.BuildUdpAssociateRequestIPv4(IPAddress.Any, 0);
        await stream.WriteAsync(udpAssociateRequest);
        var associateReply = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x00, associateReply.ReplyCode);
        Assert.NotNull(associateReply.BoundEndPoint);

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.ASCII.GetBytes("udp-domain-target");
        var udpPacket = TestNetwork.BuildSocks5UdpDatagram("localhost", udpEcho.Port, payload);
        await udpClient.SendAsync(udpPacket, associateReply.BoundEndPoint!, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var echoed = await udpClient.ReceiveAsync(timeoutCts.Token);
        var parsed = TestNetwork.ParseSocks5UdpDatagram(echoed.Buffer);
        Assert.Equal(IPAddress.Loopback, parsed.SourceAddress);
        Assert.Equal(udpEcho.Port, parsed.SourcePort);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public async Task UdpAssociate_ViaTunnel_ReassemblesFragmentedDatagrams()
    {
        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var udpAssociateRequest = TestNetwork.BuildUdpAssociateRequestIPv4(IPAddress.Any, 0);
        await stream.WriteAsync(udpAssociateRequest);
        var associateReply = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x00, associateReply.ReplyCode);
        Assert.NotNull(associateReply.BoundEndPoint);

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.ASCII.GetBytes("udp-tunnel-fragmented-payload");
        var part1 = payload.AsSpan(0, 12).ToArray();
        var part2 = payload.AsSpan(12).ToArray();

        var fragment1 = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEcho.Port, part1, fragment: 0x01);
        var fragment2 = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEcho.Port, part2, fragment: 0x82);
        await udpClient.SendAsync(fragment1, associateReply.BoundEndPoint!, CancellationToken.None);
        await udpClient.SendAsync(fragment2, associateReply.BoundEndPoint!, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var echoed = await udpClient.ReceiveAsync(timeoutCts.Token);
        var parsed = TestNetwork.ParseSocks5UdpDatagram(echoed.Buffer);
        Assert.Equal(IPAddress.Loopback, parsed.SourceAddress);
        Assert.Equal(udpEcho.Port, parsed.SourcePort);
        Assert.Equal(payload, parsed.Payload);
    }
}
