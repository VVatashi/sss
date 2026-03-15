using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class TunnelIntegrationTests
{
    [Fact]
    public async Task Socks5Client_MultiplexesMultipleSessions_OverSingleTunnelConnection()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);
        var acceptedBefore = tunnel.Server.AcceptedTunnelConnections;

        async Task RunClientAsync(string message)
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(IPAddress.Loopback, socks.Port);
            using var stream = tcpClient.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var greeting = await TestNetwork.ReadExactAsync(stream, 2);
            Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

            var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
            await stream.WriteAsync(connectRequest);
            var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
            Assert.Equal((byte)0x00, connectResponse[1]);

            var payload = Encoding.ASCII.GetBytes(message);
            await stream.WriteAsync(payload);
            var echoed = await TestNetwork.ReadExactAsync(stream, payload.Length);
            Assert.Equal(payload, echoed);
        }

        await Task.WhenAll(
            RunClientAsync("first-stream-through-multiplexer"),
            RunClientAsync("second-stream-through-multiplexer"));

        await Task.Delay(200);
        Assert.Equal(acceptedBefore + 1, tunnel.Server.AcceptedTunnelConnections);
    }

    [Fact]
    public async Task Socks5Client_RoundRobinAcrossTunnelServers_PerTcpSessionBinding()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnelA = await TestNetwork.StartTunnelServerAsync();
        await using var tunnelB = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(
            new (string Host, int Port)[]
            {
                ("127.0.0.1", tunnelA.Port),
                ("127.0.0.1", tunnelB.Port)
            });

        for (var i = 0; i < 6; i++)
        {
            await RunSingleSocksEchoRequestAsync(socks.Port, echo.Port, $"rr-{i}");
        }

        await Task.Delay(300);
        Assert.True(tunnelA.Server.AcceptedTunnelConnections >= 1, "First tunnel server was not used.");
        Assert.True(tunnelB.Server.AcceptedTunnelConnections >= 1, "Second tunnel server was not used.");
    }
}
