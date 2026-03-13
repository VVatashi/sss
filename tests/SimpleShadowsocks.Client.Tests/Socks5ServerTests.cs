using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Socks5;

namespace SimpleShadowsocks.Client.Tests;

public sealed class Socks5ServerTests : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    public Socks5ServerTests()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }

    [Fact]
    public async Task Greeting_WithNoAuthMethod_ReturnsNoAuthAccepted()
    {
        await using var socks = await StartSocksServerAsync();
        using var tcpClient = await ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });

        var response = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, response);
    }

    [Fact]
    public async Task Greeting_WithoutSupportedMethods_ReturnsNoAcceptableMethods()
    {
        await using var socks = await StartSocksServerAsync();
        using var tcpClient = await ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x02 });

        var response = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0xFF }, response);
    }

    [Fact]
    public async Task ConnectCommand_ToReachableTarget_EnablesDataRelay()
    {
        await using var echo = await StartEchoServerAsync();
        await using var socks = await StartSocksServerAsync();
        using var tcpClient = await ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var connectRequest = BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x00, connectResponse.ReplyCode);

        var payload = Encoding.ASCII.GetBytes("ping-through-socks");
        await stream.WriteAsync(payload);
        var echoed = await ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task ConnectCommand_ToUnreachableTarget_ReturnsHostUnreachable()
    {
        await using var socks = await StartSocksServerAsync();
        using var tcpClient = await ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var closedPort = AllocateUnusedPort();
        var connectRequest = BuildConnectRequestIPv4(IPAddress.Loopback, closedPort);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x05, connectResponse.ReplyCode);
    }

    [Fact]
    public async Task UnsupportedCommand_ReturnsCommandNotSupported()
    {
        await using var socks = await StartSocksServerAsync();
        using var tcpClient = await ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var bindRequest = BuildBindRequestIPv4(IPAddress.Loopback, 80);
        await stream.WriteAsync(bindRequest);
        var response = await ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x07, response.ReplyCode);
    }

    private static async Task<RunningSocksServer> StartSocksServerAsync()
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(IPAddress.Loopback, port);
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningSocksServer(port, cts, runTask);
    }

    private static async Task<RunningEchoServer> StartEchoServerAsync()
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
                        using (var stream = client.GetStream())
                        {
                            var buffer = new byte[8 * 1024];
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

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static async Task WaitUntilReachableAsync(int port, CancellationToken cancellationToken)
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

    private static int AllocateUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] BuildConnectRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return new byte[]
            { 0x05, 0x01, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port };
    }

    private static byte[] BuildBindRequestIPv4(IPAddress address, int port)
    {
        var ipBytes = address.GetAddressBytes();
        return new byte[]
            { 0x05, 0x02, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port };
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
            if (read == 0)
            {
                throw new IOException("Unexpected EOF while reading from stream.");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<Socks5Reply> ReadSocks5ReplyAsync(NetworkStream stream)
    {
        var header = await ReadExactAsync(stream, 4);
        var addressType = header[3];

        var addressLength = addressType switch
        {
            0x01 => 4,
            0x04 => 16,
            _ => throw new InvalidDataException($"Unexpected address type in SOCKS5 reply: {addressType}")
        };

        await ReadExactAsync(stream, addressLength + 2);
        return new Socks5Reply(header[1]);
    }

    private readonly record struct Socks5Reply(byte ReplyCode);

    private sealed class RunningSocksServer : IAsyncDisposable
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

    private sealed class RunningEchoServer : IAsyncDisposable
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
}
