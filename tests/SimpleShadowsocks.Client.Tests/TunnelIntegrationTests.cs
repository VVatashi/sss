using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Socks5;
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

    private static async Task<RunningTunnelServer> StartTunnelServerAsync()
    {
        var port = AllocateUnusedPort();
        var server = new TunnelServer(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningTunnelServer(port, cts, runTask);
    }

    private static async Task<RunningSocksServer> StartSocksServerAsync(int tunnelPort)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(IPAddress.Loopback, port, "127.0.0.1", tunnelPort);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
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
        public RunningTunnelServer(int port, CancellationTokenSource cts, Task runTask)
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
