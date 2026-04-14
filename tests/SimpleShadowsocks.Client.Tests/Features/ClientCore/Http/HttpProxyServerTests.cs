using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Integration)]
public sealed class HttpProxyServerTests
{
    [Fact]
    public async Task HttpProxy_ViaTunnel_RelaysGetRequest_WithoutProxyDisclosureHeaders()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(
                200,
                "OK",
                [new HttpHeader("Content-Type", "text/plain")],
                Encoding.ASCII.GetBytes("hello-http-tunnel")));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);

        var response = await SendRawHttpRequestAsync(
            proxy.Port,
            $"GET http://127.0.0.1:{origin.Port}/hello HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "User-Agent: proxy-tests\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "Proxy-Authorization: Basic dGVzdA==\r\n" +
            "Via: should-be-stripped\r\n" +
            "X-Forwarded-For: 1.2.3.4\r\n" +
            "Connection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Equal("hello-http-tunnel", response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal("/hello", request.PathAndQuery);
        Assert.Equal("example.test", request.GetHeader("Host"));
        Assert.Equal("proxy-tests", request.GetHeader("User-Agent"));
        Assert.Null(request.GetHeader("Proxy-Connection"));
        Assert.Null(request.GetHeader("Proxy-Authorization"));
        Assert.Null(request.GetHeader("Via"));
        Assert.Null(request.GetHeader("Forwarded"));
        Assert.Null(request.GetHeader("X-Forwarded-For"));
    }

    [Fact]
    public async Task HttpProxy_ViaTunnel_RelaysPostBody()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(
                201,
                "Created",
                [new HttpHeader("Content-Type", "text/plain")],
                request.Body));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);

        const string payload = "post-body-through-http-proxy";
        var response = await SendRawHttpRequestAsync(
            proxy.Port,
            $"POST http://127.0.0.1:{origin.Port}/submit HTTP/1.1\r\n" +
            "Host: submit.test\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {Encoding.ASCII.GetByteCount(payload)}\r\n" +
            "Connection: close\r\n\r\n" +
            payload);

        Assert.Contains("HTTP/1.1 201 Created", response.Head, StringComparison.Ordinal);
        Assert.Equal(payload, response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal(payload, Encoding.ASCII.GetString(request.Body));
    }

    [Fact]
    public async Task HttpProxy_WithDirectRoutingRule_BypassesTunnel()
    {
        var routingPolicy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Subnet,
                Match = "127.0.0.0/8",
                Decision = TrafficRouteDecision.Direct
            },
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Any,
                Match = "*",
                Decision = TrafficRouteDecision.Tunnel
            }
        ]);

        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(200, "OK", [], Encoding.ASCII.GetBytes("direct")));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port, routingPolicy: routingPolicy);
        var acceptedBefore = tunnel.Server.AcceptedTunnelConnections;

        var response = await SendRawHttpRequestAsync(
            proxy.Port,
            $"GET http://127.0.0.1:{origin.Port}/direct HTTP/1.1\r\n" +
            "Host: direct.test\r\n" +
            "Connection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Equal("direct", response.BodyText);
        Assert.Equal(acceptedBefore, tunnel.Server.AcceptedTunnelConnections);
    }

    [Fact]
    public async Task HttpProxy_WithDropRoutingRule_ReturnsForbidden()
    {
        var routingPolicy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Any,
                Match = "*",
                Decision = TrafficRouteDecision.Drop
            }
        ]);

        await using var proxy = await TestNetwork.StartStandaloneHttpProxyServerAsync(routingPolicy);
        var response = await SendRawHttpRequestAsync(
            proxy.Port,
            "GET http://example.org/blocked HTTP/1.1\r\n" +
            "Host: example.org\r\n" +
            "Connection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 403 Forbidden", response.Head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpProxy_ConnectRequest_ReturnsNotImplemented()
    {
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);

        var response = await SendRawHttpRequestAsync(
            proxy.Port,
            "CONNECT example.org:443 HTTP/1.1\r\n" +
            "Host: example.org:443\r\n" +
            "Connection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 501 Not Implemented", response.Head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpProxy_HttpsAbsoluteForm_ReturnsNotImplemented()
    {
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);

        var response = await SendRawHttpRequestAsync(
            proxy.Port,
            "GET https://example.org/ HTTP/1.1\r\n" +
            "Host: example.org\r\n" +
            "Connection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 501 Not Implemented", response.Head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpProxy_ViaTunnel_RelaysLongLivedChunkedStream_WithoutTimingOut()
    {
        await using var origin = await TestNetwork.StartStreamingHttpOriginServerAsync(_ =>
            new TestNetwork.HttpStreamingOriginResponse(
                200,
                "OK",
                [new HttpHeader("Content-Type", "text/event-stream")],
                [
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes(": keep-alive\n\n")),
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: one\n\n"), 2200),
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: two\n\n"), 2200),
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: three\n\n"), 2200)
                ]));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(
            tunnel.Port,
            connectionPolicy: new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 1,
                IdleTimeoutSeconds = 5,
                ReconnectBaseDelayMs = 100,
                ReconnectMaxDelayMs = 200,
                ReconnectMaxAttempts = 5
            });
        await using var response = await TestNetwork.OpenStreamingHttpConnectionAsync(
            proxy.Port,
            $"GET http://127.0.0.1:{origin.Port}/events HTTP/1.1\r\n" +
            "Host: stream.local\r\n" +
            "Accept: text/event-stream\r\n" +
            "Connection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/event-stream", response.Head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(": keep-alive\n\n", await response.ReadUntilTextAsync(": keep-alive\n\n", CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("data: one\n\n", await response.ReadUntilTextAsync("data: one\n\n", CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("data: two\n\n", await response.ReadUntilTextAsync("data: two\n\n", CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("data: three\n\n", await response.ReadUntilTextAsync("data: three\n\n", CancellationToken.None), StringComparison.Ordinal);
        await response.ReadToEndTextAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HttpProxy_ViaTunnel_CleansUpStreamingSession_WhenDownstreamDisconnects()
    {
        await using var origin = await TestNetwork.StartStreamingHttpOriginServerAsync(_ =>
            new TestNetwork.HttpStreamingOriginResponse(
                200,
                "OK",
                [new HttpHeader("Content-Type", "text/event-stream")],
                [
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: first\n\n")),
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: second\n\n"), 2000),
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: third\n\n"), 2000)
                ]));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);
        await using (var response = await TestNetwork.OpenStreamingHttpConnectionAsync(
            proxy.Port,
            $"GET http://127.0.0.1:{origin.Port}/cleanup HTTP/1.1\r\n" +
            "Host: cleanup.local\r\n" +
            "Connection: close\r\n\r\n"))
        {
            Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
            Assert.Contains("data: first\n\n", await response.ReadUntilTextAsync("data: first\n\n", CancellationToken.None), StringComparison.Ordinal);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await TestNetwork.WaitForTunnelConnectionsAsync(() => proxy.Server.ActiveTunnelHttpSessionCount == 0, timeoutCts.Token);
    }

    [Fact]
    public async Task HttpProxy_ViaTunnel_CleansUpStreamingSession_AfterTunnelFault()
    {
        await using var origin = await TestNetwork.StartStreamingHttpOriginServerAsync(_ =>
            new TestNetwork.HttpStreamingOriginResponse(
                200,
                "OK",
                [new HttpHeader("Content-Type", "text/event-stream")],
                [
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: first\n\n")),
                    new TestNetwork.HttpStreamingChunk(Encoding.UTF8.GetBytes("data: second\n\n"), 5000)
                ]));
        var tunnel = await TestNetwork.StartTunnelServerAsync();
        var tunnelDisposed = false;
        try
        {
            await using var proxy = await TestNetwork.StartHttpProxyServerAsync(
                tunnel.Port,
                connectionPolicy: new TunnelConnectionPolicy
                {
                    HeartbeatIntervalSeconds = 1,
                    IdleTimeoutSeconds = 5,
                    ReconnectBaseDelayMs = 100,
                    ReconnectMaxDelayMs = 200,
                    ReconnectMaxAttempts = 3
                });
            await using var response = await TestNetwork.OpenStreamingHttpConnectionAsync(
                proxy.Port,
                $"GET http://127.0.0.1:{origin.Port}/fault HTTP/1.1\r\n" +
                "Host: fault.local\r\n" +
                "Connection: close\r\n\r\n");

            Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
            Assert.Contains("data: first\n\n", await response.ReadUntilTextAsync("data: first\n\n", CancellationToken.None), StringComparison.Ordinal);

            await tunnel.DisposeAsync();
            tunnelDisposed = true;

            using var readTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var exception = await Record.ExceptionAsync(() => response.ReadUntilTextAsync("data: second\n\n", readTimeoutCts.Token));
            Assert.NotNull(exception);

            using var cleanupTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await TestNetwork.WaitForTunnelConnectionsAsync(() => proxy.Server.ActiveTunnelHttpSessionCount == 0, cleanupTimeoutCts.Token);
        }
        finally
        {
            if (!tunnelDisposed)
            {
                await tunnel.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task HttpProxy_RejectsWebSocketUpgradeRequest()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var proxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);

        var response = await SendRawHttpRequestAsync(
            proxy.Port,
            $"GET http://127.0.0.1:{origin.Port}/socket HTTP/1.1\r\n" +
            "Host: ws.local\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: websocket\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");

        Assert.Contains("HTTP/1.1 501 WebSocket Not Supported", response.Head, StringComparison.Ordinal);
        Assert.Empty(origin.Requests);
    }

    private static async Task<(string Head, string BodyText)> SendRawHttpRequestAsync(int proxyPort, string requestText)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        using var stream = client.GetStream();

        var requestBytes = Encoding.ASCII.GetBytes(requestText.ReplaceLineEndings("\r\n"));
        await stream.WriteAsync(requestBytes);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responseBytes = await ReadResponseAsync(stream, timeoutCts.Token);
        var responseText = Encoding.ASCII.GetString(responseBytes);
        var separator = responseText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(separator >= 0, "HTTP response separator not found.");
        return (responseText[..separator], responseText[(separator + 4)..]);
    }

    private static async Task<byte[]> ReadResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
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
}
