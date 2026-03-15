using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class TunnelIntegrationTests
{
    [Fact]
    public async Task SlowOrTimedOutConnect_DoesNotBlockOtherSessions_OnSameTunnel()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync(new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 32,
            MaxSessionsPerTunnel = 256,
            ConnectTimeoutMs = 1200
        });
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);

        var slowConnectTask = RunSingleSocksConnectExpectFailureAsync(
            socks.Port,
            IPAddress.Parse("203.0.113.1"),
            81);

        await Task.Delay(150);
        await RunSingleSocksEchoRequestAsync(socks.Port, echo.Port, "fast-path");
        await slowConnectTask;
    }

    [Fact]
    public async Task Socks5Client_SecondSessionIsRejected_WhenServerSessionLimitReached()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync(new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 32,
            MaxSessionsPerTunnel = 1
        });
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);

        using var tcpClient1 = new TcpClient();
        await tcpClient1.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream1 = tcpClient1.GetStream();
        await stream1.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        Assert.Equal(new byte[] { 0x05, 0x00 }, await TestNetwork.ReadExactAsync(stream1, 2));
        await stream1.WriteAsync(TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port));
        var firstConnect = await TestNetwork.ReadExactAsync(stream1, 10);
        Assert.Equal((byte)0x00, firstConnect[1]);

        using var tcpClient2 = new TcpClient();
        await tcpClient2.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream2 = tcpClient2.GetStream();
        await stream2.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        Assert.Equal(new byte[] { 0x05, 0x00 }, await TestNetwork.ReadExactAsync(stream2, 2));
        await stream2.WriteAsync(TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port));
        var secondConnect = await TestNetwork.ReadExactAsync(stream2, 10);
        Assert.NotEqual((byte)0x00, secondConnect[1]);
    }

    private static async Task RunSingleSocksEchoRequestAsync(int socksPort, int echoPort, string message)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echoPort);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var payload = Encoding.ASCII.GetBytes(message);
        await stream.WriteAsync(payload);
        var echoed = await TestNetwork.ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }

    private static async Task RunSingleSocksConnectExpectFailureAsync(int socksPort, IPAddress address, int port)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = TestNetwork.BuildConnectRequestIPv4(address, port);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.NotEqual((byte)0x00, connectResponse[1]);
    }
}
