using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Http;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Http;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Client.Tests;

internal static class TestNetwork
{
    public static async Task<RunningSocksServer> StartSocksServerAsync(
        int tunnelPort,
        TunnelConnectionPolicy? connectionPolicy = null,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TrafficRoutingPolicy? routingPolicy = null,
        Socks5AuthenticationOptions? authenticationOptions = null)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            "127.0.0.1",
            tunnelPort,
            "dev-shared-key",
            cryptoPolicy ?? TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default,
            routingPolicy: routingPolicy,
            authenticationOptions: authenticationOptions);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    public static async Task<RunningSocksServer> StartSocksServerAsync(
        IReadOnlyList<(string Host, int Port)> tunnelServers,
        TunnelConnectionPolicy? connectionPolicy = null,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TrafficRoutingPolicy? routingPolicy = null,
        Socks5AuthenticationOptions? authenticationOptions = null)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            tunnelServers,
            "dev-shared-key",
            cryptoPolicy ?? TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default,
            routingPolicy: routingPolicy,
            authenticationOptions: authenticationOptions);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    public static async Task<RunningHttpProxyServer> StartHttpProxyServerAsync(
        int tunnelPort,
        TunnelConnectionPolicy? connectionPolicy = null,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TrafficRoutingPolicy? routingPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new HttpProxyServer(
            IPAddress.Loopback,
            port,
            "127.0.0.1",
            tunnelPort,
            "dev-shared-key",
            cryptoPolicy ?? TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default,
            routingPolicy: routingPolicy);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningHttpProxyServer(server, port, cts, runTask);
    }

    public static async Task<RunningHttpProxyServer> StartStandaloneHttpProxyServerAsync(
        TrafficRoutingPolicy? routingPolicy = null)
    {
        var port = AllocateUnusedPort();
        var server = new HttpProxyServer(
            IPAddress.Loopback,
            port,
            Array.Empty<(string Host, int Port)>(),
            "dev-shared-key",
            TunnelCryptoPolicy.Default,
            TunnelConnectionPolicy.Default,
            routingPolicy: routingPolicy);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningHttpProxyServer(server, port, cts, runTask);
    }

    public static async Task<RunningHttpReverseProxyServer> StartHttpReverseProxyServerAsync(TunnelServer tunnelServer)
    {
        var port = AllocateUnusedPort();
        var server = new HttpReverseProxyServer(IPAddress.Loopback, port, tunnelServer);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningHttpReverseProxyServer(server, port, cts, runTask);
    }

    public static async Task<RunningHttpReverseProxyClient> StartHttpReverseProxyClientAsync(
        RunningTunnelServer tunnel,
        IReadOnlyList<HttpReverseProxyTunnelHandler.Route> routes,
        TunnelConnectionPolicy? connectionPolicy = null)
    {
        var client = new HttpReverseProxyClient(
            "127.0.0.1",
            tunnel.Port,
            "dev-shared-key",
            routes,
            TunnelCryptoPolicy.Default,
            connectionPolicy ?? TunnelConnectionPolicy.Default);
        var cts = new CancellationTokenSource();
        var runTask = client.RunAsync(cts.Token);
        await WaitForTunnelConnectionsAsync(() => tunnel.Server.AcceptedTunnelConnections >= 1, cts.Token);
        return new RunningHttpReverseProxyClient(client, cts, runTask);
    }

    public static async Task<RunningSocksServer> StartStandaloneSocksServerAsync(
        TrafficRoutingPolicy? routingPolicy = null,
        Socks5AuthenticationOptions? authenticationOptions = null)
    {
        var port = AllocateUnusedPort();
        var server = routingPolicy is null
            ? new Socks5Server(IPAddress.Loopback, port)
            : new Socks5Server(
                IPAddress.Loopback,
                port,
                Array.Empty<(string Host, int Port)>(),
                "dev-shared-key",
                TunnelCryptoPolicy.Default,
                TunnelConnectionPolicy.Default,
                routingPolicy: routingPolicy,
                authenticationOptions: authenticationOptions);

        if (routingPolicy is null && authenticationOptions is not null)
        {
            server = new Socks5Server(
                IPAddress.Loopback,
                port,
                Array.Empty<(string Host, int Port)>(),
                "dev-shared-key",
                TunnelCryptoPolicy.Default,
                TunnelConnectionPolicy.Default,
                routingPolicy: null,
                authenticationOptions: authenticationOptions);
        }
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

        await server.WaitUntilStartedAsync();
        return new RunningTunnelServer(server, port, cts, runTask);
    }

    public static async Task<RunningTunnelServer> StartTunnelServerOnPortAsync(int port)
    {
        var server = new TunnelServer(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await server.WaitUntilStartedAsync();
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

    public static async Task<RunningHttpOriginServer> StartHttpOriginServerAsync(
        Func<HttpOriginRequest, HttpOriginResponse>? responder = null)
    {
        var port = AllocateUnusedPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var capturedRequests = new List<HttpOriginRequest>();
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
                            var reader = new BufferedTestStreamReader(stream);
                            while (!cts.IsCancellationRequested)
                            {
                                var request = await HttpOriginRequest.ReadAsync(reader, cts.Token);
                                if (request is null)
                                {
                                    return;
                                }

                                lock (capturedRequests)
                                {
                                    capturedRequests.Add(request);
                                }

                                var response = responder?.Invoke(request)
                                    ?? new HttpOriginResponse(200, "OK", [new HttpHeader("Content-Type", "text/plain")], Encoding.ASCII.GetBytes("ok"));
                                await WriteOriginResponseAsync(stream, response, cts.Token);
                                if (!request.ShouldKeepAlive)
                                {
                                    return;
                                }
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
        return new RunningHttpOriginServer(port, cts, runTask, capturedRequests);
    }

    public static async Task<RunningHttpOriginServer> StartStreamingHttpOriginServerAsync(
        Func<HttpOriginRequest, HttpStreamingOriginResponse>? responder)
    {
        var port = AllocateUnusedPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var capturedRequests = new List<HttpOriginRequest>();
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
                            var reader = new BufferedTestStreamReader(stream);
                            while (!cts.IsCancellationRequested)
                            {
                                var request = await HttpOriginRequest.ReadAsync(reader, cts.Token);
                                if (request is null)
                                {
                                    return;
                                }

                                lock (capturedRequests)
                                {
                                    capturedRequests.Add(request);
                                }

                                var response = responder?.Invoke(request)
                                    ?? new HttpStreamingOriginResponse(
                                        200,
                                        "OK",
                                        [new HttpHeader("Content-Type", "text/plain")],
                                        [new HttpStreamingChunk(Encoding.ASCII.GetBytes("ok"))]);
                                await WriteStreamingOriginResponseAsync(stream, response, cts.Token);
                                if (!request.ShouldKeepAlive)
                                {
                                    return;
                                }
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
        return new RunningHttpOriginServer(port, cts, runTask, capturedRequests);
    }

    public static async Task<StreamingHttpConnection> OpenStreamingHttpConnectionAsync(int port, string requestText)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var reader = new BufferedTestStreamReader(stream);

        var requestBytes = Encoding.ASCII.GetBytes(requestText.ReplaceLineEndings("\r\n"));
        await stream.WriteAsync(requestBytes);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var headerBytes = await reader.ReadHeaderBlockAsync(timeoutCts.Token)
            ?? throw new InvalidDataException("HTTP response header was not received.");
        var headerText = Encoding.ASCII.GetString(headerBytes);
        var separator = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidDataException("HTTP response separator not found.");
        }

        return new StreamingHttpConnection(client, stream, reader, headerText[..separator]);
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

    public static async Task WaitForTunnelConnectionsAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }
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

    public static async Task<byte[]> SendSocks5GreetingAsync(NetworkStream stream, params byte[] methods)
    {
        if (methods.Length == 0)
        {
            throw new ArgumentException("At least one authentication method must be provided.", nameof(methods));
        }

        var request = new byte[2 + methods.Length];
        request[0] = 0x05;
        request[1] = (byte)methods.Length;
        Buffer.BlockCopy(methods, 0, request, 2, methods.Length);
        await stream.WriteAsync(request);
        return await ReadExactAsync(stream, 2);
    }

    public static async Task<byte[]> SendUsernamePasswordAuthAsync(NetworkStream stream, string username, string password)
    {
        var usernameBytes = System.Text.Encoding.UTF8.GetBytes(username);
        var passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
        if (usernameBytes.Length is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(username), "Username must be 1..255 bytes in UTF-8.");
        }

        if (passwordBytes.Length is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(password), "Password must be 1..255 bytes in UTF-8.");
        }

        var request = new byte[3 + usernameBytes.Length + passwordBytes.Length];
        request[0] = 0x01;
        request[1] = (byte)usernameBytes.Length;
        Buffer.BlockCopy(usernameBytes, 0, request, 2, usernameBytes.Length);
        request[2 + usernameBytes.Length] = (byte)passwordBytes.Length;
        Buffer.BlockCopy(passwordBytes, 0, request, 3 + usernameBytes.Length, passwordBytes.Length);
        await stream.WriteAsync(request);
        return await ReadExactAsync(stream, 2);
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
    internal sealed record HttpOriginResponse(int StatusCode, string ReasonPhrase, IReadOnlyList<HttpHeader> Headers, byte[] Body);
    internal sealed record HttpStreamingChunk(byte[] Body, int DelayMs = 0);
    internal sealed record HttpStreamingOriginResponse(
        int StatusCode,
        string ReasonPhrase,
        IReadOnlyList<HttpHeader> Headers,
        IReadOnlyList<HttpStreamingChunk> Chunks);

    public static async Task<(string Head, string BodyText)> SendRawHttpRequestAsync(int port, string requestText)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var requestBytes = Encoding.ASCII.GetBytes(requestText.ReplaceLineEndings("\r\n"));
        await stream.WriteAsync(requestBytes);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responseBytes = await ReadHttpResponseAsync(stream, timeoutCts.Token);
        var responseText = Encoding.ASCII.GetString(responseBytes);
        var separator = responseText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidDataException("HTTP response separator not found.");
        }

        return (responseText[..separator], responseText[(separator + 4)..]);
    }

    internal sealed class HttpOriginRequest
    {
        private HttpOriginRequest(
            string method,
            string pathAndQuery,
            Version version,
            IReadOnlyList<HttpHeader> headers,
            byte[] body,
            bool shouldKeepAlive)
        {
            Method = method;
            PathAndQuery = pathAndQuery;
            Version = version;
            Headers = headers;
            Body = body;
            ShouldKeepAlive = shouldKeepAlive;
        }

        public string Method { get; }
        public string PathAndQuery { get; }
        public Version Version { get; }
        public IReadOnlyList<HttpHeader> Headers { get; }
        public byte[] Body { get; }
        public bool ShouldKeepAlive { get; }

        public string? GetHeader(string name)
        {
            return Headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        }

        public static async Task<HttpOriginRequest?> ReadAsync(BufferedTestStreamReader reader, CancellationToken cancellationToken)
        {
            var headerBytes = await reader.ReadHeaderBlockAsync(cancellationToken);
            if (headerBytes is null)
            {
                return null;
            }

            var headerText = Encoding.Latin1.GetString(headerBytes);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length != 3)
            {
                throw new InvalidDataException("Invalid origin request line.");
            }

            var version = Version.Parse(requestLine[2][5..]);
            var headers = lines
                .Skip(1)
                .TakeWhile(line => !string.IsNullOrEmpty(line))
                .Select(static line =>
                {
                    var separatorIndex = line.IndexOf(':');
                    return new HttpHeader(line[..separatorIndex].Trim(), line[(separatorIndex + 1)..].Trim());
                })
                .ToArray();
            var contentLengthHeader = headers.FirstOrDefault(static h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value;
            var body = string.IsNullOrWhiteSpace(contentLengthHeader)
                ? []
                : await reader.ReadBytesAsync(int.Parse(contentLengthHeader), cancellationToken);
            var keepAlive = version <= HttpVersion.Version10
                ? headers.Any(static h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    && h.Value.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
                : !headers.Any(static h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    && h.Value.Contains("close", StringComparison.OrdinalIgnoreCase));
            return new HttpOriginRequest(requestLine[0], requestLine[1], version, headers, body, keepAlive);
        }
    }

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

    internal sealed class RunningHttpProxyServer : IAsyncDisposable
    {
        public RunningHttpProxyServer(HttpProxyServer server, int port, CancellationTokenSource cts, Task runTask)
        {
            Server = server;
            Port = port;
            _cts = cts;
            _runTask = runTask;
        }

        public HttpProxyServer Server { get; }
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

    internal sealed class RunningHttpReverseProxyServer : IAsyncDisposable
    {
        public RunningHttpReverseProxyServer(HttpReverseProxyServer server, int port, CancellationTokenSource cts, Task runTask)
        {
            Server = server;
            Port = port;
            _cts = cts;
            _runTask = runTask;
        }

        public HttpReverseProxyServer Server { get; }
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

    internal sealed class RunningHttpReverseProxyClient : IAsyncDisposable
    {
        public RunningHttpReverseProxyClient(HttpReverseProxyClient client, CancellationTokenSource cts, Task runTask)
        {
            Client = client;
            _cts = cts;
            _runTask = runTask;
        }

        public HttpReverseProxyClient Client { get; }
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

    internal sealed class RunningHttpOriginServer : IAsyncDisposable
    {
        public RunningHttpOriginServer(int port, CancellationTokenSource cts, Task runTask, List<HttpOriginRequest> requests)
        {
            Port = port;
            Requests = requests;
            _cts = cts;
            _runTask = runTask;
        }

        public int Port { get; }
        public List<HttpOriginRequest> Requests { get; }
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

    internal sealed class StreamingHttpConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly BufferedTestStreamReader _reader;

        public StreamingHttpConnection(TcpClient client, NetworkStream stream, BufferedTestStreamReader reader, string head)
        {
            _client = client;
            _stream = stream;
            _reader = reader;
            Head = head;
        }

        public string Head { get; }

        public async Task<string> ReadUntilTextAsync(string expectedText, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            var buffer = new byte[1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _reader.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    throw new IOException($"Unexpected EOF while waiting for '{expectedText}'.");
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var text = builder.ToString();
                if (text.Contains(expectedText, StringComparison.Ordinal))
                {
                    return text;
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        public async Task<string> ReadToEndTextAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            var buffer = new byte[1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _reader.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return builder.ToString();
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WriteOriginResponseAsync(NetworkStream stream, HttpOriginResponse response, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
            .Append(response.StatusCode)
            .Append(' ')
            .Append(response.ReasonPhrase)
            .Append("\r\n");
        foreach (var header in response.Headers)
        {
            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, cancellationToken);
        }
    }

    private static async Task WriteStreamingOriginResponseAsync(
        NetworkStream stream,
        HttpStreamingOriginResponse response,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
            .Append(response.StatusCode)
            .Append(' ')
            .Append(response.ReasonPhrase)
            .Append("\r\n");
        foreach (var header in response.Headers)
        {
            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("Transfer-Encoding: chunked\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken);

        foreach (var chunk in response.Chunks)
        {
            if (chunk.DelayMs > 0)
            {
                await Task.Delay(chunk.DelayMs, cancellationToken);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Body.Length:X}\r\n"), cancellationToken);
            if (chunk.Body.Length > 0)
            {
                await stream.WriteAsync(chunk.Body, cancellationToken);
            }

            await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
        }

        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
    }

    private static async Task<byte[]> ReadHttpResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var readBuffer = new byte[4096];
        while (true)
        {
            var snapshot = buffer.ToArray();
            var text = Encoding.ASCII.GetString(snapshot);
            var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator >= 0)
            {
                var head = text[..separator];
                var contentLength = head.Split("\r\n", StringSplitOptions.None)
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .Select(parts => int.Parse(parts[1].Trim()))
                    .Cast<int?>()
                    .FirstOrDefault();
                if (contentLength.HasValue)
                {
                    var totalBytes = separator + 4 + contentLength.Value;
                    while (buffer.Length < totalBytes)
                    {
                        var read = await stream.ReadAsync(readBuffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        await buffer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken);
                    }

                    return buffer.ToArray();
                }

                if (head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("\r\n0\r\n\r\n", StringComparison.Ordinal))
                {
                    return buffer.ToArray();
                }
            }

            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
            {
                return buffer.ToArray();
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    internal sealed class BufferedTestStreamReader
    {
        private readonly Stream _stream;
        private byte[] _buffer = new byte[4096];
        private int _offset;
        private int _count;

        public BufferedTestStreamReader(Stream stream)
        {
            _stream = stream;
        }

        public async Task<byte[]?> ReadHeaderBlockAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var markerIndex = _buffer.AsSpan(_offset, _count - _offset).IndexOf("\r\n\r\n"u8);
                if (markerIndex >= 0)
                {
                    var bytes = new byte[markerIndex + 4];
                    Buffer.BlockCopy(_buffer, _offset, bytes, 0, bytes.Length);
                    _offset += bytes.Length;
                    return bytes;
                }

                Compact();
                if (_count == _buffer.Length)
                {
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                }

                var read = await _stream.ReadAsync(_buffer.AsMemory(_count), cancellationToken);
                if (read == 0)
                {
                    return _count == _offset ? null : throw new IOException("Unexpected EOF while reading origin request.");
                }

                _count += read;
            }
        }

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            var written = 0;
            while (written < count)
            {
                if (_count > _offset)
                {
                    var available = Math.Min(count - written, _count - _offset);
                    Buffer.BlockCopy(_buffer, _offset, buffer, written, available);
                    _offset += available;
                    written += available;
                    continue;
                }

                var read = await _stream.ReadAsync(buffer.AsMemory(written, count - written), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Unexpected EOF while reading origin body.");
                }

                written += read;
            }

            return buffer;
        }

        public async Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            if (_count > _offset)
            {
                var available = Math.Min(buffer.Length, _count - _offset);
                Buffer.BlockCopy(_buffer, _offset, buffer, 0, available);
                _offset += available;
                return available;
            }

            return await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var markerIndex = _buffer.AsSpan(_offset, _count - _offset).IndexOf("\r\n"u8);
                if (markerIndex >= 0)
                {
                    var bytes = new byte[markerIndex];
                    Buffer.BlockCopy(_buffer, _offset, bytes, 0, bytes.Length);
                    _offset += markerIndex + 2;
                    return Encoding.ASCII.GetString(bytes);
                }

                Compact();
                if (_count == _buffer.Length)
                {
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                }

                var read = await _stream.ReadAsync(_buffer.AsMemory(_count), cancellationToken);
                if (read == 0)
                {
                    return _count == _offset ? null : throw new IOException("Unexpected EOF while reading line.");
                }

                _count += read;
            }
        }

        private void Compact()
        {
            if (_offset == 0)
            {
                return;
            }

            if (_offset == _count)
            {
                _offset = 0;
                _count = 0;
                return;
            }

            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _count - _offset);
            _count -= _offset;
            _offset = 0;
        }
    }
}
