using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Client.Tests;

public sealed class TunnelIntegrationTests
{
    [Fact]
    public async Task Socks5Client_UsesProtocolTunnel_ToReachTarget()
    {
        await using var echo = await StartEchoServerAsync();
        await using var tunnel = await StartTunnelServerAsync();
        await using var socks = await StartSocksServerAsync(tunnel.Port);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
        await stream.WriteAsync(connectRequest);

        var connectResponse = await ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var payload = Encoding.ASCII.GetBytes("hello-over-tunnel");
        await stream.WriteAsync(payload);
        var echoed = await ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Socks5Client_MultiplexesMultipleSessions_OverSingleTunnelConnection()
    {
        await using var echo = await StartEchoServerAsync();
        await using var tunnel = await StartTunnelServerAsync();
        await using var socks = await StartSocksServerAsync(tunnel.Port);
        var acceptedBefore = tunnel.Server.AcceptedTunnelConnections;

        async Task RunClientAsync(string message)
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(IPAddress.Loopback, socks.Port);
            using var stream = tcpClient.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var greeting = await ReadExactAsync(stream, 2);
            Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

            var connectRequest = BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
            await stream.WriteAsync(connectRequest);
            var connectResponse = await ReadExactAsync(stream, 10);
            Assert.Equal((byte)0x00, connectResponse[1]);

            var payload = Encoding.ASCII.GetBytes(message);
            await stream.WriteAsync(payload);
            var echoed = await ReadExactAsync(stream, payload.Length);
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
        await using var echo = await StartEchoServerAsync();
        await using var tunnelA = await StartTunnelServerAsync();
        await using var tunnelB = await StartTunnelServerAsync();
        await using var socks = await StartSocksServerAsync(
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

    [Fact]
    public async Task Socks5Client_ReconnectsTunnel_AfterServerRestart()
    {
        await using var echo = await StartEchoServerAsync();

        var tunnelPort = AllocateUnusedPort();
        var tunnel1 = await StartTunnelServerOnPortAsync(tunnelPort);
        await using var socks = await StartSocksServerAsync(
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
            return await StartTunnelServerOnPortAsync(tunnelPort);
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
    public async Task SlowOrTimedOutConnect_DoesNotBlockOtherSessions_OnSameTunnel()
    {
        await using var echo = await StartEchoServerAsync();
        await using var tunnel = await StartTunnelServerAsync(new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 32,
            MaxSessionsPerTunnel = 256,
            ConnectTimeoutMs = 1200
        });
        await using var socks = await StartSocksServerAsync(tunnel.Port);

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
        await using var echo = await StartEchoServerAsync();
        await using var tunnel = await StartTunnelServerAsync(new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 32,
            MaxSessionsPerTunnel = 1
        });
        await using var socks = await StartSocksServerAsync(tunnel.Port);

        using var tcpClient1 = new TcpClient();
        await tcpClient1.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream1 = tcpClient1.GetStream();
        await stream1.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        Assert.Equal(new byte[] { 0x05, 0x00 }, await ReadExactAsync(stream1, 2));
        await stream1.WriteAsync(BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port));
        var firstConnect = await ReadExactAsync(stream1, 10);
        Assert.Equal((byte)0x00, firstConnect[1]);

        using var tcpClient2 = new TcpClient();
        await tcpClient2.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream2 = tcpClient2.GetStream();
        await stream2.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        Assert.Equal(new byte[] { 0x05, 0x00 }, await ReadExactAsync(stream2, 2));
        await stream2.WriteAsync(BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port));
        var secondConnect = await ReadExactAsync(stream2, 10);
        Assert.NotEqual((byte)0x00, secondConnect[1]);
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

    private static async Task<RunningTunnelServer> StartTunnelServerAsync(TunnelServerPolicy? serverPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new TunnelServer(IPAddress.Loopback, port, "dev-shared-key", TunnelCryptoPolicy.Default, serverPolicy);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningTunnelServer(server, port, cts, runTask);
    }

    private static async Task<RunningTunnelServer> StartTunnelServerOnPortAsync(int port)
    {
        var server = new TunnelServer(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningTunnelServer(server, port, cts, runTask);
    }

    private static async Task<RunningSocksServer> StartSocksServerAsync(
        int tunnelPort,
        TunnelConnectionPolicy? connectionPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            "127.0.0.1",
            tunnelPort,
            "dev-shared-key",
            TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    private static async Task<RunningSocksServer> StartSocksServerAsync(
        IReadOnlyList<(string Host, int Port)> tunnelServers,
        TunnelConnectionPolicy? connectionPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            tunnelServers,
            "dev-shared-key",
            TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    private static async Task RunSingleSocksEchoRequestAsync(int socksPort, int echoPort, string message)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = BuildConnectRequestIPv4(IPAddress.Loopback, echoPort);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var payload = Encoding.ASCII.GetBytes(message);
        await stream.WriteAsync(payload);
        var echoed = await ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }

    private static async Task RunSingleSocksConnectExpectFailureAsync(int socksPort, IPAddress address, int port)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = BuildConnectRequestIPv4(address, port);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await ReadExactAsync(stream, 10);
        Assert.NotEqual((byte)0x00, connectResponse[1]);
    }

    private static async Task<RunningEchoServer> StartEchoServerAsync()
    {
        var port = AllocateUnusedPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            var buffer = new byte[8 * 1024];
                            while (!cts.IsCancellationRequested)
                            {
                                var read = await stream.ReadAsync(buffer, cts.Token);
                                if (read == 0)
                                {
                                    break;
                                }

                                await stream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                            }
                        }
                    }, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                listener.Stop();
            }
        }, cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningEchoServer(port, cts, runTask);
    }

    private static async Task WaitUntilReachableAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return;
            }
            catch
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        throw new TimeoutException($"Port {port} did not become reachable in time.");
    }

    private static int AllocateUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] BuildConnectRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return
        [
            0x05, 0x01, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port
        ];
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
            if (read == 0)
            {
                throw new IOException("Unexpected EOF while reading from stream.");
            }

            offset += read;
        }

        return buffer;
    }

    private sealed class RunningSocksServer : IAsyncDisposable
    {
        public RunningSocksServer(int port, CancellationTokenSource cts, Task runTask)
        {
            Port = port;
            _cts = cts;
            _runTask = runTask;
        }

        public int Port { get; }
        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private sealed class RunningTunnelServer : IAsyncDisposable
    {
        public RunningTunnelServer(TunnelServer server, int port, CancellationTokenSource cts, Task runTask)
        {
            Server = server;
            Port = port;
            _cts = cts;
            _runTask = runTask;
        }

        public TunnelServer Server { get; }
        public int Port { get; }
        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private sealed class RunningEchoServer : IAsyncDisposable
    {
        public RunningEchoServer(int port, CancellationTokenSource cts, Task runTask)
        {
            Port = port;
            _cts = cts;
            _runTask = runTask;
        }

        public int Port { get; }
        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}
