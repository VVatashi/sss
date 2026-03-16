using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class PerformanceMeasurementsTests
{
    private static async Task<RunningTunnelServer> StartTunnelServerAsync()
    {
        var port = AllocateUnusedPort();
        var server = new TunnelServer(IPAddress.Loopback, port, "dev-shared-key", TunnelCryptoPolicy.Default, new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 64,
            MaxSessionsPerTunnel = 4096
        });
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningTunnelServer(port, cts, runTask);
    }

    private static async Task<RunningSocksServer> StartSocksServerAsync(
        int tunnelPort,
        TunnelCipherAlgorithm algorithm,
        CompressionMode compression)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            "127.0.0.1",
            tunnelPort,
            "dev-shared-key",
            new TunnelCryptoPolicy
            {
                HandshakeMaxClockSkewSeconds = TunnelCryptoPolicy.Default.HandshakeMaxClockSkewSeconds,
                ReplayWindowSeconds = TunnelCryptoPolicy.Default.ReplayWindowSeconds,
                PreferredAlgorithm = algorithm
            },
            new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 10,
                IdleTimeoutSeconds = 60,
                ReconnectBaseDelayMs = 100,
                ReconnectMaxDelayMs = 500,
                ReconnectMaxAttempts = 10,
                MaxConcurrentSessions = 4096,
                SessionReceiveChannelCapacity = 1024
            },
            ProtocolConstants.Version,
            compression.Enabled,
            compression.Algorithm);
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
                        {
                            using var stream = client.GetStream();

                            var buffer = new byte[64 * 1024];
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

    private static async Task<RunningTunnelTrafficProxy> StartTunnelTrafficProxyAsync(int upstreamTunnelPort)
    {
        var proxyPort = AllocateUnusedPort();
        var listener = new TcpListener(IPAddress.Loopback, proxyPort);
        listener.Start();

        long bytesClientToServer = 0;
        long bytesServerToClient = 0;
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
                            using var upstream = new TcpClient();
                            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                            try
                            {
                                await upstream.ConnectAsync(IPAddress.Loopback, upstreamTunnelPort, cts.Token);
                                using var clientStream = client.GetStream();
                                using var upstreamStream = upstream.GetStream();

                                var toServer = CopyAndCountAsync(
                                    clientStream,
                                    upstreamStream,
                                    bytes => Interlocked.Add(ref bytesClientToServer, bytes),
                                    relayCts.Token);
                                var toClient = CopyAndCountAsync(
                                    upstreamStream,
                                    clientStream,
                                    bytes => Interlocked.Add(ref bytesServerToClient, bytes),
                                    relayCts.Token);

                                await Task.WhenAny(toServer, toClient);
                                relayCts.Cancel();

                                try
                                {
                                    await Task.WhenAll(toServer, toClient);
                                }
                                catch (OperationCanceledException)
                                {
                                }
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch
                            {
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

        await WaitUntilReachableAsync(proxyPort, cts.Token);
        return new RunningTunnelTrafficProxy(
            proxyPort,
            cts,
            runTask,
            () => bytesClientToServer,
            () => bytesServerToClient,
            () =>
            {
                Interlocked.Exchange(ref bytesClientToServer, 0);
                Interlocked.Exchange(ref bytesServerToClient, 0);
            });
    }

    private static async Task CopyAndCountAsync(
        NetworkStream source,
        NetworkStream destination,
        Action<int> onBytesCopied,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            onBytesCopied(read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task WaitUntilReachableAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
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

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Unexpected EOF while reading stream.");
            }

            offset += read;
        }

        return buffer;
    }
}
