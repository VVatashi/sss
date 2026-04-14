using System.Text;
using SimpleShadowsocks.Client.Http;
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
}
