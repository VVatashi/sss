using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class TunnelIntegrationTests
{
    private static readonly IPAddress SlowConnectTestAddress = IPAddress.Parse("203.0.113.1");

    [Fact]
    public async Task SlowOrTimedOutConnect_DoesNotBlockOtherSessions_OnSameTunnel()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync(new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 32,
            MaxSessionsPerTunnel = 256,
            ConnectTimeoutMs = 1200,
            ConnectReplyOverrideAsync = async (request, connectTimeoutMs, cancellationToken) =>
            {
                if (request.AddressType is AddressType.IPv4
                    && request.Address == SlowConnectTestAddress.ToString()
                    && request.Port == 81)
                {
                    await Task.Delay(connectTimeoutMs + 200, cancellationToken);
                    return 0x04;
                }

                return null;
            }
        });
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);

        var slowConnectTask = RunSingleSocksConnectExpectFailureAsync(
            socks.Port,
            SlowConnectTestAddress,
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

    [Fact]
    public async Task Socks5Client_FailsOverToNextTunnelServer_WhenFirstServerRejectsConnect()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnelA = await TestNetwork.StartTunnelServerAsync(new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 32,
            MaxSessionsPerTunnel = 1
        });
        await using var tunnelB = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(
            new (string Host, int Port)[]
            {
                ("127.0.0.1", tunnelA.Port),
                ("127.0.0.1", tunnelB.Port)
            });

        await using var occupier = new TunnelClientMultiplexer(
            "127.0.0.1",
            tunnelA.Port,
            PreSharedKey.Derive32Bytes("dev-shared-key"),
            TunnelCryptoPolicy.Default,
            TunnelConnectionPolicy.Default);

        var (occupiedSessionId, occupiedReply, _) = await occupier.OpenSessionAsync(
            new ConnectRequest(AddressType.IPv4, "127.0.0.1", (ushort)echo.Port),
            CancellationToken.None);
        Assert.Equal((byte)0x00, occupiedReply);

        try
        {
            await RunSingleSocksEchoRequestAsync(socks.Port, echo.Port, "failover-connect");
        }
        finally
        {
            await occupier.CloseSessionAsync(occupiedSessionId, 0x00, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Connect_WithUnresolvableDomain_ReturnsHostUnreachable()
    {
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = TestNetwork.BuildConnectRequestDomain("nonexistent.invalid", 80);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x04, connectResponse[1]);
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
