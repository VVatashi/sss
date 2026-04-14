using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Http;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Integration)]
public sealed class ProxyCoexistenceTests
{
    [Fact]
    public async Task SocksHttpForwardAndReverseProxy_CanTransferDataSimultaneously_WithoutConflicts()
    {
        var arrivals = 0;
        var allArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTraffic = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        static void CompleteWhenReached(TaskCompletionSource<bool> tcs, int value, int expected)
        {
            if (value == expected)
            {
                tcs.TrySetResult(true);
            }
        }

        void MarkArrived()
        {
            var current = Interlocked.Increment(ref arrivals);
            CompleteWhenReached(allArrived, current, 3);
        }

        await using var echo = await StartCoordinatedEchoServerAsync(MarkArrived, releaseTraffic.Task);
        await using var forwardOrigin = await TestNetwork.StartHttpOriginServerAsync(request =>
        {
            MarkArrived();
            releaseTraffic.Task.GetAwaiter().GetResult();
            return new TestNetwork.HttpOriginResponse(
                200,
                "OK",
                [new HttpHeader("Content-Type", "text/plain")],
                request.Body);
        });
        await using var reverseOrigin = await TestNetwork.StartHttpOriginServerAsync(request =>
        {
            MarkArrived();
            releaseTraffic.Task.GetAwaiter().GetResult();
            return new TestNetwork.HttpOriginResponse(
                200,
                "OK",
                [new HttpHeader("Content-Type", "text/plain")],
                request.Body);
        });

        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);
        await using var forwardProxy = await TestNetwork.StartHttpProxyServerAsync(tunnel.Port);
        await using var reverseClient = await TestNetwork.StartHttpReverseProxyClientAsync(
            tunnel,
            [
                new HttpReverseProxyTunnelHandler.Route(
                    "coexist.local",
                    "/",
                    new Uri($"http://127.0.0.1:{reverseOrigin.Port}/"),
                    false)
            ]);
        await using var reverseProxy = await TestNetwork.StartHttpReverseProxyServerAsync(tunnel.Server);

        const string socksPayload = "socks-simultaneous-payload";
        const string forwardPayload = "forward-http-simultaneous-payload";
        const string reversePayload = "reverse-http-simultaneous-payload";

        var socksTask = RunSocksEchoAsync(socks.Port, echo.Port, socksPayload);
        var forwardTask = TestNetwork.SendRawHttpRequestAsync(
            forwardProxy.Port,
            $"POST http://127.0.0.1:{forwardOrigin.Port}/forward HTTP/1.1\r\n" +
            "Host: forward.local\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {Encoding.ASCII.GetByteCount(forwardPayload)}\r\n" +
            "Connection: close\r\n\r\n" +
            forwardPayload);
        var reverseTask = TestNetwork.SendRawHttpRequestAsync(
            reverseProxy.Port,
            $"POST /reverse HTTP/1.1\r\n" +
            "Host: coexist.local\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {Encoding.ASCII.GetByteCount(reversePayload)}\r\n" +
            "Connection: close\r\n\r\n" +
            reversePayload);

        await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseTraffic.TrySetResult(true);

        var socksEcho = await socksTask;
        var forwardResponse = await forwardTask;
        var reverseResponse = await reverseTask;

        Assert.Equal(socksPayload, socksEcho);
        Assert.Contains("HTTP/1.1 200 OK", forwardResponse.Head, StringComparison.Ordinal);
        Assert.Equal(forwardPayload, forwardResponse.BodyText);
        Assert.Contains("HTTP/1.1 200 OK", reverseResponse.Head, StringComparison.Ordinal);
        Assert.Equal(reversePayload, reverseResponse.BodyText);

        var capturedForward = Assert.Single(forwardOrigin.Requests);
        var capturedReverse = Assert.Single(reverseOrigin.Requests);
        Assert.Equal("/forward", capturedForward.PathAndQuery);
        Assert.Equal("/reverse", capturedReverse.PathAndQuery);
        Assert.Equal(forwardPayload, Encoding.ASCII.GetString(capturedForward.Body));
        Assert.Equal(reversePayload, Encoding.ASCII.GetString(capturedReverse.Body));
    }

    private static async Task<string> RunSocksEchoAsync(int socksPort, int echoPort, string payloadText)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echoPort);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var payload = Encoding.ASCII.GetBytes(payloadText);
        await stream.WriteAsync(payload);
        var echoed = await TestNetwork.ReadExactAsync(stream, payload.Length, timeoutMs: 15_000);
        return Encoding.ASCII.GetString(echoed);
    }

    private static async Task<CoordinatedEchoServer> StartCoordinatedEchoServerAsync(Action onArrival, Task releaseTask)
    {
        var port = TestNetwork.AllocateUnusedPort();
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

                            var buffer = new byte[16 * 1024];
                            var read = await stream.ReadAsync(buffer, cts.Token);
                            if (read == 0)
                            {
                                return;
                            }

                            onArrival();
                            await releaseTask.WaitAsync(cts.Token);
                            await stream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
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

        await TestNetwork.WaitUntilReachableAsync(port, cts.Token);
        return new CoordinatedEchoServer(port, cts, runTask);
    }

    private sealed class CoordinatedEchoServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;

        public CoordinatedEchoServer(int port, CancellationTokenSource cts, Task runTask)
        {
            Port = port;
            _cts = cts;
            _runTask = runTask;
        }

        public int Port { get; }

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
