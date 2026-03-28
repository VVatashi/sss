using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Socks5;

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

    [Fact]
    public async Task UdpAssociate_WithDirectRoutingRule_RelaysDatagramsWithoutTunnel()
    {
        var routingPolicy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Subnet,
                Match = "127.0.0.0/8",
                Decision = TrafficRouteDecision.Direct
            },
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Any,
                Match = "*",
                Decision = TrafficRouteDecision.Tunnel
            }
        ]);

        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port, routingPolicy: routingPolicy);
        var acceptedBefore = tunnel.Server.AcceptedTunnelConnections;
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
        var payload = Encoding.ASCII.GetBytes("udp-direct-with-routing-rule");
        var udpPacket = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEcho.Port, payload);
        await udpClient.SendAsync(udpPacket, associateReply.BoundEndPoint!, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var echoed = await udpClient.ReceiveAsync(timeoutCts.Token);
        var parsed = TestNetwork.ParseSocks5UdpDatagram(echoed.Buffer);
        Assert.Equal(IPAddress.Loopback, parsed.SourceAddress);
        Assert.Equal(udpEcho.Port, parsed.SourcePort);
        Assert.Equal(payload, parsed.Payload);
        Assert.Equal(acceptedBefore, tunnel.Server.AcceptedTunnelConnections);
    }

    [Fact]
    public async Task UdpAssociate_WithoutMatchingRoutingRule_DropsDatagram()
    {
        var routingPolicy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Host,
                Match = "*.example.com",
                Decision = TrafficRouteDecision.Tunnel
            }
        ]);

        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync(routingPolicy);
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
        var payload = Encoding.ASCII.GetBytes("udp-no-match-should-drop");
        var udpPacket = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEcho.Port, payload);
        await udpClient.SendAsync(udpPacket, associateReply.BoundEndPoint!, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await udpClient.ReceiveAsync(timeoutCts.Token));
    }

    [Fact]
    public async Task UdpAssociate_WithDropRoutingRule_DropsDatagram()
    {
        var routingPolicy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Subnet,
                Match = "127.0.0.0/8",
                Decision = TrafficRouteDecision.Drop
            },
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Any,
                Match = "*",
                Decision = TrafficRouteDecision.Tunnel
            }
        ]);

        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port, routingPolicy: routingPolicy);
        var acceptedBefore = tunnel.Server.AcceptedTunnelConnections;
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
        var payload = Encoding.ASCII.GetBytes("udp-drop-routing-rule");
        var udpPacket = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEcho.Port, payload);
        await udpClient.SendAsync(udpPacket, associateReply.BoundEndPoint!, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await udpClient.ReceiveAsync(timeoutCts.Token));
        Assert.Equal(acceptedBefore, tunnel.Server.AcceptedTunnelConnections);
    }

    [Fact]
    public async Task UdpAssociate_WithUsernamePasswordAuthentication_RelaysDatagramsAfterSuccessfulAuthentication()
    {
        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(
            tunnel.Port,
            authenticationOptions: new Socks5AuthenticationOptions("local-user", "local-pass"));
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        var greetingResponse = await TestNetwork.SendSocks5GreetingAsync(stream, 0x00, 0x02);
        Assert.Equal(new byte[] { 0x05, 0x02 }, greetingResponse);

        var authResponse = await TestNetwork.SendUsernamePasswordAuthAsync(stream, "local-user", "local-pass");
        Assert.Equal(new byte[] { 0x01, 0x00 }, authResponse);

        var udpAssociateRequest = TestNetwork.BuildUdpAssociateRequestIPv4(IPAddress.Any, 0);
        await stream.WriteAsync(udpAssociateRequest);
        var associateReply = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x00, associateReply.ReplyCode);
        Assert.NotNull(associateReply.BoundEndPoint);

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.ASCII.GetBytes("udp-through-authenticated-socks");
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
    public async Task UdpAssociate_WithUsernamePasswordAuthentication_ClosesSessionAfterFailedAuthentication()
    {
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(
            tunnel.Port,
            authenticationOptions: new Socks5AuthenticationOptions("local-user", "local-pass"));
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        var greetingResponse = await TestNetwork.SendSocks5GreetingAsync(stream, 0x02);
        Assert.Equal(new byte[] { 0x05, 0x02 }, greetingResponse);

        var authResponse = await TestNetwork.SendUsernamePasswordAuthAsync(stream, "local-user", "wrong-pass");
        Assert.Equal(new byte[] { 0x01, 0x01 }, authResponse);

        var udpAssociateRequest = TestNetwork.BuildUdpAssociateRequestIPv4(IPAddress.Any, 0);
        await stream.WriteAsync(udpAssociateRequest);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var nextByte = new byte[1];
        try
        {
            var read = await stream.ReadAsync(nextByte, timeoutCts.Token);
            Assert.Equal(0, read);
        }
        catch (IOException)
        {
            // A hard socket close is also an acceptable outcome after failed auth.
        }
    }
}
