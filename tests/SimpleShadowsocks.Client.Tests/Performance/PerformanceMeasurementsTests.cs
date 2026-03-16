using System.Diagnostics;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using Xunit.Abstractions;

namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Performance)]
public sealed partial class PerformanceMeasurementsTests
{
    private static TimeSpan WarmupTimeout => TimeSpan.FromSeconds(GetPerfIntOverride("SS_PERF_WARMUP_TIMEOUT_SEC", 90));
    private static TimeSpan MeasurementTimeout => TimeSpan.FromSeconds(GetPerfIntOverride("SS_PERF_MEASUREMENT_TIMEOUT_SEC", 600));
    private static readonly TunnelCipherAlgorithm[] CipherAlgorithms =
    [
        TunnelCipherAlgorithm.ChaCha20Poly1305,
        TunnelCipherAlgorithm.Aes256Gcm,
        TunnelCipherAlgorithm.Aegis128L,
        TunnelCipherAlgorithm.Aegis256
    ];

    private static readonly CompressionMode[] CompressionModes =
    [
        CompressionMode.Disabled,
        new CompressionMode(true, PayloadCompressionAlgorithm.Deflate),
        new CompressionMode(true, PayloadCompressionAlgorithm.Gzip),
        new CompressionMode(true, PayloadCompressionAlgorithm.Brotli)
    ];

    private readonly ITestOutputHelper _output;
    private readonly HashSet<TunnelCipherAlgorithm>? _cipherFilter = ParseCipherFilter();
    private readonly HashSet<string>? _compressionFilter = ParseCompressionFilter();

    public PerformanceMeasurementsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Measure_Throughput_And_Allocations_Matrix_MixedNoisePayload()
    {
        await MeasureMatrixAsync(PayloadProfile.MixedNoise);
    }

    [Fact]
    public async Task Measure_Throughput_And_Allocations_Matrix_CompressiblePayload()
    {
        await MeasureMatrixAsync(PayloadProfile.Compressible);
    }

    private async Task MeasureMatrixAsync(PayloadProfile payloadProfile)
    {
        var totalMb = GetPerfIntOverride("SS_PERF_TOTAL_MB", 128);
        var chunkKb = GetPerfIntOverride("SS_PERF_CHUNK_KB", 64);
        var streams = GetPerfIntOverride("SS_PERF_STREAMS", 4);
        var payloadVariants = GetPerfIntOverride("SS_PERF_PAYLOAD_VARIANTS", 256);
        var totalBytes = (long)totalMb * 1024 * 1024;
        var chunkBytes = chunkKb * 1024;

        var payloadSet = PayloadSet.Create(payloadProfile, chunkBytes, payloadVariants);
        var results = new List<PerfResult>();

        await using var echo = await StartEchoServerAsync();
        await using var tunnel = await StartTunnelServerAsync();

        for (var cipherIndex = 0; cipherIndex < CipherAlgorithms.Length; cipherIndex++)
        {
            var cipher = CipherAlgorithms[cipherIndex];
            if (_cipherFilter is not null && !_cipherFilter.Contains(cipher))
            {
                continue;
            }

            if (!IsCipherSupported(cipher))
            {
                _output.WriteLine($"[perf] skip unsupported cipher={cipher}");
                continue;
            }

            foreach (var compression in GetCompressionModes(cipherIndex))
            {
                if (_compressionFilter is not null && !_compressionFilter.Contains(compression.DisplayName))
                {
                    continue;
                }

                _output.WriteLine(
                    $"[perf] matrix start: cipher={cipher}, compression={compression.DisplayName}, payload={payloadSet.Profile}");

                var result = await MeasureModeAsync(
                    echo.Port,
                    tunnel.Port,
                    chunkBytes,
                    totalBytes,
                    streams,
                    payloadSet,
                    cipher,
                    compression);

                _output.WriteLine(result.ToString());
                Assert.True(
                    result.ThroughputMibPerSec > 5,
                    $"Unexpectedly low throughput ({cipher}, {compression.DisplayName}): {result.ThroughputMibPerSec:F2} MiB/s");
                results.Add(result);
            }
        }

        var markdown = BuildMarkdownTable(results);
        _output.WriteLine($"=== Performance matrix (markdown, payload={payloadSet.Profile}) ===");
        _output.WriteLine(markdown);
        Console.WriteLine(markdown);
    }

    private async Task<PerfResult> MeasureModeAsync(
        int echoPort,
        int tunnelPort,
        int chunkBytes,
        long totalBytes,
        int streams,
        PayloadSet payloadSet,
        TunnelCipherAlgorithm algorithm,
        CompressionMode compression)
    {
        await using var tunnelProxy = await StartTunnelTrafficProxyAsync(tunnelPort);
        _output.WriteLine(
            $"[perf] start socks server (cipher={algorithm}, compression={compression.DisplayName}, payload={payloadSet.Profile})");
        await using var socks = await StartSocksServerAsync(tunnelProxy.Port, algorithm, compression);
        _output.WriteLine($"[perf] socks server ready on 127.0.0.1:{socks.Port}");

        _output.WriteLine($"[perf] warmup start (timeout={WarmupTimeout.TotalSeconds}s)");
        await RunTrafficAsync(
            socks.Port,
            echoPort,
            8L * 1024 * 1024,
            chunkBytes,
            2,
            payloadSet,
            WarmupTimeout,
            "warmup");
        _output.WriteLine("[perf] warmup done");

        _output.WriteLine("[perf] full GC before measurement");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _output.WriteLine("[perf] preparing connected measurement streams");
        await using var connectedStreams = await PrepareConnectedStreamsAsync(
            socks.Port,
            echoPort,
            totalBytes,
            chunkBytes,
            streams,
            payloadSet,
            MeasurementTimeout);

        tunnelProxy.ResetCounters();
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        _output.WriteLine($"[perf] measurement start (timeout={MeasurementTimeout.TotalSeconds}s)");
        var sw = Stopwatch.StartNew();
        await RunPreparedTrafficAsync(
            connectedStreams.Streams,
            chunkBytes,
            payloadSet,
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
        return new PerfResult(
            payloadSet.Profile,
            algorithm,
            compression.DisplayName,
            seconds,
            throughput,
            allocated,
            bytesPerMiB,
            tunnelProxy.BytesClientToServer,
            tunnelProxy.BytesServerToClient);
    }

    private static IEnumerable<CompressionMode> GetCompressionModes(int startIndex)
    {
        for (var offset = 0; offset < CompressionModes.Length; offset++)
        {
            yield return CompressionModes[(startIndex + offset) % CompressionModes.Length];
        }
    }

    private static string BuildMarkdownTable(IReadOnlyList<PerfResult> results)
    {
        var lines = new List<string>
        {
            "| Payload | Cipher | Compression | Throughput (MiB/s) | Alloc/MiB (bytes) | Tunnel C->S (bytes) | Tunnel S->C (bytes) | Tunnel Total (bytes) |",
            "|---|---|---|---:|---:|---:|---:|---:|"
        };

        foreach (var result in results.OrderBy(static result => result.PayloadProfile, StringComparer.Ordinal)
                     .ThenBy(static result => Array.IndexOf(CipherAlgorithms, result.Cipher))
                     .ThenBy(static result => GetCompressionSortKey(result.Compression)))
        {
            lines.Add(
                $"| `{result.PayloadProfile}` | `{result.Cipher}` | `{result.Compression}` | {result.ThroughputMibPerSec:F2} | {result.AllocatedBytesPerMiB:N0} | {result.TunnelBytesClientToServer:N0} | {result.TunnelBytesServerToClient:N0} | {result.TunnelBytesTotal:N0} |");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int GetCompressionSortKey(string compressionDisplayName)
    {
        for (var i = 0; i < CompressionModes.Length; i++)
        {
            if (CompressionModes[i].DisplayName == compressionDisplayName)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static HashSet<TunnelCipherAlgorithm>? ParseCipherFilter()
    {
        var raw = Environment.GetEnvironmentVariable("SS_PERF_CIPHERS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var result = new HashSet<TunnelCipherAlgorithm>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<TunnelCipherAlgorithm>(token, ignoreCase: true, out var algorithm))
            {
                result.Add(algorithm);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static HashSet<string>? ParseCompressionFilter()
    {
        var raw = Environment.GetEnvironmentVariable("SS_PERF_COMPRESSIONS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(token);
        }

        return result.Count > 0 ? result : null;
    }

    private static int GetPerfIntOverride(string variableName, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        return int.TryParse(raw, out var value) && value > 0 ? value : defaultValue;
    }

    private static bool IsCipherSupported(TunnelCipherAlgorithm algorithm)
    {
        return algorithm switch
        {
            TunnelCipherAlgorithm.Aegis128L or TunnelCipherAlgorithm.Aegis256 => AeadDuplexStream.IsSupported(algorithm),
            _ => true
        };
    }
}
