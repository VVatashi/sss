using System.Net.Sockets;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class PerformanceMeasurementsTests
{
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
        string PayloadProfile,
        TunnelCipherAlgorithm Cipher,
        string Compression,
        double Seconds,
        double ThroughputMibPerSec,
        long AllocatedBytes,
        double AllocatedBytesPerMiB,
        long TunnelBytesClientToServer,
        long TunnelBytesServerToClient)
    {
        public long TunnelBytesTotal => TunnelBytesClientToServer + TunnelBytesServerToClient;

        public override string ToString()
        {
            return $"Payload={PayloadProfile}, Cipher={Cipher}, Compression={Compression}, Elapsed={Seconds:F3}s, Throughput={ThroughputMibPerSec:F2} MiB/s, Alloc={AllocatedBytes:N0} bytes, Alloc/MiB={AllocatedBytesPerMiB:N0}, Tunnel C->S={TunnelBytesClientToServer:N0} bytes, S->C={TunnelBytesServerToClient:N0} bytes, Total={TunnelBytesTotal:N0} bytes";
        }
    }

    private readonly record struct CompressionMode(bool Enabled, PayloadCompressionAlgorithm Algorithm)
    {
        public static CompressionMode Disabled { get; } = new(false, PayloadCompressionAlgorithm.Deflate);

        public string DisplayName => Enabled ? Algorithm.ToString() : "off";
    }

    private sealed class PayloadSet
    {
        private readonly byte[][] _chunks;

        private PayloadSet(string profile, byte[][] chunks)
        {
            Profile = profile;
            _chunks = chunks;
        }

        public string Profile { get; }

        public static PayloadSet Create(PayloadProfile profile, int chunkBytes, int variantCount)
        {
            var chunks = new byte[variantCount][];
            var random = new Random(0x5EED1234);
            for (var i = 0; i < chunks.Length; i++)
            {
                var buffer = new byte[chunkBytes];
                if (profile == PayloadProfile.Compressible)
                {
                    var pattern = "ABCDABCDABCDABCD"u8.ToArray();
                    for (var j = 0; j < buffer.Length; j++)
                    {
                        buffer[j] = pattern[j % pattern.Length];
                    }
                }
                else
                {
                    random.NextBytes(buffer);
                }

                chunks[i] = buffer;
            }

            return new PayloadSet(profile.ToString(), chunks);
        }

        public byte[] GetChunk(int streamId, int chunkIndex)
        {
            var index = (streamId * 131 + chunkIndex) % _chunks.Length;
            return _chunks[index];
        }
    }

    private enum PayloadProfile
    {
        MixedNoise = 0,
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

    private sealed class RunningTunnelTrafficProxy : IAsyncDisposable
    {
        public RunningTunnelTrafficProxy(
            int port,
            CancellationTokenSource cts,
            Task runTask,
            Func<long> bytesClientToServer,
            Func<long> bytesServerToClient,
            Action resetCounters)
        {
            Port = port;
            _cts = cts;
            _runTask = runTask;
            _bytesClientToServer = bytesClientToServer;
            _bytesServerToClient = bytesServerToClient;
            _resetCounters = resetCounters;
        }

        public int Port { get; }
        public long BytesClientToServer => _bytesClientToServer();
        public long BytesServerToClient => _bytesServerToClient();

        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;
        private readonly Func<long> _bytesClientToServer;
        private readonly Func<long> _bytesServerToClient;
        private readonly Action _resetCounters;

        public void ResetCounters() => _resetCounters();

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _runTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private sealed class ConnectedSocksStream : IAsyncDisposable
    {
        public ConnectedSocksStream(TcpClient client, NetworkStream stream, int streamId, long totalBytes, byte[] readBuffer)
        {
            Client = client;
            Stream = stream;
            StreamId = streamId;
            TotalBytes = totalBytes;
            ReadBuffer = readBuffer;
        }

        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public int StreamId { get; }
        public long TotalBytes { get; }
        public byte[] ReadBuffer { get; }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConnectedStreamGroup : IAsyncDisposable
    {
        public ConnectedStreamGroup(IReadOnlyList<ConnectedSocksStream> streams)
        {
            Streams = streams;
        }

        public IReadOnlyList<ConnectedSocksStream> Streams { get; }

        public async ValueTask DisposeAsync()
        {
            foreach (var stream in Streams)
            {
                await stream.DisposeAsync();
            }
        }
    }
}
