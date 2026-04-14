using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Http;

public sealed class HttpProxyServer
{
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedDirectHttpClient = CreateHttpClient();
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Expect"
    };

    private readonly TcpListener _listener;
    private readonly List<(string Host, int Port)> _remoteServers = new();
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelConnectionPolicy _connectionPolicy;
    private readonly byte _protocolVersion;
    private readonly bool _enableCompression;
    private readonly PayloadCompressionAlgorithm _compressionAlgorithm;
    private readonly TrafficRoutingPolicy? _routingPolicy;
    private readonly Action<Socket>? _configureTunnelSocket;
    private List<TunnelClientMultiplexer>? _multiplexers;
    private int _nextMultiplexerIndex = -1;

    internal int ActiveTunnelHttpSessionCount
    {
        get
        {
            var multiplexers = _multiplexers;
            if (multiplexers is null)
            {
                return 0;
            }

            var total = 0;
            foreach (var multiplexer in multiplexers)
            {
                total += multiplexer.ActiveHttpSessionCount;
            }

            return total;
        }
    }

    public HttpProxyServer(
        IPAddress listenAddress,
        int port,
        string remoteServerHost,
        int remoteServerPort,
        string sharedKey,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TunnelConnectionPolicy? connectionPolicy = null,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate,
        TrafficRoutingPolicy? routingPolicy = null,
        Action<Socket>? configureTunnelSocket = null)
    {
        _listener = new TcpListener(listenAddress, port);
        _remoteServers.Add((remoteServerHost, remoteServerPort));
        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
        _cryptoPolicy = cryptoPolicy ?? TunnelCryptoPolicy.Default;
        _connectionPolicy = connectionPolicy ?? TunnelConnectionPolicy.Default;
        _protocolVersion = protocolVersion;
        _enableCompression = enableCompression;
        _compressionAlgorithm = compressionAlgorithm;
        _routingPolicy = routingPolicy;
        _configureTunnelSocket = configureTunnelSocket;
    }

    public HttpProxyServer(
        IPAddress listenAddress,
        int port,
        IReadOnlyList<(string Host, int Port)> remoteServers,
        string sharedKey,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TunnelConnectionPolicy? connectionPolicy = null,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate,
        TrafficRoutingPolicy? routingPolicy = null,
        Action<Socket>? configureTunnelSocket = null)
    {
        _listener = new TcpListener(listenAddress, port);
        foreach (var (host, serverPort) in remoteServers)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                _remoteServers.Add((host, serverPort));
            }
        }

        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
        _cryptoPolicy = cryptoPolicy ?? TunnelCryptoPolicy.Default;
        _connectionPolicy = connectionPolicy ?? TunnelConnectionPolicy.Default;
        _protocolVersion = protocolVersion;
        _enableCompression = enableCompression;
        _compressionAlgorithm = compressionAlgorithm;
        _routingPolicy = routingPolicy;
        _configureTunnelSocket = configureTunnelSocket;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        StructuredLog.Info("http-proxy", "HTTP", $"listening on {_listener.LocalEndpoint}");
        if (_remoteServers.Count > 0)
        {
            _multiplexers = new List<TunnelClientMultiplexer>(_remoteServers.Count);
            foreach (var (host, serverPort) in _remoteServers)
            {
                _multiplexers.Add(new TunnelClientMultiplexer(
                    host,
                    serverPort,
                    _sharedKey,
                    _cryptoPolicy,
                    _connectionPolicy,
                    _protocolVersion,
                    _enableCompression,
                    _compressionAlgorithm,
                    _configureTunnelSocket));
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientSafelyAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_multiplexers is not null)
            {
                foreach (var multiplexer in _multiplexers)
                {
                    await multiplexer.DisposeAsync();
                }
            }

            _listener.Stop();
            StructuredLog.Info("http-proxy", "HTTP", "listener stopped");
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                StructuredLog.Error("http-proxy", "HTTP", "client handling failed", ex);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var reader = new BufferedStreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            ParsedHttpRequest? request;
            try
            {
                request = await ParsedHttpRequest.ReadAsync(reader, stream, cancellationToken);
            }
            catch (NotSupportedException ex)
            {
                await WriteSimpleResponseAsync(stream, 501, "Not Implemented", closeConnection: true, cancellationToken);
                StructuredLog.Warn("http-proxy", "HTTP", ex.Message);
                return;
            }
            catch (InvalidDataException)
            {
                await WriteSimpleResponseAsync(stream, 400, "Bad Request", closeConnection: true, cancellationToken);
                return;
            }

            if (request is null)
            {
                return;
            }

            StructuredLog.Info("http-proxy", "HTTP", $"{request.Method} {request.TargetUri}");
            if (request.IsConnect || string.Equals(request.TargetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                await WriteSimpleResponseAsync(stream, 501, "Not Implemented", closeConnection: true, cancellationToken);
                return;
            }

            if (request.IsWebSocketUpgrade)
            {
                await WriteSimpleResponseAsync(stream, 501, "WebSocket Not Supported", closeConnection: true, cancellationToken);
                StructuredLog.Warn("http-proxy", "HTTP", "websocket upgrade is not supported");
                return;
            }

            var decision = ResolveRoutingDecision(request);
            if (decision == null)
            {
                await WriteSimpleResponseAsync(stream, 403, "Forbidden", closeConnection: true, cancellationToken);
                return;
            }

            await using var response = decision.Value switch
            {
                TrafficRouteDecision.Direct => await ExecuteDirectAsync(request, cancellationToken),
                TrafficRouteDecision.Drop => ProxyExecutionResponse.ForStatus(403, "Forbidden"),
                TrafficRouteDecision.Tunnel => await ExecuteViaTunnelAsync(request, cancellationToken),
                _ => ProxyExecutionResponse.ForStatus(500, "Internal Server Error")
            };

            var keepAlive = request.ShouldKeepAlive && !response.CloseConnection;
            await WriteResponseAsync(stream, request.Method, response, keepAlive, cancellationToken);
            if (!keepAlive)
            {
                return;
            }
        }
    }

    private TrafficRouteDecision? ResolveRoutingDecision(ParsedHttpRequest request)
    {
        var routeRequest = request.ToRoutingRequest();
        var matchedRule = _routingPolicy?.Match(routeRequest);
        if (matchedRule is not null)
        {
            return matchedRule.Decision;
        }

        if (_routingPolicy is not null)
        {
            return null;
        }

        return SelectMultiplexersForClient().Count == 0
            ? TrafficRouteDecision.Direct
            : TrafficRouteDecision.Tunnel;
    }

    private async Task<ProxyExecutionResponse> ExecuteViaTunnelAsync(ParsedHttpRequest request, CancellationToken cancellationToken)
    {
        var multiplexers = SelectMultiplexersForClient();
        if (multiplexers.Count == 0)
        {
            return ProxyExecutionResponse.ForStatus(502, "Bad Gateway");
        }

        Exception? lastError = null;
        foreach (var multiplexer in multiplexers)
        {
            try
            {
                var requestStart = request.ToTunnelRequestStart();
                var (sessionId, response, reader) = await multiplexer.ExecuteHttpRequestAsync(requestStart, request.Body, cancellationToken);

                return ProxyExecutionResponse.ForTunnel(
                    response,
                    reader,
                    async () =>
                    {
                        try { await multiplexer.CloseSessionAsync(sessionId, 0x00, CancellationToken.None); } catch { }
                    });
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        return lastError switch
        {
            TaskCanceledException => ProxyExecutionResponse.ForStatus(504, "Gateway Timeout"),
            TimeoutException => ProxyExecutionResponse.ForStatus(504, "Gateway Timeout"),
            _ => ProxyExecutionResponse.ForStatus(502, "Bad Gateway")
        };
    }

    private static async Task<ProxyExecutionResponse> ExecuteDirectAsync(ParsedHttpRequest request, CancellationToken cancellationToken)
    {
        var message = BuildHttpRequestMessage(request);
        try
        {
            var response = await SharedDirectHttpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return ProxyExecutionResponse.ForDirect(response, message);
        }
        catch (TaskCanceledException)
        {
            message.Dispose();
            return ProxyExecutionResponse.ForStatus(504, "Gateway Timeout");
        }
        catch (TimeoutException)
        {
            message.Dispose();
            return ProxyExecutionResponse.ForStatus(504, "Gateway Timeout");
        }
        catch (HttpRequestException)
        {
            message.Dispose();
            return ProxyExecutionResponse.ForStatus(502, "Bad Gateway");
        }
    }

    private static HttpRequestMessage BuildHttpRequestMessage(ParsedHttpRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.TargetUri)
        {
            Version = request.Version,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        ByteArrayContent? content = null;
        if (request.Body.Length > 0)
        {
            content = new ByteArrayContent(request.Body);
            message.Content = content;
        }

        foreach (var header in request.SanitizedHeaders)
        {
            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Host = header.Value;
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                message.Content ??= content = new ByteArrayContent(Array.Empty<byte>());
                message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return message;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string requestMethod,
        ProxyExecutionResponse response,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        var headers = response.GetSanitizedHeaders();
        var contentLength = headers
            .Where(static h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(static h => long.TryParse(h.Value, out var parsed) ? parsed : -1L)
            .FirstOrDefault(static value => value >= 0);
        var hasBody = !string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
            && response.StatusCode is not 204 and not 304;
        var useChunked = hasBody && contentLength < 0;

        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
            .Append(response.StatusCode)
            .Append(' ')
            .Append(string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "OK" : response.ReasonPhrase)
            .Append("\r\n");

        foreach (var header in headers)
        {
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        if (hasBody)
        {
            if (contentLength >= 0)
            {
                builder.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            }
            else
            {
                builder.Append("Transfer-Encoding: chunked\r\n");
            }
        }
        else
        {
            builder.Append("Content-Length: 0\r\n");
        }

        builder.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken);

        if (!hasBody)
        {
            return;
        }

        await foreach (var chunk in response.ReadBodyAsync(cancellationToken))
        {
            if (chunk.IsEmpty)
            {
                continue;
            }

            if (useChunked)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"), cancellationToken);
            }

            await stream.WriteAsync(chunk, cancellationToken);
            if (useChunked)
            {
                await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
            }
        }

        if (useChunked)
        {
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
        }
    }

    private static async Task WriteSimpleResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        bool closeConnection,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Length: 0\r\nConnection: {(closeConnection ? "close" : "keep-alive")}\r\n\r\n");
        await stream.WriteAsync(payload, cancellationToken);
    }

    private IReadOnlyList<TunnelClientMultiplexer> SelectMultiplexersForClient()
    {
        var multiplexers = _multiplexers;
        if (multiplexers is null || multiplexers.Count == 0)
        {
            return Array.Empty<TunnelClientMultiplexer>();
        }

        var index = Interlocked.Increment(ref _nextMultiplexerIndex);
        var startIndex = (index & int.MaxValue) % multiplexers.Count;
        if (multiplexers.Count == 1)
        {
            return multiplexers;
        }

        var ordered = new TunnelClientMultiplexer[multiplexers.Count];
        for (var offset = 0; offset < multiplexers.Count; offset++)
        {
            ordered[offset] = multiplexers[(startIndex + offset) % multiplexers.Count];
        }

        return ordered;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = UpstreamConnectTimeout
        };

        return new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private sealed class ProxyExecutionResponse : IAsyncDisposable
    {
        private readonly Func<CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> _bodyFactory;
        private readonly Func<ValueTask> _disposeAsync;

        private ProxyExecutionResponse(
            int statusCode,
            string reasonPhrase,
            IReadOnlyList<HttpHeader> headers,
            bool closeConnection,
            Func<CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> bodyFactory,
            Func<ValueTask> disposeAsync)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            Headers = headers;
            CloseConnection = closeConnection;
            _bodyFactory = bodyFactory;
            _disposeAsync = disposeAsync;
        }

        public int StatusCode { get; }
        public string ReasonPhrase { get; }
        public IReadOnlyList<HttpHeader> Headers { get; }
        public bool CloseConnection { get; }

        public static ProxyExecutionResponse ForStatus(int statusCode, string reasonPhrase)
        {
            return new ProxyExecutionResponse(
                statusCode,
                reasonPhrase,
                [],
                closeConnection: true,
                static _ => EmptyBodyAsync(),
                static () => ValueTask.CompletedTask);
        }

        public static ProxyExecutionResponse ForTunnel(
            HttpResponseStart response,
            ChannelReader<byte[]> reader,
            Func<Task> closeAsync)
        {
            return new ProxyExecutionResponse(
                response.StatusCode,
                response.ReasonPhrase,
                response.Headers,
                closeConnection: false,
                cancellationToken => ReadChannelBodyAsync(reader, cancellationToken),
                () => new ValueTask(closeAsync()));
        }

        public static ProxyExecutionResponse ForDirect(HttpResponseMessage response, HttpRequestMessage request)
        {
            var headers = new List<HttpHeader>();
            foreach (var header in response.Headers)
            {
                foreach (var value in header.Value)
                {
                    headers.Add(new HttpHeader(header.Key, value));
                }
            }

            if (response.Content is not null)
            {
                foreach (var header in response.Content.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        headers.Add(new HttpHeader(header.Key, value));
                    }
                }
            }

            return new ProxyExecutionResponse(
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                headers,
                closeConnection: false,
                cancellationToken => ReadHttpBodyAsync(response, cancellationToken),
                () =>
                {
                    response.Dispose();
                    request.Dispose();
                    return ValueTask.CompletedTask;
                });
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadBodyAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in _bodyFactory(cancellationToken).WithCancellation(cancellationToken))
            {
                yield return chunk;
            }
        }

        public IReadOnlyList<HttpHeader> GetSanitizedHeaders()
        {
            var connectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in Headers)
            {
                if (!header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var token in header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    connectionTokens.Add(token);
                }
            }

            var sanitized = new List<HttpHeader>(Headers.Count);
            foreach (var header in Headers)
            {
                if (HopByHopHeaders.Contains(header.Name) || connectionTokens.Contains(header.Name))
                {
                    continue;
                }

                sanitized.Add(header);
            }

            return sanitized;
        }

        public ValueTask DisposeAsync() => _disposeAsync();

        private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChannelBodyAsync(
            ChannelReader<byte[]> reader,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }

        private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadHttpBodyAsync(
            HttpResponseMessage response,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (response.Content is null)
            {
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16 * 1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    yield break;
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                yield return chunk;
            }
        }

        private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyBodyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ParsedHttpRequest
    {
        private ParsedHttpRequest(
            string method,
            Uri targetUri,
            Version version,
            IReadOnlyList<HttpHeader> headers,
            IReadOnlyList<HttpHeader> sanitizedHeaders,
            byte[] body,
            bool shouldKeepAlive,
            bool isConnect,
            bool isWebSocketUpgrade)
        {
            Method = method;
            TargetUri = targetUri;
            Version = version;
            Headers = headers;
            SanitizedHeaders = sanitizedHeaders;
            Body = body;
            ShouldKeepAlive = shouldKeepAlive;
            IsConnect = isConnect;
            IsWebSocketUpgrade = isWebSocketUpgrade;
        }

        public string Method { get; }
        public Uri TargetUri { get; }
        public Version Version { get; }
        public IReadOnlyList<HttpHeader> Headers { get; }
        public IReadOnlyList<HttpHeader> SanitizedHeaders { get; }
        public byte[] Body { get; }
        public bool ShouldKeepAlive { get; }
        public bool IsConnect { get; }
        public bool IsWebSocketUpgrade { get; }

        public HttpRequestStart ToTunnelRequestStart()
        {
            var authority = TargetUri.IsDefaultPort
                ? TargetUri.Host
                : $"{TargetUri.Host}:{TargetUri.Port}";
            var pathAndQuery = string.IsNullOrEmpty(TargetUri.PathAndQuery) ? "/" : TargetUri.PathAndQuery;
            return new HttpRequestStart(
                Method,
                TargetUri.Scheme,
                authority,
                pathAndQuery,
                (byte)Version.Major,
                (byte)Version.Minor,
                SanitizedHeaders);
        }

        public Socks5ConnectRequest ToRoutingRequest()
        {
            var host = TargetUri.Host;
            var addressType = IPAddress.TryParse(host, out var ipAddress)
                ? ipAddress.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x04 : (byte)0x01
                : (byte)0x03;
            return new Socks5ConnectRequest(0x01, host, TargetUri.Port, addressType);
        }

        public static async Task<ParsedHttpRequest?> ReadAsync(
            BufferedStreamReader reader,
            NetworkStream networkStream,
            CancellationToken cancellationToken)
        {
            var headerBytes = await reader.ReadHeaderBlockAsync(cancellationToken);
            if (headerBytes is null)
            {
                return null;
            }

            var headerText = Encoding.Latin1.GetString(headerBytes);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            if (lines.Length < 2)
            {
                throw new InvalidDataException("HTTP request is missing headers.");
            }

            var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length != 3)
            {
                throw new InvalidDataException("Invalid HTTP request line.");
            }

            var method = requestLine[0].Trim();
            var targetText = requestLine[1].Trim();
            var isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
            if (!requestLine[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Unsupported HTTP version.");
            }

            var version = Version.Parse(requestLine[2][5..]);
            Uri? targetUri;
            if (isConnect)
            {
                targetUri = Uri.TryCreate($"https://{targetText}/", UriKind.Absolute, out var parsedConnectUri)
                    ? parsedConnectUri
                    : null;
            }
            else
            {
                targetUri = Uri.TryCreate(targetText, UriKind.Absolute, out var parsedAbsoluteUri)
                    ? parsedAbsoluteUri
                    : null;
            }

            if (targetUri is null)
            {
                throw new InvalidDataException("HTTP proxy requires absolute-form request target.");
            }

            var headers = ParseHeaders(lines.Skip(1).TakeWhile(static line => line.Length > 0));
            var sanitizedHeaders = SanitizeHeaders(headers, targetUri);
            var isWebSocketUpgrade = IsWebSocketUpgradeRequest(headers);
            var expectContinue = headers.Any(static h => h.Name.Equals("Expect", StringComparison.OrdinalIgnoreCase)
                && h.Value.Contains("100-continue", StringComparison.OrdinalIgnoreCase));
            if (expectContinue)
            {
                await networkStream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken);
            }

            var body = await ReadBodyAsync(reader, headers, cancellationToken);
            return new ParsedHttpRequest(
                method,
                targetUri,
                version,
                headers,
                sanitizedHeaders,
                body,
                DetermineKeepAlive(version, headers),
                isConnect,
                isWebSocketUpgrade);
        }

        private static bool IsWebSocketUpgradeRequest(IReadOnlyList<HttpHeader> headers)
        {
            var hasUpgradeConnectionToken = headers
                .Where(static h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                .SelectMany(static h => h.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Any(static token => token.Equals("Upgrade", StringComparison.OrdinalIgnoreCase));
            var hasWebSocketUpgradeHeader = headers
                .Where(static h => h.Name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                .Any(static h => h.Value.Equals("websocket", StringComparison.OrdinalIgnoreCase));

            return hasUpgradeConnectionToken && hasWebSocketUpgradeHeader;
        }

        private static async Task<byte[]> ReadBodyAsync(
            BufferedStreamReader reader,
            IReadOnlyList<HttpHeader> headers,
            CancellationToken cancellationToken)
        {
            var transferEncoding = headers
                .FirstOrDefault(static h => h.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                .Value;
            var contentLength = headers
                .FirstOrDefault(static h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Value;

            if (!string.IsNullOrWhiteSpace(transferEncoding))
            {
                if (!transferEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault()?
                    .Equals("chunked", StringComparison.OrdinalIgnoreCase) ?? true)
                {
                    throw new NotSupportedException("Only chunked request transfer-encoding is supported.");
                }

                return await reader.ReadChunkedBodyAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(contentLength))
            {
                return [];
            }

            if (!int.TryParse(contentLength, out var parsedLength) || parsedLength < 0)
            {
                throw new InvalidDataException("Invalid Content-Length.");
            }

            return await reader.ReadBytesAsync(parsedLength, cancellationToken);
        }

        private static bool DetermineKeepAlive(Version version, IReadOnlyList<HttpHeader> headers)
        {
            var connectionValue = headers
                .Where(static h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                .Select(static h => h.Value)
                .FirstOrDefault();
            if (version <= HttpVersion.Version10)
            {
                return connectionValue?.Contains("keep-alive", StringComparison.OrdinalIgnoreCase) == true;
            }

            return connectionValue?.Contains("close", StringComparison.OrdinalIgnoreCase) != true;
        }

        private static HttpHeader[] ParseHeaders(IEnumerable<string> lines)
        {
            var headers = new List<HttpHeader>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    throw new InvalidDataException("Invalid HTTP header line.");
                }

                headers.Add(new HttpHeader(
                    line[..separatorIndex].Trim(),
                    line[(separatorIndex + 1)..].Trim()));
            }

            return headers.ToArray();
        }

        private static IReadOnlyList<HttpHeader> SanitizeHeaders(IReadOnlyList<HttpHeader> headers, Uri targetUri)
        {
            var connectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                if (!header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var token in header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    connectionTokens.Add(token);
                }
            }

            var sanitized = new List<HttpHeader>(headers.Count);
            foreach (var header in headers)
            {
                if (HopByHopHeaders.Contains(header.Name)
                    || connectionTokens.Contains(header.Name)
                    || header.Name.Equals("Via", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sanitized.Add(header);
            }

            if (!sanitized.Any(static h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)))
            {
                var host = targetUri.IsDefaultPort ? targetUri.Host : $"{targetUri.Host}:{targetUri.Port}";
                sanitized.Add(new HttpHeader("Host", host));
            }

            return sanitized;
        }
    }

    private sealed class BufferedStreamReader
    {
        private readonly Stream _stream;
        private byte[] _buffer = new byte[8192];
        private int _offset;
        private int _count;

        public BufferedStreamReader(Stream stream)
        {
            _stream = stream;
        }

        public async Task<byte[]?> ReadHeaderBlockAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var terminatorIndex = IndexOf("\r\n\r\n"u8);
                if (terminatorIndex >= 0)
                {
                    return ReadBufferedBytes(terminatorIndex + 4);
                }

                if (_count - _offset >= 64 * 1024)
                {
                    throw new InvalidDataException("HTTP header block is too large.");
                }

                var read = await ReadMoreAsync(cancellationToken);
                if (read == 0)
                {
                    if (_count == _offset)
                    {
                        return null;
                    }

                    throw new IOException("Unexpected EOF while reading HTTP headers.");
                }
            }
        }

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return [];
            }

            var result = new byte[count];
            var written = 0;
            while (written < count)
            {
                if (_count > _offset)
                {
                    var available = Math.Min(count - written, _count - _offset);
                    Buffer.BlockCopy(_buffer, _offset, result, written, available);
                    _offset += available;
                    written += available;
                    continue;
                }

                var read = await _stream.ReadAsync(result.AsMemory(written, count - written), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Unexpected EOF while reading HTTP body.");
                }

                written += read;
            }

            return result;
        }

        public async Task<byte[]> ReadChunkedBodyAsync(CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var line = await ReadLineAsync(cancellationToken);
                var separatorIndex = line.IndexOf(';');
                var sizeText = separatorIndex >= 0 ? line[..separatorIndex] : line;
                if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                {
                    throw new InvalidDataException("Invalid chunk size.");
                }

                if (size == 0)
                {
                    while (!string.IsNullOrEmpty(await ReadLineAsync(cancellationToken)))
                    {
                    }

                    return stream.ToArray();
                }

                var chunk = await ReadBytesAsync(size, cancellationToken);
                await stream.WriteAsync(chunk, cancellationToken);
                var chunkTerminator = await ReadBytesAsync(2, cancellationToken);
                if (chunkTerminator[0] != '\r' || chunkTerminator[1] != '\n')
                {
                    throw new InvalidDataException("Invalid chunk terminator.");
                }
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var lineEnding = IndexOf("\r\n"u8);
                if (lineEnding >= 0)
                {
                    var lineBytes = ReadBufferedBytes(lineEnding);
                    _offset += 2;
                    return Encoding.ASCII.GetString(lineBytes);
                }

                if (_count - _offset >= 16 * 1024)
                {
                    throw new InvalidDataException("HTTP line is too long.");
                }

                var read = await ReadMoreAsync(cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Unexpected EOF while reading HTTP line.");
                }
            }
        }

        private async Task<int> ReadMoreAsync(CancellationToken cancellationToken)
        {
            Compact();
            EnsureCapacity(_count + 4096);
            var read = await _stream.ReadAsync(_buffer.AsMemory(_count), cancellationToken);
            _count += read;
            return read;
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

        private void EnsureCapacity(int capacity)
        {
            if (_buffer.Length >= capacity)
            {
                return;
            }

            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, capacity));
        }

        private int IndexOf(ReadOnlySpan<byte> marker)
        {
            var span = _buffer.AsSpan(_offset, _count - _offset);
            var index = span.IndexOf(marker);
            return index < 0 ? -1 : index;
        }

        private byte[] ReadBufferedBytes(int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(_buffer, _offset, result, 0, count);
            _offset += count;
            return result;
        }
    }
}
