using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed partial class TunnelServer
{
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
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

    private static Task HandleHttpRequestFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!ProtocolConstants.SupportsHttpRelay(writeOptions.Version))
        {
            return Task.CompletedTask;
        }

        if (!pendingConnectSessions.TryAdd(frame.SessionId, 0))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (frame.Sequence != 0 || sessions.ContainsKey(frame.SessionId))
            {
                return Task.CompletedTask;
            }

            HttpRequestStart requestStart;
            try
            {
                requestStart = ProtocolPayloadSerializer.DeserializeHttpRequestStart(frame.Payload.Span);
            }
            catch
            {
                return Task.CompletedTask;
            }

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var context = new HttpSessionContext(frame.SessionId, requestStart, sessionCts);
            context.MarkConnectReceived();
            if (!sessions.TryAdd(frame.SessionId, context))
            {
                context.Dispose();
                return Task.CompletedTask;
            }

            StructuredLog.Info(
                "tunnel-server",
                "TUNNEL/HTTP",
                $"{requestStart.Method} {requestStart.Authority}{requestStart.PathAndQuery}",
                frame.SessionId);
        }
        finally
        {
            pendingConnectSessions.TryRemove(frame.SessionId, out _);
        }

        return Task.CompletedTask;
    }

    private static async Task HandleHttpRequestEndFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(frame.SessionId, out var session))
        {
            return;
        }

        if (session is not HttpSessionContext httpSession)
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        var incomingSequenceResult = session.EvaluateIncomingSequence(frame.Sequence);
        if (incomingSequenceResult != IncomingSequenceResult.Accepted || !httpSession.TryMarkRequestCompleted())
        {
            await HandleIncomingSequenceMismatchAsync(
                secureStream,
                writeLock,
                sessions,
                session,
                frame.SessionId,
                incomingSequenceResult,
                writeOptions,
                CancellationToken.None);
            return;
        }

        if (ProtocolConstants.SupportsSelectiveRecovery(writeOptions.Version))
        {
            await SendAckAsync(
                secureStream,
                frame.SessionId,
                frame.Sequence,
                writeLock,
                writeOptions,
                cancellationToken);
        }

        _ = Task.Run(
            () => ProcessHttpSessionAsync(
                secureStream,
                writeLock,
                sessions,
                httpSession,
                writeOptions,
                cancellationToken),
            cancellationToken);
    }

    private static async Task ProcessHttpSessionAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        HttpSessionContext session,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!session.TryStartResponse())
        {
            return;
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await SendHttpUpstreamAsync(session, cancellationToken);
            await SendHttpResponseStartAsync(
                secureStream,
                session.SessionId,
                BuildHttpResponseStart(response),
                writeLock,
                writeOptions,
                cancellationToken);

            if (response.Content is not null)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[RelayChunkSize];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await SendFrameLockedAsync(
                        secureStream,
                        session.CreateTrackedOutboundFrame(FrameType.Data, buffer.AsMemory(0, read)),
                        writeLock,
                        writeOptions,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var synthetic = BuildSyntheticErrorResponse(ex);
            try
            {
                await SendHttpResponseStartAsync(
                    secureStream,
                    session.SessionId,
                    synthetic,
                    writeLock,
                    writeOptions,
                    CancellationToken.None);
            }
            catch
            {
            }
        }
        finally
        {
            response?.Dispose();
            var sendCloseToClient = !session.Cancellation.IsCancellationRequested;
            await CloseSessionAsync(
                sessions,
                session.SessionId,
                sendCloseToClient: sendCloseToClient,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
        }
    }

    private static async Task<HttpResponseMessage> SendHttpUpstreamAsync(HttpSessionContext session, CancellationToken cancellationToken)
    {
        using var request = BuildHttpRequestMessage(session);
        return await SharedHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private static HttpRequestMessage BuildHttpRequestMessage(HttpSessionContext session)
    {
        var start = session.RequestStart;
        var uri = new Uri($"{start.Scheme}://{start.Authority}{start.PathAndQuery}", UriKind.Absolute);
        var request = new HttpRequestMessage(new HttpMethod(start.Method), uri)
        {
            Version = new Version(start.VersionMajor, start.VersionMinor),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        ByteArrayContent? content = null;
        if (TryGetWrittenBuffer(session.RequestBody, out var bodySegment))
        {
            content = new ByteArrayContent(bodySegment.Array!, bodySegment.Offset, bodySegment.Count);
            request.Content = content;
        }

        foreach (var header in SanitizeHeaders(start.Headers))
        {
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Host = header.Value;
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                request.Content ??= content = new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return request;
    }

    private static bool TryGetWrittenBuffer(MemoryStream stream, out ArraySegment<byte> buffer)
    {
        if (stream.TryGetBuffer(out buffer) && buffer.Count > 0)
        {
            buffer = new ArraySegment<byte>(buffer.Array!, buffer.Offset, (int)stream.Length);
            return true;
        }

        buffer = default;
        return false;
    }

    private static HttpResponseStart BuildHttpResponseStart(HttpResponseMessage response)
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
                if (HopByHopHeaders.Contains(header.Key))
                {
                    continue;
                }

                foreach (var value in header.Value)
                {
                    headers.Add(new HttpHeader(header.Key, value));
                }
            }
        }

        return new HttpResponseStart(
            (ushort)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            (byte)Math.Min(byte.MaxValue, response.Version.Major),
            (byte)Math.Min(byte.MaxValue, response.Version.Minor),
            headers);
    }

    private static HttpResponseStart BuildSyntheticErrorResponse(Exception ex)
    {
        return ex switch
        {
            TaskCanceledException => new HttpResponseStart(504, "Gateway Timeout", 1, 1, []),
            TimeoutException => new HttpResponseStart(504, "Gateway Timeout", 1, 1, []),
            HttpRequestException => new HttpResponseStart(502, "Bad Gateway", 1, 1, []),
            _ => new HttpResponseStart(500, "Internal Server Error", 1, 1, [])
        };
    }

    private static IReadOnlyList<HttpHeader> SanitizeHeaders(IReadOnlyList<HttpHeader> headers)
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

        return sanitized;
    }
}
