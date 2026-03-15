using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Client.Tests;

internal static class TestNetwork
{
    public static async Task<RunningSocksServer> StartSocksServerAsync(
        int tunnelPort,
        TunnelConnectionPolicy? connectionPolicy = null,
        TunnelCryptoPolicy? cryptoPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            "127.0.0.1",
            tunnelPort,
            "dev-shared-key",
            cryptoPolicy ?? TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    public static async Task<RunningSocksServer> StartSocksServerAsync(
        IReadOnlyList<(string Host, int Port)> tunnelServers,
        TunnelConnectionPolicy? connectionPolicy = null,
        TunnelCryptoPolicy? cryptoPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            tunnelServers,
            "dev-shared-key",
            cryptoPolicy ?? TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    public static async Task<RunningSocksServer> StartStandaloneSocksServerAsync()
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    public static async Task<RunningTunnelServer> StartTunnelServerAsync(TunnelServerPolicy? serverPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new TunnelServer(IPAddress.Loopback, port, "dev-shared-key", TunnelCryptoPolicy.Default, serverPolicy);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningTunnelServer(server, port, cts, runTask);
    }

    public static async Task<RunningTunnelServer> StartTunnelServerOnPortAsync(int port)
    {
        var server = new TunnelServer(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningTunnelServer(server, port, cts, runTask);
    }

    public static async Task<RunningEchoServer> StartEchoServerAsync(int bufferBytes = 8 * 1024)
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
                        {
                            using var stream = client.GetStream();

                            var buffer = new byte[bufferBytes];
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

    public static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    public static async Task WaitUntilReachableAsync(int port, CancellationToken cancellationToken)
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

    public static int AllocateUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static byte[] BuildConnectRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return
        [
            0x05, 0x01, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port
        ];
    }

    public static byte[] BuildBindRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return
        [
            0x05, 0x02, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port
        ];
    }

    public static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, int timeoutMs = 5000)
    {
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), timeoutCts.Token);
            if (read == 0)
            {
                throw new IOException("Unexpected EOF while reading from stream.");
            }

            offset += read;
        }

        return buffer;
    }

    public static async Task<Socks5Reply> ReadSocks5ReplyAsync(NetworkStream stream)
    {
        var header = await ReadExactAsync(stream, 4);
        var addressType = header[3];
        var addressLength = addressType switch
        {
            0x01 => 4,
            0x04 => 16,
            _ => throw new InvalidDataException($"Unexpected address type in SOCKS5 reply: {addressType}")
        };

        await ReadExactAsync(stream, addressLength + 2);
        return new Socks5Reply(header[1]);
    }

    internal readonly record struct Socks5Reply(byte ReplyCode);

    internal sealed class RunningSocksServer : IAsyncDisposable
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

    internal sealed class RunningTunnelServer : IAsyncDisposable
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

    internal sealed class RunningEchoServer : IAsyncDisposable
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
