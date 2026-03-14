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
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromSeconds(90);
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

    [Fact]
    public async Task Measure_Throughput_And_Allocations_ChaCha20Poly1305()
    {
        await MeasureSingleAlgorithmAsync(TunnelCipherAlgorithm.ChaCha20Poly1305, "ChaCha20-Poly1305");
    }

    [Fact]
    public async Task Measure_Throughput_And_Allocations_Aes256Gcm()
    {
        await MeasureSingleAlgorithmAsync(TunnelCipherAlgorithm.Aes256Gcm, "AES-256-GCM");
    }

    [Fact]
    public async Task Measure_Throughput_And_Allocations_Aegis128L()
    {
        if (!AeadDuplexStream.IsSupported(TunnelCipherAlgorithm.Aegis128L))
        {
            _output.WriteLine("AEGIS-128L is not supported on this runtime.");
            return;
        }

        await MeasureSingleAlgorithmAsync(TunnelCipherAlgorithm.Aegis128L, "AEGIS-128L");
    }

    [Fact]
    public async Task Measure_Throughput_And_Allocations_Aegis256()
    {
        if (!AeadDuplexStream.IsSupported(TunnelCipherAlgorithm.Aegis256))
        {
            _output.WriteLine("AEGIS-256 is not supported on this runtime.");
            return;
        }

        await MeasureSingleAlgorithmAsync(TunnelCipherAlgorithm.Aegis256, "AEGIS-256");
    }

    private async Task MeasureSingleAlgorithmAsync(TunnelCipherAlgorithm algorithm, string algorithmName)
    {
        const int totalMb = 128;
        const int chunkKb = 16;
        const int streams = 4;
        var totalBytes = (long)totalMb * 1024 * 1024;
        var chunkBytes = chunkKb * 1024;

        _output.WriteLine($"[perf] start algorithm={algorithmName}, total={totalMb}MiB, chunk={chunkKb}KiB, streams={streams}");
        await using var echo = await StartEchoServerAsync();
        _output.WriteLine($"[perf] echo server ready on 127.0.0.1:{echo.Port}");
        await using var tunnel = await StartTunnelServerAsync();
        _output.WriteLine($"[perf] tunnel server ready on 127.0.0.1:{tunnel.Port}");

        var result = await MeasureModeAsync(
            echo.Port,
            tunnel.Port,
            chunkBytes,
            totalBytes,
            streams,
            enableCompression: false,
            payloadProfile: PayloadProfile.Mixed,
            algorithm: algorithm);

        _output.WriteLine($"=== AEAD {algorithmName} ===");
        _output.WriteLine(result.ToString());
        Assert.True(result.ThroughputMibPerSec > 5, $"Unexpectedly low throughput ({algorithmName}): {result.ThroughputMibPerSec:F2} MiB/s");
    }

    private async Task<PerfResult> MeasureModeAsync(
        int echoPort,
        int tunnelPort,
        int chunkBytes,
        long totalBytes,
        int streams,
        bool enableCompression,
        PayloadProfile payloadProfile,
        TunnelCipherAlgorithm algorithm = TunnelCipherAlgorithm.ChaCha20Poly1305)
    {
        _output.WriteLine($"[perf] start socks server (algorithm={algorithm}, compression={enableCompression})");
        await using var socks = await StartSocksServerAsync(tunnelPort, enableCompression, algorithm);
        _output.WriteLine($"[perf] socks server ready on 127.0.0.1:{socks.Port}");

        _output.WriteLine($"[perf] warmup start (timeout={WarmupTimeout.TotalSeconds}s)");
        await RunTrafficAsync(
            socks.Port,
            echoPort,
            8L * 1024 * 1024,
            chunkBytes,
            2,
            payloadProfile,
            WarmupTimeout,
            "warmup");
        _output.WriteLine("[perf] warmup done");

        _output.WriteLine("[perf] full GC before measurement");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        _output.WriteLine($"[perf] measurement start (timeout={MeasurementTimeout.TotalSeconds}s)");
        await RunTrafficAsync(
            socks.Port,
            echoPort,
            totalBytes,
            chunkBytes,
            streams,
            payloadProfile,
            MeasurementTimeout,
            "measurement");
        sw.Stop();
        var allocAfter = GC.GetTotalAllocatedBytes(true);
        _output.WriteLine("[perf] measurement done");

        var allocated = allocAfter - allocBefore;
        var seconds = sw.Elapsed.TotalSeconds;
        var mib = totalBytes / 1024d / 1024d;
        var throughput = mib / seconds;
        var bytesPerMiB = allocated / mib;
        return new PerfResult(enableCompression, seconds, throughput, allocated, bytesPerMiB);
    }

    private async Task RunTrafficAsync(
        int socksPort,
        int echoPort,
        long totalBytes,
        int chunkBytes,
        int streams,
        PayloadProfile payloadProfile,
        TimeSpan timeout,
        string stage)
    {
        _output.WriteLine($"[perf] {stage}: preparing streams, totalBytes={totalBytes}, streams={streams}");
        var bytesPerStream = totalBytes / streams;
        var extra = totalBytes % streams;
        var tasks = new List<Task>(streams);
        using var cts = new CancellationTokenSource(timeout);

        for (var i = 0; i < streams; i++)
        {
            var streamBytes = bytesPerStream + (i == streams - 1 ? extra : 0);
            var streamId = i + 1;
            _output.WriteLine($"[perf] {stage}: stream#{streamId} start, bytes={streamBytes}");
            tasks.Add(RunSingleStreamAsync(socksPort, echoPort, streamBytes, chunkBytes, payloadProfile, streamId, cts.Token));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"[perf] {stage} timeout after {timeout.TotalSeconds}s.");
        }

        _output.WriteLine($"[perf] {stage}: all streams completed");
    }

    private async Task RunSingleStreamAsync(
        int socksPort,
        int echoPort,
        long totalBytes,
        int chunkBytes,
        PayloadProfile payloadProfile,
        int streamId,
        CancellationToken cancellationToken)
    {
        _output.WriteLine($"[perf] stream#{streamId}: connecting to SOCKS 127.0.0.1:{socksPort}");
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort, cancellationToken);
        using var stream = tcpClient.GetStream();
        _output.WriteLine($"[perf] stream#{streamId}: SOCKS TCP connected");

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        var greeting = await ReadExactAsync(stream, 2, cancellationToken);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
        {
            throw new InvalidOperationException("SOCKS5 greeting failed.");
        }
        _output.WriteLine($"[perf] stream#{streamId}: SOCKS greeting ok");

        await stream.WriteAsync(BuildConnectRequestIPv4(IPAddress.Loopback, echoPort), cancellationToken);
        var connect = await ReadExactAsync(stream, 10, cancellationToken);
        if (connect[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 connect failed: {connect[1]}");
        }
        _output.WriteLine($"[perf] stream#{streamId}: SOCKS connect to echo ok");

        var sendBuffer = BuildPayload(chunkBytes, payloadProfile);

        var readBuffer = new byte[chunkBytes];
        long sent = 0;
        var lastLoggedMiB = 0;
        while (sent < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toSend = (int)Math.Min(chunkBytes, totalBytes - sent);
            await stream.WriteAsync(sendBuffer.AsMemory(0, toSend), cancellationToken);

            var offset = 0;
            while (offset < toSend)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(offset, toSend - offset), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Unexpected EOF while reading echo.");
                }

                offset += read;
            }

            sent += toSend;
            if (sent == toSend)
            {
                _output.WriteLine($"[perf] stream#{streamId}: first payload exchange ok ({toSend} bytes)");
            }

            var sentMiB = (int)(sent / (1024 * 1024));
            if (sentMiB >= lastLoggedMiB + 16)
            {
                lastLoggedMiB = sentMiB;
                _output.WriteLine($"[perf] stream#{streamId}: sent={sentMiB}MiB/{totalBytes / (1024 * 1024)}MiB");
            }
        }

        _output.WriteLine($"[perf] stream#{streamId}: completed");
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

    private static async Task<RunningSocksServer> StartSocksServerAsync(
        int tunnelPort,
        bool enableCompression,
        TunnelCipherAlgorithm algorithm)
    {
        var port = AllocateUnusedPort();
        var server = new Socks5Server(
            IPAddress.Loopback,
            port,
            "127.0.0.1",
            tunnelPort,
            "dev-shared-key",
            new TunnelCryptoPolicy
            {
                HandshakeMaxClockSkewSeconds = TunnelCryptoPolicy.Default.HandshakeMaxClockSkewSeconds,
                ReplayWindowSeconds = TunnelCryptoPolicy.Default.ReplayWindowSeconds,
                PreferredAlgorithm = algorithm
            },
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

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
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
