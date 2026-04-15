using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Server.Http;

public sealed class HttpReverseProxyServer
{
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
        "Expect",
        "Via",
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto"
    };

    private readonly TcpListener _listener;
    private readonly TunnelServer _tunnelServer;

    internal int ActiveReverseHttpSessionCount => _tunnelServer.ActiveReverseHttpSessionCount;

    public HttpReverseProxyServer(IPAddress listenAddress, int port, TunnelServer tunnelServer)
    {
        _listener = new TcpListener(listenAddress, port);
        _tunnelServer = tunnelServer;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        StructuredLog.Info("http-reverse-proxy", "HTTP", $"listening on {_listener.LocalEndpoint}");

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
            _listener.Stop();
            StructuredLog.Info("http-reverse-proxy", "HTTP", "listener stopped");
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
                StructuredLog.Error("http-reverse-proxy", "HTTP", "client handling failed", ex);
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
            catch (NotSupportedException)
            {
                await WriteSimpleResponseAsync(stream, 501, "Not Implemented", closeConnection: true, cancellationToken);
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

            StructuredLog.Info(
                "http-reverse-proxy",
                "HTTP",
                $"{request.Method} rawTarget={SummarizeForLog(request.RawTarget)} decodedTarget={SummarizeForLog(request.DecodedTarget)} parsedPath={request.Path} hasQuery={request.HasQuery} queryLength={request.Query.Length} queryPreview={SummarizeForLog(request.Query, 96)} hostHeader={request.HostHeader ?? "<none>"} scheme={request.Scheme} authority={request.Authority}");
            if (request.IsConnect || string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                await WriteSimpleResponseAsync(stream, 501, "Not Implemented", closeConnection: true, cancellationToken);
                return;
            }

            if (request.IsWebSocketUpgrade)
            {
                await WriteSimpleResponseAsync(stream, 501, "WebSocket Not Supported", closeConnection: true, cancellationToken);
                StructuredLog.Warn("http-reverse-proxy", "HTTP", "websocket upgrade is not supported");
                return;
            }

            await using var response = await ExecuteViaTunnelAsync(request, cancellationToken);
            var keepAlive = request.ShouldKeepAlive && !response.CloseConnection;
            await WriteResponseAsync(stream, request.Method, response, keepAlive, cancellationToken);
            if (!keepAlive)
            {
                return;
            }
        }
    }

    private async Task<ProxyExecutionResponse> ExecuteViaTunnelAsync(ParsedHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (sessionId, response, reader, closeAsync) = await _tunnelServer.ExecuteReverseHttpRequestAsync(
                request.ToTunnelRequestStart(),
                request.Body,
                cancellationToken);

            return ProxyExecutionResponse.ForTunnel(response, reader, async () =>
            {
                try
                {
                    await closeAsync();
                }
                catch
                {
                }
            });
        }
        catch (InvalidOperationException)
        {
            return ProxyExecutionResponse.ForStatus(502, "Bad Gateway");
        }
        catch (TaskCanceledException)
        {
            return ProxyExecutionResponse.ForStatus(504, "Gateway Timeout");
        }
        catch (TimeoutException)
        {
            return ProxyExecutionResponse.ForStatus(504, "Gateway Timeout");
        }
        catch (IOException)
        {
            return ProxyExecutionResponse.ForStatus(502, "Bad Gateway");
        }
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
            using (chunk)
            {
                if (chunk.IsEmpty)
                {
                    continue;
                }

                if (useChunked)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"), cancellationToken);
                }

                await stream.WriteAsync(chunk.Memory, cancellationToken);
                if (useChunked)
                {
                    await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
                }
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

    private static string SummarizeForLog(string value, int maxLength = 160)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength]}...(len={value.Length})";
    }

    private sealed class ProxyExecutionResponse : IAsyncDisposable
    {
        private readonly Func<CancellationToken, IAsyncEnumerable<OwnedPayloadChunk>> _bodyFactory;
        private readonly Func<ValueTask> _disposeAsync;

        private ProxyExecutionResponse(
            int statusCode,
            string reasonPhrase,
            IReadOnlyList<HttpHeader> headers,
            bool closeConnection,
            Func<CancellationToken, IAsyncEnumerable<OwnedPayloadChunk>> bodyFactory,
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
            ChannelReader<OwnedPayloadChunk> reader,
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

        public async IAsyncEnumerable<OwnedPayloadChunk> ReadBodyAsync([EnumeratorCancellation] CancellationToken cancellationToken)
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

        private static async IAsyncEnumerable<OwnedPayloadChunk> ReadChannelBodyAsync(
            ChannelReader<OwnedPayloadChunk> reader,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }

        private static async IAsyncEnumerable<OwnedPayloadChunk> EmptyBodyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ParsedHttpRequest
    {
        private ParsedHttpRequest(
            string method,
            string rawTarget,
            string decodedTarget,
            string scheme,
            string authority,
            string path,
            string query,
            string pathAndQuery,
            string? hostHeader,
            Version version,
            IReadOnlyList<HttpHeader> headers,
            IReadOnlyList<HttpHeader> sanitizedHeaders,
            byte[] body,
            bool shouldKeepAlive,
            bool isConnect,
            bool isWebSocketUpgrade)
        {
            Method = method;
            RawTarget = rawTarget;
            DecodedTarget = decodedTarget;
            Scheme = scheme;
            Authority = authority;
            Path = path;
            Query = query;
            PathAndQuery = pathAndQuery;
            HostHeader = hostHeader;
            Version = version;
            Headers = headers;
            SanitizedHeaders = sanitizedHeaders;
            Body = body;
            ShouldKeepAlive = shouldKeepAlive;
            IsConnect = isConnect;
            IsWebSocketUpgrade = isWebSocketUpgrade;
        }

        public string Method { get; }
        public string RawTarget { get; }
        public string DecodedTarget { get; }
        public string Scheme { get; }
        public string Authority { get; }
        public string Path { get; }
        public string Query { get; }
        public bool HasQuery => Query.Length > 0;
        public string PathAndQuery { get; }
        public string? HostHeader { get; }
        public Version Version { get; }
        public IReadOnlyList<HttpHeader> Headers { get; }
        public IReadOnlyList<HttpHeader> SanitizedHeaders { get; }
        public byte[] Body { get; }
        public bool ShouldKeepAlive { get; }
        public bool IsConnect { get; }
        public bool IsWebSocketUpgrade { get; }

        public HttpRequestStart ToTunnelRequestStart()
        {
            return new HttpRequestStart(
                Method,
                Scheme,
                Authority,
                string.IsNullOrEmpty(PathAndQuery) ? "/" : PathAndQuery,
                (byte)Version.Major,
                (byte)Version.Minor,
                SanitizedHeaders);
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
            var normalizedTargetText = DecodeRequestTarget(targetText);
            var isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
            if (!requestLine[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Unsupported HTTP version.");
            }

            var version = Version.Parse(requestLine[2][5..]);
            var headers = ParseHeaders(lines.Skip(1).TakeWhile(static line => line.Length > 0));
            var host = headers.FirstOrDefault(static h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)).Value;
            var parsedTarget = ParseRequestTarget(normalizedTargetText, host);

            var sanitizedHeaders = SanitizeHeaders(headers, parsedTarget.Authority);
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
                targetText,
                normalizedTargetText,
                parsedTarget.Scheme,
                parsedTarget.Authority,
                parsedTarget.Path,
                parsedTarget.Query,
                parsedTarget.PathAndQuery,
                host,
                version,
                headers,
                sanitizedHeaders,
                body,
                DetermineKeepAlive(version, headers),
                isConnect,
                isWebSocketUpgrade);
        }

        private static string DecodeRequestTarget(string targetText)
        {
            if (string.IsNullOrWhiteSpace(targetText) || targetText.IndexOf('%') < 0)
            {
                return targetText;
            }

            var decodedTarget = targetText;
            for (var pass = 0; pass < 2; pass++)
            {
                string nextTarget;
                try
                {
                    nextTarget = Uri.UnescapeDataString(decodedTarget);
                }
                catch (UriFormatException)
                {
                    break;
                }

                if (string.Equals(nextTarget, decodedTarget, StringComparison.Ordinal))
                {
                    break;
                }

                decodedTarget = nextTarget;
            }

            return decodedTarget;
        }

        private static ParsedRequestTarget ParseRequestTarget(string decodedTarget, string? hostHeader)
        {
            if (Uri.TryCreate(decodedTarget, UriKind.Absolute, out var absoluteUri))
            {
                var absolutePath = string.IsNullOrEmpty(absoluteUri.AbsolutePath) ? "/" : absoluteUri.AbsolutePath;
                var absoluteQuery = absoluteUri.Query;
                return new ParsedRequestTarget(
                    absoluteUri.Scheme,
                    absoluteUri.Authority,
                    absolutePath,
                    absoluteQuery,
                    absolutePath + absoluteQuery);
            }

            if (string.IsNullOrWhiteSpace(hostHeader))
            {
                throw new InvalidDataException("Reverse proxy requires Host header.");
            }

            SplitPathAndQuery(decodedTarget, out var path, out var query);
            return new ParsedRequestTarget(
                Uri.UriSchemeHttp,
                hostHeader,
                path,
                query,
                path + query);
        }

        private static void SplitPathAndQuery(string target, out string path, out string query)
        {
            var normalizedTarget = string.IsNullOrWhiteSpace(target) ? "/" : target;
            var queryIndex = normalizedTarget.IndexOf('?');
            if (queryIndex < 0)
            {
                path = normalizedTarget;
                query = string.Empty;
                return;
            }

            path = queryIndex == 0 ? "/" : normalizedTarget[..queryIndex];
            query = normalizedTarget[queryIndex..];
        }

        private readonly record struct ParsedRequestTarget(
            string Scheme,
            string Authority,
            string Path,
            string Query,
            string PathAndQuery);

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

        private static IReadOnlyList<HttpHeader> SanitizeHeaders(IReadOnlyList<HttpHeader> headers, string authority)
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
                if (HopByHopHeaders.Contains(header.Name) || connectionTokens.Contains(header.Name))
                {
                    continue;
                }

                sanitized.Add(header);
            }

            if (!sanitized.Any(static h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)))
            {
                sanitized.Add(new HttpHeader("Host", authority));
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
