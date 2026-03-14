using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Tunnel;
using Xunit.Abstractions;

namespace SimpleShadowsocks.Client.Tests;

public sealed class PerformanceMeasurementsTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceMeasurementsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Measure_Throughput_And_Allocations()
    {
        const int totalMb = 128;
        const int chunkKb = 16;
        const int streams = 4;
        var totalBytes = (long)totalMb * 1024 * 1024;
        var chunkBytes = chunkKb * 1024;

        await using var echo = await StartEchoServerAsync();
        await using var tunnel = await StartTunnelServerAsync();
        var withoutCompression = await MeasureModeAsync(
            echo.Port,
            tunnel.Port,
            chunkBytes,
            totalBytes,
            streams,
            enableCompression: false,
            payloadProfile: PayloadProfile.Mixed);
        var withCompression = await MeasureModeAsync(
            echo.Port,
            tunnel.Port,
            chunkBytes,
            totalBytes,
            streams,
            enableCompression: true,
            payloadProfile: PayloadProfile.Mixed);

        _output.WriteLine("=== Without compression ===");
        _output.WriteLine(withoutCompression.ToString());
        _output.WriteLine("=== With compression ===");
        _output.WriteLine(withCompression.ToString());

        Assert.True(withoutCompression.ThroughputMibPerSec > 5, $"Unexpectedly low throughput: {withoutCompression.ThroughputMibPerSec:F2} MiB/s");
        Assert.True(withCompression.ThroughputMibPerSec > 5, $"Unexpectedly low throughput: {withCompression.ThroughputMibPerSec:F2} MiB/s");
    }

    [Fact]
    public async Task Measure_Throughput_And_Allocations_CompressiblePayload()
    {
        const int totalMb = 128;
        const int chunkKb = 16;
        const int streams = 4;
        var totalBytes = (long)totalMb * 1024 * 1024;
        var chunkBytes = chunkKb * 1024;

        await using var echo = await StartEchoServerAsync();
        await using var tunnel = await StartTunnelServerAsync();

        var withoutCompression = await MeasureModeAsync(
            echo.Port,
            tunnel.Port,
            chunkBytes,
            totalBytes,
            streams,
            enableCompression: false,
            payloadProfile: PayloadProfile.Compressible);
        var withCompression = await MeasureModeAsync(
            echo.Port,
            tunnel.Port,
            chunkBytes,
            totalBytes,
            streams,
            enableCompression: true,
            payloadProfile: PayloadProfile.Compressible);

        _output.WriteLine("=== Compressible payload / without compression ===");
        _output.WriteLine(withoutCompression.ToString());
        _output.WriteLine("=== Compressible payload / with compression ===");
        _output.WriteLine(withCompression.ToString());

        Assert.True(withoutCompression.ThroughputMibPerSec > 5, $"Unexpectedly low throughput: {withoutCompression.ThroughputMibPerSec:F2} MiB/s");
        Assert.True(withCompression.ThroughputMibPerSec > 5, $"Unexpectedly low throughput: {withCompression.ThroughputMibPerSec:F2} MiB/s");
    }

    private async Task<PerfResult> MeasureModeAsync(
        int echoPort,
        int tunnelPort,
        int chunkBytes,
        long totalBytes,
        int streams,
        bool enableCompression,
        PayloadProfile payloadProfile)
    {
        await using var socks = await StartSocksServerAsync(tunnelPort, enableCompression);
        await RunTrafficAsync(socks.Port, echoPort, 8L * 1024 * 1024, chunkBytes, 2, payloadProfile);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        await RunTrafficAsync(socks.Port, echoPort, totalBytes, chunkBytes, streams, payloadProfile);
        sw.Stop();
        var allocAfter = GC.GetTotalAllocatedBytes(true);

        var allocated = allocAfter - allocBefore;
        var seconds = sw.Elapsed.TotalSeconds;
        var mib = totalBytes / 1024d / 1024d;
        var throughput = mib / seconds;
        var bytesPerMiB = allocated / mib;
        return new PerfResult(enableCompression, seconds, throughput, allocated, bytesPerMiB);
    }

    private static async Task RunTrafficAsync(
        int socksPort,
        int echoPort,
        long totalBytes,
        int chunkBytes,
        int streams,
        PayloadProfile payloadProfile)
    {
        var bytesPerStream = totalBytes / streams;
        var extra = totalBytes % streams;
        var tasks = new List<Task>(streams);

        for (var i = 0; i < streams; i++)
        {
            var streamBytes = bytesPerStream + (i == streams - 1 ? extra : 0);
            tasks.Add(RunSingleStreamAsync(socksPort, echoPort, streamBytes, chunkBytes, payloadProfile));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task RunSingleStreamAsync(
        int socksPort,
        int echoPort,
        long totalBytes,
        int chunkBytes,
        PayloadProfile payloadProfile)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await ReadExactAsync(stream, 2);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
        {
            throw new InvalidOperationException("SOCKS5 greeting failed.");
        }

        await stream.WriteAsync(BuildConnectRequestIPv4(IPAddress.Loopback, echoPort));
        var connect = await ReadExactAsync(stream, 10);
        if (connect[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 connect failed: {connect[1]}");
        }

        var sendBuffer = BuildPayload(chunkBytes, payloadProfile);

        var readBuffer = new byte[chunkBytes];
        long sent = 0;
        while (sent < totalBytes)
        {
            var toSend = (int)Math.Min(chunkBytes, totalBytes - sent);
            await stream.WriteAsync(sendBuffer.AsMemory(0, toSend));

            var offset = 0;
            while (offset < toSend)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(offset, toSend - offset));
                if (read == 0)
                {
                    throw new IOException("Unexpected EOF while reading echo.");
                }

                offset += read;
            }

            sent += toSend;
        }
    }

    private static async Task<RunningTunnelServer> StartTunnelServerAsync()
    {
        var port = AllocateUnusedPort();
        var server = new TunnelServer(IPAddress.Loopback, port, "dev-shared-key", TunnelCryptoPolicy.Default, new TunnelServerPolicy
        {
            MaxConcurrentTunnels = 64,
            MaxSessionsPerTunnel = 4096
        });
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);

        await WaitUntilReachableAsync(port, cts.Token);
        return new RunningTunnelServer(port, cts, runTask);
    }

    private static async Task<RunningSocksServer> StartSocksServerAsync(int tunnelPort, bool enableCompression)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            "127.0.0.1",
            tunnelPort,
            "dev-shared-key",
            TunnelCryptoPolicy.Default,
            new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 10,
                IdleTimeoutSeconds = 60,
                ReconnectBaseDelayMs = 100,
                ReconnectMaxDelayMs = 500,
                ReconnectMaxAttempts = 10,
                MaxConcurrentSessions = 4096,
                SessionReceiveChannelCapacity = 1024
            },
            ProtocolConstants.Version,
            enableCompression);
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
                            var buffer = new byte[64 * 1024];
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

    private static async Task WaitUntilReachableAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
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
        return
        [
            0x05, 0x01, 0x00, 0x01, ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3], (byte)(port >> 8), (byte)port
        ];
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
                throw new IOException("Unexpected EOF while reading stream.");
            }

            offset += read;
        }

        return buffer;
    }

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
            try { await _runTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private readonly record struct PerfResult(
        bool CompressionEnabled,
        double Seconds,
        double ThroughputMibPerSec,
        long AllocatedBytes,
        double AllocatedBytesPerMiB)
    {
        public override string ToString()
        {
            return $"Compression={(CompressionEnabled ? "on" : "off")}, Elapsed={Seconds:F3}s, Throughput={ThroughputMibPerSec:F2} MiB/s, Alloc={AllocatedBytes:N0} bytes, Alloc/MiB={AllocatedBytesPerMiB:N0}";
        }
    }

    private static byte[] BuildPayload(int chunkBytes, PayloadProfile payloadProfile)
    {
        var buffer = new byte[chunkBytes];
        if (payloadProfile == PayloadProfile.Compressible)
        {
            var pattern = System.Text.Encoding.ASCII.GetBytes("ABCDABCDABCDABCD");
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = pattern[i % pattern.Length];
            }

            return buffer;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i % 251);
        }

        return buffer;
    }

    private enum PayloadProfile
    {
        Mixed = 0,
        Compressible = 1
    }

    private sealed class RunningTunnelServer : IAsyncDisposable
    {
        public RunningTunnelServer(int port, CancellationTokenSource cts, Task runTask)
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
            try { await _runTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
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
            try { await _runTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}
