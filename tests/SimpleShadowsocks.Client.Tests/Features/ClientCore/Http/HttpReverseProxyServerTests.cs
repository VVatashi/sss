using System.Text;
using SimpleShadowsocks.Client.Http;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Integration)]
public sealed class HttpReverseProxyServerTests
{
    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_RelaysGetRequest_WithoutProxyDisclosureHeaders()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(
                200,
                "OK",
                [new HttpHeader("Content-Type", "text/plain")],
                Encoding.ASCII.GetBytes("reverse-hello")));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route("app.local", "/", new Uri($"http://127.0.0.1:{origin.Port}/"), false)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /hello HTTP/1.1\r\n" +
            "Host: app.local\r\n" +
            "User-Agent: reverse-tests\r\n" +
            "Via: should-be-stripped\r\n" +
            "Forwarded: for=1.2.3.4\r\n" +
            "X-Forwarded-For: 1.2.3.4\r\n" +
            "Connection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Equal("reverse-hello", response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal("/hello", request.PathAndQuery);
        Assert.Equal("app.local", request.GetHeader("Host"));
        Assert.Equal("reverse-tests", request.GetHeader("User-Agent"));
        Assert.Null(request.GetHeader("Via"));
        Assert.Null(request.GetHeader("Forwarded"));
        Assert.Null(request.GetHeader("X-Forwarded-For"));
    }

    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_RelaysGetQuery_WithPathPrefixRouting()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(200, "OK", [], Encoding.ASCII.GetBytes(request.PathAndQuery)));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route(null, "/api", new Uri($"http://127.0.0.1:{origin.Port}/backend"), true)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /api/items?page=1&limit=50 HTTP/1.1\r\nHost: local.reverse\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Equal("/backend/items?page=1&limit=50", response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal("/backend/items?page=1&limit=50", request.PathAndQuery);
    }

    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_DecodesDoubleEncodedRequestTarget()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(200, "OK", [], Encoding.ASCII.GetBytes(request.PathAndQuery)));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route("app.local", "/", new Uri($"http://127.0.0.1:{origin.Port}/"), false)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /api/items%253Fpage%253D1%2526limit%253D50 HTTP/1.1\r\nHost: app.local\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Equal("/api/items?page=1&limit=50", response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal("/api/items?page=1&limit=50", request.PathAndQuery);
    }

    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_DecodesDoubleEncodedRequestTarget_WithPathPrefixRouting()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(200, "OK", [], Encoding.ASCII.GetBytes(request.PathAndQuery)));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route(null, "/api", new Uri($"http://127.0.0.1:{origin.Port}/backend"), true)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /api/items%253fpage%253d1%2526limit%253d50 HTTP/1.1\r\nHost: local.reverse\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Equal("/backend/items?page=1&limit=50", response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal("/backend/items?page=1&limit=50", request.PathAndQuery);
    }

    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_DecodesDoubleEncodedQueryValue()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(200, "OK", [], Encoding.UTF8.GetBytes(request.PathAndQuery)));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route("stream.local", "/connection", new Uri($"http://127.0.0.1:{origin.Port}/"), true)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /connection/sse%3Fcf_connect=%257B%2522connect%2522%253A1%257D HTTP/1.1\r\nHost: stream.local\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
        Assert.Equal("/sse?cf_connect=%7B%22connect%22:1%7D", response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal("/sse?cf_connect=%7B%22connect%22:1%7D", request.PathAndQuery);
    }

    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_RelaysPostBody_WithPathPrefixStripping()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(201, "Created", [], request.Body));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route(null, "/api", new Uri($"http://127.0.0.1:{origin.Port}/backend"), true)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        const string payload = "reverse-post-body";
        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            $"POST /api/items?id=42 HTTP/1.1\r\nHost: local.reverse\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.ASCII.GetByteCount(payload)}\r\nConnection: close\r\n\r\n{payload}");

        Assert.Contains("HTTP/1.1 201 Created", response.Head, StringComparison.Ordinal);
        Assert.Equal(payload, response.BodyText);
        var request = Assert.Single(origin.Requests);
        Assert.Equal("/backend/items?id=42", request.PathAndQuery);
        Assert.Equal(payload, Encoding.ASCII.GetString(request.Body));
    }

    [Fact]
    public async Task HttpReverseProxy_WithoutMatchingRoute_ReturnsNotFound()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route("allowed.local", "/", new Uri($"http://127.0.0.1:{origin.Port}/"), false)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /hello HTTP/1.1\r\nHost: denied.local\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 404 Not Found", response.Head, StringComparison.Ordinal);
        Assert.Empty(origin.Requests);
    }

    [Fact]
    public async Task HttpReverseProxy_WhenClientReverseProxyIsDisabled_ReturnsForbidden()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync(request =>
            new TestNetwork.HttpOriginResponse(200, "OK", [], Encoding.ASCII.GetBytes("warmup")));
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var forwardProxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var warmup = await TestNetwork.SendRawHttpRequestAsync(
            forwardProxy.Port,
            $"GET http://127.0.0.1:{origin.Port}/warmup HTTP/1.1\r\nHost: warmup.local\r\nConnection: close\r\n\r\n");
        Assert.Contains("HTTP/1.1 200 OK", warmup.Head, StringComparison.Ordinal);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /blocked HTTP/1.1\r\nHost: app.local\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 403 Forbidden", response.Head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_RelaysLongLivedChunkedStream_WithoutTimingOut()
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
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route("stream.local", "/", new Uri($"http://127.0.0.1:{origin.Port}/"), false)
            ],
            connectionPolicy: new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 1,
                IdleTimeoutSeconds = 5,
                ReconnectBaseDelayMs = 100,
                ReconnectMaxDelayMs = 200,
                ReconnectMaxAttempts = 5
            });
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);
        await using var response = await TestNetwork.OpenStreamingHttpConnectionAsync(
            reverseProxy.Port,
            "GET /events HTTP/1.1\r\n" +
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
    public async Task HttpReverseProxy_ViaTunnel_CleansUpStreamingSession_WhenDownstreamDisconnects()
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
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route("cleanup.local", "/", new Uri($"http://127.0.0.1:{origin.Port}/"), false)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);
        await using (var response = await TestNetwork.OpenStreamingHttpConnectionAsync(
            reverseProxy.Port,
            "GET /cleanup HTTP/1.1\r\n" +
            "Host: cleanup.local\r\n" +
            "Connection: close\r\n\r\n"))
        {
            Assert.Contains("HTTP/1.1 200 OK", response.Head, StringComparison.Ordinal);
            Assert.Contains("data: first\n\n", await response.ReadUntilTextAsync("data: first\n\n", CancellationToken.None), StringComparison.Ordinal);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await TestNetwork.WaitForTunnelConnectionsAsync(
            () => reverseProxy.Server.ActiveReverseHttpSessionCount == 0 && reverseClient.Client.ActiveReverseHttpSessionCount == 0,
            timeoutCts.Token);
    }

    [Fact]
    public async Task HttpReverseProxy_ViaTunnel_CleansUpStreamingSession_AfterTunnelFault()
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
            await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
                tunnel,
                [
                    new HttpReverseProxyTunnelHandler.Route("fault.local", "/", new Uri($"http://127.0.0.1:{origin.Port}/"), false)
                ],
                connectionPolicy: new TunnelConnectionPolicy
                {
                    HeartbeatIntervalSeconds = 1,
                    IdleTimeoutSeconds = 5,
                    ReconnectBaseDelayMs = 100,
                    ReconnectMaxDelayMs = 200,
                    ReconnectMaxAttempts = 3
                });
            await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);
            await using var response = await TestNetwork.OpenStreamingHttpConnectionAsync(
                reverseProxy.Port,
                "GET /fault HTTP/1.1\r\n" +
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
            await TestNetwork.WaitForTunnelConnectionsAsync(
                () => reverseProxy.Server.ActiveReverseHttpSessionCount == 0 && reverseClient.Client.ActiveReverseHttpSessionCount == 0,
                cleanupTimeoutCts.Token);
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
    public async Task HttpReverseProxy_RejectsWebSocketUpgradeRequest()
    {
        await using var origin = await TestNetwork.StartHttpOriginServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route("ws.local", "/", new Uri($"http://127.0.0.1:{origin.Port}/"), false)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        var response = await TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            "GET /socket HTTP/1.1\r\n" +
            "Host: ws.local\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: websocket\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");

        Assert.Contains("HTTP/1.1 501 WebSocket Not Supported", response.Head, StringComparison.Ordinal);
        Assert.Empty(origin.Requests);
    }
}
