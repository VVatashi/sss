using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class TunnelIntegrationTests
{
    [Fact]
    public async Task Socks5Client_ReconnectsTunnel_AfterServerRestart()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();

        var tunnelPort = TestNetwork.AllocateUnusedPort();
        var tunnel1 = await TestNetwork.StartTunnelServerOnPortAsync(tunnelPort);
        await using var socks = await TestNetwork.StartSocksServerAsync(
            tunnelPort,
            new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 1,
                IdleTimeoutSeconds = 5,
                ReconnectBaseDelayMs = 100,
                ReconnectMaxDelayMs = 300,
                ReconnectMaxAttempts = 20
            });

        await RunSingleSocksEchoRequestAsync(socks.Port, echo.Port, "phase-1");

        await tunnel1.DisposeAsync();

        var restartTask = Task.Run(async () =>
        {
            await Task.Delay(300);
            return await TestNetwork.StartTunnelServerOnPortAsync(tunnelPort);
        });

        var success = false;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                await RunSingleSocksEchoRequestAsync(socks.Port, echo.Port, "phase-2");
                success = true;
                break;
            }
            catch
            {
                await Task.Delay(150);
            }
        }

        Assert.True(success, "Client did not reconnect tunnel in time.");
        await using var tunnel2 = await restartTask;
    }

    [Fact]
    public async Task Socks5Client_GracefullyMigratesActiveSession_AfterTunnelReconnect()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();

        var tunnelPort = TestNetwork.AllocateUnusedPort();
        var tunnel1 = await TestNetwork.StartTunnelServerOnPortAsync(tunnelPort);
        await using var socks = await TestNetwork.StartSocksServerAsync(
            tunnelPort,
            new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 1,
                IdleTimeoutSeconds = 30,
                ReconnectBaseDelayMs = 100,
                ReconnectMaxDelayMs = 300,
                ReconnectMaxAttempts = 30
            });

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        Assert.Equal(new byte[] { 0x05, 0x00 }, await TestNetwork.ReadExactAsync(stream, 2));
        await stream.WriteAsync(TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port));
        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var phase1 = Encoding.ASCII.GetBytes("migration-phase-1");
        await stream.WriteAsync(phase1);
        Assert.Equal(phase1, await TestNetwork.ReadExactAsync(stream, phase1.Length));

        await tunnel1.DisposeAsync();
        var restartTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            return await TestNetwork.StartTunnelServerOnPortAsync(tunnelPort);
        });

        await using var tunnel2 = await restartTask;

        var resumed = false;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                var phase2 = Encoding.ASCII.GetBytes($"migration-phase-2-{attempt}");
                await stream.WriteAsync(phase2);
                var echoed = await TestNetwork.ReadExactAsync(stream, phase2.Length, timeoutMs: 20_000);
                Assert.Equal(phase2, echoed);
                resumed = true;
                break;
            }
            catch
            {
                await Task.Delay(300);
            }
        }

        Assert.True(resumed, "Active SOCKS session was not resumed after tunnel reconnect.");
    }

    [Fact]
    public void TunnelClientMultiplexer_InvalidReconnectPolicy_Throws()
    {
        var policy = new TunnelConnectionPolicy
        {
            ReconnectMaxAttempts = 0
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TunnelClientMultiplexer(
                "127.0.0.1",
                12345,
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("dev-shared-key")),
                TunnelCryptoPolicy.Default,
                policy));
    }
}
