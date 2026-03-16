using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
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

    public static Task<RunningUdpEchoServer> StartUdpEchoServerAsync()
    {
        var port = AllocateUnusedUdpPort();
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var cts = new CancellationTokenSource();
        var runTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var received = await udp.ReceiveAsync(cts.Token);
                    await udp.SendAsync(received.Buffer, received.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                udp.Dispose();
            }
        }, cts.Token);

        return Task.FromResult(new RunningUdpEchoServer(port, cts, runTask));
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

    public static int AllocateUnusedUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    public static byte[] BuildConnectRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return
        [
            0x05, 0x01, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port
        ];
    }

    public static byte[] BuildConnectRequestDomain(string domain, int port)
    {
        var domainBytes = System.Text.Encoding.ASCII.GetBytes(domain);
        if (domainBytes.Length == 0 || domainBytes.Length > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(domain), "Domain length must be in 1..255 bytes.");
        }

        var request = new byte[4 + 1 + domainBytes.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)domainBytes.Length;
        Buffer.BlockCopy(domainBytes, 0, request, 5, domainBytes.Length);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)port;
        return request;
    }

    public static byte[] BuildBindRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return
        [
            0x05, 0x02, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port
        ];
    }

    public static byte[] BuildUdpAssociateRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return
        [
            0x05, 0x03, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port
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
        var addressBytes = addressType switch
        {
            0x01 => await ReadExactAsync(stream, 4),
            0x04 => await ReadExactAsync(stream, 16),
            0x03 => await ReadExactAsync(stream, (await ReadExactAsync(stream, 1))[0]),
            _ => throw new InvalidDataException($"Unexpected address type in SOCKS5 reply: {addressType}")
        };
        var portBytes = await ReadExactAsync(stream, 2);
        var port = (portBytes[0] << 8) | portBytes[1];

        var endpoint = addressType switch
        {
            0x01 => new IPEndPoint(new IPAddress(addressBytes), port),
            0x04 => new IPEndPoint(new IPAddress(addressBytes), port),
            _ => null
        };

        return new Socks5Reply(header[1], endpoint);
    }

    public static byte[] BuildSocks5UdpDatagram(IPAddress destinationAddress, int destinationPort, byte[] payload)
    {
        return BuildSocks5UdpDatagram(destinationAddress.ToString(), destinationPort, payload);
    }

    public static byte[] BuildSocks5UdpDatagram(IPAddress destinationAddress, int destinationPort, byte[] payload, byte fragment)
    {
        return BuildSocks5UdpDatagram(destinationAddress.ToString(), destinationPort, payload, fragment);
    }

    public static byte[] BuildSocks5UdpDatagram(string destinationAddressOrHost, int destinationPort, byte[] payload, byte fragment = 0x00)
    {
        var addressType = TryResolveAddressType(destinationAddressOrHost);
        var request = ProtocolPayloadSerializer.SerializeConnectRequest(
            new ConnectRequest(addressType, destinationAddressOrHost, (ushort)destinationPort));
        var datagram = new byte[3 + request.Length + payload.Length];
        datagram[0] = 0x00;
        datagram[1] = 0x00;
        datagram[2] = fragment;
        Buffer.BlockCopy(request, 0, datagram, 3, request.Length);
        Buffer.BlockCopy(payload, 0, datagram, 3 + request.Length, payload.Length);
        return datagram;
    }

    public static (IPAddress SourceAddress, int SourcePort, byte[] Payload) ParseSocks5UdpDatagram(byte[] datagram)
    {
        if (datagram.Length < 3 || datagram[0] != 0 || datagram[1] != 0 || datagram[2] != 0)
        {
            throw new InvalidDataException("Invalid SOCKS5 UDP header.");
        }

        var udpDatagram = ProtocolPayloadSerializer.DeserializeUdpDatagram(datagram.AsSpan(3));
        if (!IPAddress.TryParse(udpDatagram.Address, out var address))
        {
            throw new InvalidDataException($"Invalid source address in UDP datagram: {udpDatagram.Address}.");
        }

        return (address, udpDatagram.Port, udpDatagram.Payload.ToArray());
    }

    private static AddressType TryResolveAddressType(string destinationAddressOrHost)
    {
        if (!IPAddress.TryParse(destinationAddressOrHost, out var ipAddress))
        {
            return AddressType.Domain;
        }

        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => AddressType.IPv4,
            AddressFamily.InterNetworkV6 => AddressType.IPv6,
            _ => throw new InvalidDataException($"Unsupported address family: {ipAddress.AddressFamily}.")
        };
    }

    internal readonly record struct Socks5Reply(byte ReplyCode, IPEndPoint? BoundEndPoint);

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

    internal sealed class RunningUdpEchoServer : IAsyncDisposable
    {
        public RunningUdpEchoServer(int port, CancellationTokenSource cts, Task runTask)
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
