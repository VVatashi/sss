using System.Net;
using System.Net.Http;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer
{
    public async Task RunPersistentAsync(CancellationToken cancellationToken)
    {
        var announcedGeneration = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await EnsureConnectedAsync(cancellationToken);
            var generation = Volatile.Read(ref _connectionGeneration);
            if (generation != announcedGeneration)
            {
                announcedGeneration = generation;
                var sequence = (ulong)Interlocked.Increment(ref _controlSendSequence);
                await SendFrameAsync(new ProtocolFrame(FrameType.Ping, 0, sequence, Array.Empty<byte>()), cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task<bool> TryHandleIncomingReverseHttpFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case FrameType.ReverseHttpRequest:
                await HandleIncomingReverseHttpRequestStartAsync(frame, cancellationToken);
                return true;

            case FrameType.Data:
                if (_incomingReverseHttpSessions.TryGetValue(frame.SessionId, out var dataSession))
                {
                    await HandleIncomingReverseHttpRequestBodyAsync(dataSession, frame, cancellationToken);
                    return true;
                }

                break;

            case FrameType.ReverseHttpRequestEnd:
                if (_incomingReverseHttpSessions.TryGetValue(frame.SessionId, out var completedSession))
                {
                    await HandleIncomingReverseHttpRequestEndAsync(completedSession, frame, cancellationToken);
                    return true;
                }

                break;

            case FrameType.Close:
                if (_incomingReverseHttpSessions.TryGetValue(frame.SessionId, out var closingSession))
                {
                    if (!closingSession.TryAcceptIncomingSequence(frame.Sequence))
                    {
                        await CloseIncomingReverseHttpSessionAsync(frame.SessionId, 0x21, CancellationToken.None);
                        return true;
                    }

                    DisposeIncomingReverseHttpSession(frame.SessionId);
                    StructuredLog.Info("tunnel-client", "TUNNEL/HTTP", "reverse session closed by remote", frame.SessionId);
                    return true;
                }

                break;
        }

        return false;
    }

    private async Task HandleIncomingReverseHttpRequestStartAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (!ProtocolConstants.SupportsHttpRelay(_protocolVersion))
        {
            return;
        }

        if (frame.Sequence != 0)
        {
            await SendSyntheticReverseHttpResponseAsync(
                frame.SessionId,
                new HttpResponseStart(400, "Bad Request", 1, 1, []),
                CancellationToken.None);
            return;
        }

        HttpRequestStart requestStart;
        try
        {
            requestStart = ProtocolPayloadSerializer.DeserializeHttpRequestStart(frame.Payload.Span);
        }
        catch
        {
            await SendSyntheticReverseHttpResponseAsync(
                frame.SessionId,
                new HttpResponseStart(400, "Bad Request", 1, 1, []),
                CancellationToken.None);
            return;
        }

        var session = new IncomingReverseHttpSession(frame.SessionId, requestStart);
        if (!_incomingReverseHttpSessions.TryAdd(frame.SessionId, session))
        {
            session.Dispose();
            await SendSyntheticReverseHttpResponseAsync(
                frame.SessionId,
                new HttpResponseStart(409, "Conflict", 1, 1, []),
                CancellationToken.None);
            return;
        }

        StructuredLog.Info(
            "tunnel-client",
            "TUNNEL/HTTP",
            $"reverse {requestStart.Method} {requestStart.Authority}{requestStart.PathAndQuery}",
            frame.SessionId);
    }

    private async Task HandleIncomingReverseHttpRequestBodyAsync(
        IncomingReverseHttpSession session,
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        if (!session.TryAcceptIncomingSequence(frame.Sequence) || session.HasCompletedRequest)
        {
            await CloseIncomingReverseHttpSessionAsync(frame.SessionId, 0x21, CancellationToken.None);
            return;
        }

        if (!frame.Payload.IsEmpty)
        {
            await session.RequestBody.WriteAsync(frame.Payload, cancellationToken);
        }
    }

    private Task HandleIncomingReverseHttpRequestEndAsync(
        IncomingReverseHttpSession session,
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        if (!session.TryAcceptIncomingSequence(frame.Sequence) || !session.TryMarkRequestCompleted())
        {
            return CloseIncomingReverseHttpSessionAsync(frame.SessionId, 0x21, CancellationToken.None);
        }

        _ = Task.Run(() => ProcessIncomingReverseHttpSessionAsync(session, cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task ProcessIncomingReverseHttpSessionAsync(
        IncomingReverseHttpSession session,
        CancellationToken cancellationToken)
    {
        if (!session.TryStartResponse())
        {
            return;
        }

        HttpResponseMessage? response = null;
        try
        {
            response = _reverseHttpHandler is null
                ? CreateSyntheticReverseHttpResponse(HttpStatusCode.Forbidden, "Forbidden")
                : await _reverseHttpHandler.SendAsync(session.RequestStart, session.RequestBody.ToArray(), cancellationToken);

            await SendFrameAsync(
                new ProtocolFrame(
                    FrameType.ReverseHttpResponse,
                    session.SessionId,
                    0,
                    ProtocolPayloadSerializer.SerializeHttpResponseStart(BuildHttpResponseStart(response))),
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

                    await SendOwnedFrameAsync(
                        FrameType.Data,
                        session.SessionId,
                        session.TakeNextSendSequence(),
                        buffer.AsMemory(0, read),
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StructuredLog.Error("tunnel-client", "TUNNEL/HTTP", "reverse HTTP request failed", ex, session.SessionId);
            try
            {
                await SendFrameAsync(
                    new ProtocolFrame(
                        FrameType.ReverseHttpResponse,
                        session.SessionId,
                        0,
                        ProtocolPayloadSerializer.SerializeHttpResponseStart(BuildSyntheticErrorResponse(ex))),
                    CancellationToken.None);
            }
            catch
            {
            }
        }
        finally
        {
            response?.Dispose();
            await CloseIncomingReverseHttpSessionAsync(session.SessionId, 0x00, CancellationToken.None);
        }
    }

    private async Task SendSyntheticReverseHttpResponseAsync(
        uint sessionId,
        HttpResponseStart response,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendFrameAsync(
                new ProtocolFrame(
                    FrameType.ReverseHttpResponse,
                    sessionId,
                    0,
                    ProtocolPayloadSerializer.SerializeHttpResponseStart(response)),
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await SendFrameAsync(
                new ProtocolFrame(
                    FrameType.Close,
                    sessionId,
                    1,
                    ProtocolPayloadSerializer.SerializeClose(0x00)),
                cancellationToken);
        }
        catch
        {
        }
    }

    private async Task CloseIncomingReverseHttpSessionAsync(
        uint sessionId,
        byte reasonCode,
        CancellationToken cancellationToken)
    {
        var session = RemoveIncomingReverseHttpSession(sessionId);
        if (session is null)
        {
            return;
        }

        try
        {
            await SendFrameAsync(
                new ProtocolFrame(
                    FrameType.Close,
                    sessionId,
                    session.TakeNextSendSequence(),
                    ProtocolPayloadSerializer.SerializeClose(reasonCode)),
                cancellationToken);
        }
        catch
        {
        }
        finally
        {
            session.Dispose();
        }
    }

    private void DisposeAllIncomingReverseHttpSessions(Exception? error = null)
    {
        foreach (var sessionId in _incomingReverseHttpSessions.Keys.ToArray())
        {
            var session = RemoveIncomingReverseHttpSession(sessionId);
            if (session is null)
            {
                continue;
            }

            session.Dispose();
        }
    }

    private void DisposeIncomingReverseHttpSession(uint sessionId)
    {
        var session = RemoveIncomingReverseHttpSession(sessionId);
        session?.Dispose();
    }

    private IncomingReverseHttpSession? RemoveIncomingReverseHttpSession(uint sessionId)
    {
        return _incomingReverseHttpSessions.TryRemove(sessionId, out var session) ? session : null;
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

    private static HttpResponseMessage CreateSyntheticReverseHttpResponse(HttpStatusCode statusCode, string reasonPhrase)
    {
        return new HttpResponseMessage(statusCode)
        {
            ReasonPhrase = reasonPhrase,
            Version = HttpVersion.Version11,
            Content = new ByteArrayContent([])
        };
    }
}
