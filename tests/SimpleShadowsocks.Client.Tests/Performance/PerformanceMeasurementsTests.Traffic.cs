using System.Net;
using System.Net.Sockets;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class PerformanceMeasurementsTests
{
    private async Task<ConnectedStreamGroup> PrepareConnectedStreamsAsync(
        int socksPort,
        int echoPort,
        long totalBytes,
        int chunkBytes,
        int streams,
        PayloadSet payloadSet,
        TimeSpan timeout)
    {
        _output.WriteLine($"[perf] measurement: preconnecting streams, totalBytes={totalBytes}, streams={streams}, payload={payloadSet.Profile}");
        var bytesPerStream = totalBytes / streams;
        var extra = totalBytes % streams;
        var connectedStreams = new List<ConnectedSocksStream>(streams);
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            for (var i = 0; i < streams; i++)
            {
                var streamBytes = bytesPerStream + (i == streams - 1 ? extra : 0);
                var streamId = i + 1;
                _output.WriteLine($"[perf] measurement: stream#{streamId} preconnect, bytes={streamBytes}");
                var connectedStream = await ConnectSocksStreamAsync(
                    socksPort,
                    echoPort,
                    streamBytes,
                    chunkBytes,
                    payloadSet,
                    streamId,
                    cts.Token);
                connectedStreams.Add(connectedStream);
            }

            return new ConnectedStreamGroup(connectedStreams);
        }
        catch
        {
            foreach (var connectedStream in connectedStreams)
            {
                await connectedStream.DisposeAsync();
            }

            throw;
        }
    }

    private async Task RunTrafficAsync(
        int socksPort,
        int echoPort,
        long totalBytes,
        int chunkBytes,
        int streams,
        PayloadSet payloadSet,
        TimeSpan timeout,
        string stage)
    {
        _output.WriteLine($"[perf] {stage}: preparing streams, totalBytes={totalBytes}, streams={streams}, payload={payloadSet.Profile}");
        var bytesPerStream = totalBytes / streams;
        var extra = totalBytes % streams;
        var tasks = new List<Task>(streams);
        using var cts = new CancellationTokenSource(timeout);

        for (var i = 0; i < streams; i++)
        {
            var streamBytes = bytesPerStream + (i == streams - 1 ? extra : 0);
            var streamId = i + 1;
            _output.WriteLine($"[perf] {stage}: stream#{streamId} start, bytes={streamBytes}");
            tasks.Add(RunSingleStreamAsync(socksPort, echoPort, streamBytes, chunkBytes, payloadSet, streamId, cts.Token));
        }

        var allTasks = Task.WhenAll(tasks);
        if (await Task.WhenAny(allTasks, Task.Delay(timeout)) != allTasks)
        {
            cts.Cancel();
            await DrainTasksAfterTimeoutAsync(tasks);
            throw new TimeoutException($"[perf] {stage} timeout after {timeout.TotalSeconds}s.");
        }

        try
        {
            await allTasks;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"[perf] {stage} timeout after {timeout.TotalSeconds}s.");
        }

        _output.WriteLine($"[perf] {stage}: all streams completed");
    }

    private async Task RunPreparedTrafficAsync(
        IReadOnlyList<ConnectedSocksStream> connectedStreams,
        int chunkBytes,
        PayloadSet payloadSet,
        TimeSpan timeout,
        string stage)
    {
        _output.WriteLine($"[perf] {stage}: running on preconnected streams={connectedStreams.Count}, payload={payloadSet.Profile}");
        var tasks = new List<Task>(connectedStreams.Count);
        using var cts = new CancellationTokenSource(timeout);

        foreach (var connectedStream in connectedStreams)
        {
            _output.WriteLine($"[perf] {stage}: stream#{connectedStream.StreamId} start, bytes={connectedStream.TotalBytes}");
            tasks.Add(RunPreparedStreamAsync(connectedStream, chunkBytes, payloadSet, cts.Token));
        }

        var allTasks = Task.WhenAll(tasks);
        if (await Task.WhenAny(allTasks, Task.Delay(timeout)) != allTasks)
        {
            cts.Cancel();
            await DrainTasksAfterTimeoutAsync(tasks);
            throw new TimeoutException($"[perf] {stage} timeout after {timeout.TotalSeconds}s.");
        }

        try
        {
            await allTasks;
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
        PayloadSet payloadSet,
        int streamId,
        CancellationToken cancellationToken)
    {
        _output.WriteLine($"[perf] stream#{streamId}: connecting to SOCKS 127.0.0.1:{socksPort}");
        using var tcpClient = new TcpClient();
        using var cancellationRegistration = cancellationToken.Register(static state => ((TcpClient)state!).Dispose(), tcpClient);
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

        var readBuffer = new byte[chunkBytes];
        long sent = 0;
        var chunkIndex = 0;
        var lastLoggedMiB = 0;
        while (sent < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toSend = (int)Math.Min(chunkBytes, totalBytes - sent);
            var sendBuffer = payloadSet.GetChunk(streamId, chunkIndex++);
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

    private async Task<ConnectedSocksStream> ConnectSocksStreamAsync(
        int socksPort,
        int echoPort,
        long totalBytes,
        int chunkBytes,
        PayloadSet payloadSet,
        int streamId,
        CancellationToken cancellationToken)
    {
        _output.WriteLine($"[perf] stream#{streamId}: connecting to SOCKS 127.0.0.1:{socksPort}");
        var tcpClient = new TcpClient();
        using var cancellationRegistration = cancellationToken.Register(static state => ((TcpClient)state!).Dispose(), tcpClient);
        try
        {
            await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort, cancellationToken);
            var stream = tcpClient.GetStream();
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
            return new ConnectedSocksStream(tcpClient, stream, streamId, totalBytes, new byte[chunkBytes]);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    private async Task RunPreparedStreamAsync(
        ConnectedSocksStream connectedStream,
        int chunkBytes,
        PayloadSet payloadSet,
        CancellationToken cancellationToken)
    {
        using var cancellationRegistration =
            cancellationToken.Register(static state => ((TcpClient)state!).Dispose(), connectedStream.Client);
        long sent = 0;
        var chunkIndex = 0;
        var lastLoggedMiB = 0;
        while (sent < connectedStream.TotalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toSend = (int)Math.Min(chunkBytes, connectedStream.TotalBytes - sent);
            var sendBuffer = payloadSet.GetChunk(connectedStream.StreamId, chunkIndex++);
            await connectedStream.Stream.WriteAsync(sendBuffer.AsMemory(0, toSend), cancellationToken);

            var offset = 0;
            while (offset < toSend)
            {
                var read = await connectedStream.Stream.ReadAsync(
                    connectedStream.ReadBuffer.AsMemory(offset, toSend - offset),
                    cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Unexpected EOF while reading echo.");
                }

                offset += read;
            }

            sent += toSend;
            if (sent == toSend)
            {
                _output.WriteLine($"[perf] stream#{connectedStream.StreamId}: first payload exchange ok ({toSend} bytes)");
            }

            var sentMiB = (int)(sent / (1024 * 1024));
            if (sentMiB >= lastLoggedMiB + 16)
            {
                lastLoggedMiB = sentMiB;
                _output.WriteLine(
                    $"[perf] stream#{connectedStream.StreamId}: sent={sentMiB}MiB/{connectedStream.TotalBytes / (1024 * 1024)}MiB");
            }
        }

        _output.WriteLine($"[perf] stream#{connectedStream.StreamId}: completed");
    }

    private static async Task DrainTasksAfterTimeoutAsync(IReadOnlyList<Task> tasks)
    {
        var drainTask = Task.WhenAll(tasks);
        await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(5)));
    }
}
