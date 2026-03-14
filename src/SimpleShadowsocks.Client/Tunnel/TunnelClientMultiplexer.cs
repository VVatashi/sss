using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed class TunnelClientMultiplexer : IAsyncDisposable
{
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelConnectionPolicy _connectionPolicy;
    private readonly byte _protocolVersion;
    private readonly ProtocolWriteOptions _writeOptions;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, SessionState> _sessions = new();

    private TcpClient? _tcpClient;
    private Stream? _secureStream;
    private CancellationTokenSource? _connectionCts;
    private Task? _readLoopTask;
    private Task? _writeLoopTask;
    private Task? _heartbeatLoopTask;
    private Channel<OutboundFrame>? _outboundFrames;
    private Exception? _connectionError;
    private int _nextSessionId;
    private int _connectionGeneration;
    private long _lastIncomingUtcTicks;
    private long _controlSendSequence;
    private volatile bool _disposing;

    public TunnelClientMultiplexer(
        string remoteHost,
        int remotePort,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
        TunnelConnectionPolicy connectionPolicy,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate)
    {
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _sharedKey = sharedKey;
        _cryptoPolicy = cryptoPolicy;
        _connectionPolicy = connectionPolicy;
        _protocolVersion = protocolVersion;
        _writeOptions = new ProtocolWriteOptions
        {
            Version = protocolVersion,
            EnableCompression = enableCompression,
            CompressionAlgorithm = compressionAlgorithm
        };
        ValidatePolicy(_connectionPolicy);
        TouchIncoming();
    }

    public async Task<(uint SessionId, byte ReplyCode, ChannelReader<byte[]> Reader)> OpenSessionAsync(
        ConnectRequest connectRequest,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sessions.Count >= _connectionPolicy.MaxConcurrentSessions)
        {
            throw new InvalidOperationException(
                $"Max concurrent sessions limit reached: {_connectionPolicy.MaxConcurrentSessions}.");
        }

        var sessionId = (uint)Interlocked.Increment(ref _nextSessionId);
        var state = new SessionState(connectRequest, _connectionPolicy.SessionReceiveChannelCapacity);
        if (!_sessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException("Failed to register tunnel session.");
        }

        try
        {
            var replyCode = await ConnectSessionAsync(sessionId, state, cancellationToken);
            if (replyCode != 0x00)
            {
                _sessions.TryRemove(sessionId, out _);
                state.ReaderWriter.Writer.TryComplete();
            }

            return (sessionId, replyCode, state.ReaderWriter.Reader);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            state.ReaderWriter.Writer.TryComplete();
            throw;
        }
    }

    public Task SendDataAsync(uint sessionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        return SendSessionFrameAsync(sessionId, FrameType.Data, payload, cancellationToken);
    }

    public async Task CloseSessionAsync(uint sessionId, byte reasonCode, CancellationToken cancellationToken)
    {
        if (_sessions.TryRemove(sessionId, out var state))
        {
            state.MarkClosed();
            state.FailConnect(new IOException("Session is closed."));
            var sequence = state.TakeNextSendSequence();
            try
            {
                await SendFrameAsync(
                    new ProtocolFrame(FrameType.Close, sessionId, sequence, ProtocolPayloadSerializer.SerializeClose(reasonCode)),
                    cancellationToken);
            }
            catch
            {
            }

            state.ReaderWriter.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        await HandleConnectionFaultAsync(preserveSessions: false);

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask; } catch { }
        }

        if (_heartbeatLoopTask is not null)
        {
            try { await _heartbeatLoopTask; } catch { }
        }

        if (_writeLoopTask is not null)
        {
            try { await _writeLoopTask; } catch { }
        }

        _connectLock.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected())
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected())
            {
                return;
            }

            Exception? lastError = null;
            for (var attempt = 1; attempt <= _connectionPolicy.ReconnectMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ConnectOnceAsync(cancellationToken);
                    await RestoreSessionsAsync(cancellationToken);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await HandleConnectionFaultAsync(preserveSessions: true);

                    if (attempt == _connectionPolicy.ReconnectMaxAttempts)
                    {
                        break;
                    }

                    var delayMs = Math.Min(
                        _connectionPolicy.ReconnectMaxDelayMs,
                        _connectionPolicy.ReconnectBaseDelayMs * (1 << Math.Min(attempt - 1, 6)));

                    await Task.Delay(delayMs, cancellationToken);
                }
            }

            await HandleConnectionFaultAsync(preserveSessions: false);
            throw new IOException($"Unable to connect tunnel after {_connectionPolicy.ReconnectMaxAttempts} attempts.", lastError);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_remoteHost, _remotePort, cancellationToken);

        var networkStream = _tcpClient.GetStream();
        _secureStream = await TunnelCryptoHandshake.AsClientAsync(
            networkStream,
            _sharedKey,
            _cryptoPolicy,
            cancellationToken);

        _connectionCts = new CancellationTokenSource();
        _connectionError = null;
        _outboundFrames = Channel.CreateUnbounded<OutboundFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        TouchIncoming();

        var generation = Interlocked.Increment(ref _connectionGeneration);
        _writeLoopTask = Task.Run(() => WriteLoopAsync(generation, _connectionCts.Token));
        _readLoopTask = Task.Run(() => ReadLoopAsync(generation, _connectionCts.Token));
        _heartbeatLoopTask = Task.Run(() => HeartbeatLoopAsync(generation, _connectionCts.Token));
    }

    private bool IsConnected()
    {
        return _secureStream is not null
            && _outboundFrames is not null
            && _connectionCts is not null
            && !_connectionCts.IsCancellationRequested
            && _readLoopTask is { IsCompleted: false }
            && _writeLoopTask is { IsCompleted: false };
    }

    private async Task SendFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var outboundFrames = _outboundFrames;
        if (outboundFrames is null || _connectionCts is null || _connectionCts.IsCancellationRequested)
        {
            throw new InvalidOperationException("Tunnel is not connected.");
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!outboundFrames.Writer.TryWrite(new OutboundFrame(frame, completion)))
        {
            throw new IOException("Failed to enqueue outbound frame.");
        }

        await completion.Task.WaitAsync(cancellationToken);
    }

    private Task SendSessionFrameAsync(uint sessionId, FrameType frameType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            throw new InvalidOperationException($"Session {sessionId} is not active.");
        }

        return SendSessionFrameAsync(sessionId, state, frameType, payload, cancellationToken);
    }

    private async Task ReadLoopAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_secureStream is null)
                {
                    return;
                }

                var frameResult = await ProtocolFrameCodec.ReadDetailedAsync(_secureStream, cancellationToken);
                if (frameResult is null)
                {
                    break;
                }
                var frame = frameResult.Value.Frame;

                if (frameResult.Value.Version != _protocolVersion)
                {
                    throw new InvalidDataException(
                        $"Protocol version mismatch: client expects v{_protocolVersion}, server replied with v{frameResult.Value.Version}.");
                }

                TouchIncoming();

                if (frame.SessionId == 0)
                {
                    if (frame.Type == FrameType.Ping)
                    {
                        var sequence = (ulong)Interlocked.Increment(ref _controlSendSequence);
                        await SendFrameAsync(new ProtocolFrame(FrameType.Pong, 0, sequence, frame.Payload), cancellationToken);
                    }

                    continue;
                }

                if (!_sessions.TryGetValue(frame.SessionId, out var state))
                {
                    continue;
                }

                if (!state.TryAcceptIncomingFrame(frame.Type, frame.Sequence))
                {
                    await CloseSessionAsync(frame.SessionId, 0x21, CancellationToken.None);
                    continue;
                }

                switch (frame.Type)
                {
                    case FrameType.Connect:
                        state.CompleteConnectReply(frame.Payload.Length >= 1 ? frame.Payload.Span[0] : (byte)0x01);
                        break;

                    case FrameType.Data:
                        await state.ReaderWriter.Writer.WriteAsync(DetachPayload(frame.Payload), cancellationToken);
                        break;

                    case FrameType.Close:
                        state.MarkClosed();
                        state.FailConnect(new IOException("Session closed by remote."));
                        state.ReaderWriter.Writer.TryComplete();
                        _sessions.TryRemove(frame.SessionId, out _);
                        break;

                    case FrameType.Ping:
                        await SendSessionFrameAsync(frame.SessionId, FrameType.Pong, frame.Payload, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException ex) when (_protocolVersion != ProtocolConstants.LegacyVersion)
        {
            _connectionError = new IOException(
                $"Tunnel closed unexpectedly while using protocol v{_protocolVersion}. Possible protocol version mismatch with server.",
                ex);
            throw _connectionError;
        }
        catch (Exception ex)
        {
            _connectionError = ex;
            throw new IOException(
                $"Tunnel read loop failed: {ex.Message}",
                ex);
        }
        finally
        {
            await HandleConnectionFaultAsync(preserveSessions: true, expectedGeneration: generation);
        }
    }

    private async Task WriteLoopAsync(int generation, CancellationToken cancellationToken)
    {
        var outboundFrames = _outboundFrames;
        if (_secureStream is null || outboundFrames is null)
        {
            return;
        }

        var writer = PipeWriter.Create(_secureStream, new StreamPipeWriterOptions(leaveOpen: true));
        var batch = new List<OutboundFrame>(64);
        const int maxBatchFrames = 64;
        const int maxBatchBytes = 512 * 1024;

        Exception? terminalError = null;
        try
        {
            while (await outboundFrames.Reader.WaitToReadAsync(cancellationToken))
            {
                batch.Clear();
                var accumulatedBytes = 0;
                while (outboundFrames.Reader.TryRead(out var outbound))
                {
                    try
                    {
                        ProtocolFrameCodec.WriteTo(writer, outbound.Frame, _writeOptions);
                        batch.Add(outbound);
                        accumulatedBytes += ProtocolConstants.HeaderSize + outbound.Frame.Payload.Length;

                        if (batch.Count >= maxBatchFrames || accumulatedBytes >= maxBatchBytes)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        outbound.Completion.TrySetException(ex);
                        throw;
                    }
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                var flushResult = await writer.FlushAsync(cancellationToken);
                if (flushResult.IsCanceled)
                {
                    throw new OperationCanceledException("Outbound tunnel writer flush was canceled.");
                }

                foreach (var sent in batch)
                {
                    sent.Completion.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            terminalError = new OperationCanceledException("Outbound tunnel writer was canceled.");
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            try { await writer.CompleteAsync(); } catch { }

            while (outboundFrames.Reader.TryRead(out var outbound))
            {
                outbound.Completion.TrySetException(terminalError ?? new IOException("Tunnel writer stopped."));
            }

            await HandleConnectionFaultAsync(preserveSessions: true, expectedGeneration: generation);
        }
    }

    private async Task HeartbeatLoopAsync(int generation, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _connectionPolicy.HeartbeatIntervalSeconds));
        var idleTimeout = TimeSpan.FromSeconds(Math.Max(5, _connectionPolicy.IdleTimeoutSeconds));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);

                var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastIncomingUtcTicks), DateTimeKind.Utc);
                if (idle > idleTimeout)
                {
                    throw new TimeoutException($"Tunnel idle timeout exceeded ({idleTimeout}).");
                }

                var seq = (ulong)Interlocked.Increment(ref _controlSendSequence);
                await SendFrameAsync(new ProtocolFrame(FrameType.Ping, 0, seq, Array.Empty<byte>()), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await HandleConnectionFaultAsync(preserveSessions: true, expectedGeneration: generation);
        }
    }

    private void TouchIncoming()
    {
        Interlocked.Exchange(ref _lastIncomingUtcTicks, DateTime.UtcNow.Ticks);
    }

    private static void ValidatePolicy(TunnelConnectionPolicy policy)
    {
        if (policy.ReconnectMaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.ReconnectMaxAttempts), "ReconnectMaxAttempts must be > 0.");
        }

        if (policy.ReconnectBaseDelayMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.ReconnectBaseDelayMs), "ReconnectBaseDelayMs must be > 0.");
        }

        if (policy.ReconnectMaxDelayMs < policy.ReconnectBaseDelayMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.ReconnectMaxDelayMs),
                "ReconnectMaxDelayMs must be >= ReconnectBaseDelayMs.");
        }

        if (policy.MaxConcurrentSessions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.MaxConcurrentSessions), "MaxConcurrentSessions must be > 0.");
        }

        if (policy.SessionReceiveChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.SessionReceiveChannelCapacity),
                "SessionReceiveChannelCapacity must be > 0.");
        }
    }

    private static byte[] DetachPayload(ReadOnlyMemory<byte> payload)
    {
        if (MemoryMarshal.TryGetArray(payload, out var segment)
            && segment.Array is not null
            && segment.Offset == 0
            && segment.Count == segment.Array.Length)
        {
            return segment.Array;
        }

        return payload.ToArray();
    }

    private async Task<byte> ConnectSessionAsync(uint sessionId, SessionState state, CancellationToken cancellationToken)
    {
        state.BeginConnectAttempt();
        var payload = ProtocolPayloadSerializer.SerializeConnectRequest(state.ConnectRequest);
        try
        {
            await SendFrameAsync(new ProtocolFrame(FrameType.Connect, sessionId, 0, payload), cancellationToken);
        }
        catch (Exception ex)
        {
            state.FailConnect(ex);
            throw;
        }

        return await state.WaitForConnectReplyAsync(cancellationToken);
    }

    private async Task RestoreSessionsAsync(CancellationToken cancellationToken)
    {
        if (_sessions.IsEmpty)
        {
            return;
        }

        foreach (var (sessionId, state) in _sessions.ToArray())
        {
            if (state.IsClosed || !_sessions.ContainsKey(sessionId))
            {
                continue;
            }

            byte replyCode;
            try
            {
                replyCode = await ConnectSessionAsync(sessionId, state, cancellationToken);
            }
            catch (Exception ex)
            {
                state.ReaderWriter.Writer.TryComplete(ex);
                state.MarkClosed();
                state.FailConnect(ex);
                _sessions.TryRemove(sessionId, out _);
                continue;
            }

            if (replyCode != 0x00)
            {
                state.MarkClosed();
                state.FailConnect(new IOException($"Session resume failed with remote reply code {replyCode}."));
                state.ReaderWriter.Writer.TryComplete(
                    new IOException($"Session resume failed with remote reply code {replyCode}."));
                _sessions.TryRemove(sessionId, out _);
            }
        }
    }

    private async Task SendSessionFrameAsync(
        uint sessionId,
        SessionState state,
        FrameType frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureConnectedAsync(cancellationToken);
            await state.WaitUntilActiveAsync(cancellationToken);

            if (!_sessions.ContainsKey(sessionId) || state.IsClosed)
            {
                throw new InvalidOperationException($"Session {sessionId} is not active.");
            }

            var sequence = state.TakeNextSendSequence();
            try
            {
                await SendFrameAsync(new ProtocolFrame(frameType, sessionId, sequence, payload), cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (!_disposing)
            {
                // Connection likely rotated while sending. Reconnect/resume and retry.
                continue;
            }
        }
    }

    private async Task HandleConnectionFaultAsync(bool preserveSessions, int? expectedGeneration = null)
    {
        if (expectedGeneration.HasValue && Volatile.Read(ref _connectionGeneration) != expectedGeneration.Value)
        {
            return;
        }

        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;

        var outboundFrames = _outboundFrames;
        if (outboundFrames is not null)
        {
            outboundFrames.Writer.TryComplete();
            while (outboundFrames.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetException(new IOException("Tunnel connection fault."));
            }
        }

        _outboundFrames = null;

        if (_secureStream is IAsyncDisposable asyncDisposable)
        {
            try { await asyncDisposable.DisposeAsync(); } catch { }
        }
        else
        {
            try { _secureStream?.Dispose(); } catch { }
        }

        _secureStream = null;

        try { _tcpClient?.Dispose(); } catch { }
        _tcpClient = null;

        if (preserveSessions)
        {
            var error = _connectionError ?? new IOException("Tunnel connection fault.");
            foreach (var (_, state) in _sessions)
            {
                state.NotifyConnectionFault(error);
            }
            return;
        }

        foreach (var (sessionId, state) in _sessions.ToArray())
        {
            state.MarkClosed();
            state.ReaderWriter.Writer.TryComplete();
            if (_connectionError is not null)
            {
                state.FailConnect(_connectionError);
            }
            else
            {
                state.FailConnect(new IOException("Tunnel connection closed."));
            }
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private sealed class SessionState
    {
        private readonly object _sequenceLock = new();
        private readonly object _connectLock = new();
        private ulong _nextSendSequence;
        private ulong _nextExpectedIncomingSequence;
        private bool _awaitingConnectReply;
        private bool _closed;
        private TaskCompletionSource<byte> _pendingConnectReply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _activeReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConnectRequest ConnectRequest { get; }

        public Channel<byte[]> ReaderWriter { get; }

        public SessionState(ConnectRequest connectRequest, int receiveChannelCapacity)
        {
            ConnectRequest = connectRequest;
            ReaderWriter = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(receiveChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public bool IsClosed
        {
            get
            {
                lock (_connectLock)
                {
                    return _closed;
                }
            }
        }

        public void BeginConnectAttempt()
        {
            lock (_connectLock)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("Session is closed.");
                }

                _pendingConnectReply = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            lock (_sequenceLock)
            {
                _nextSendSequence = 1;
                _nextExpectedIncomingSequence = 0;
                _awaitingConnectReply = true;
            }
        }

        public Task<byte> WaitForConnectReplyAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<byte> pending;
            lock (_connectLock)
            {
                pending = _pendingConnectReply;
            }

            return pending.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilActiveAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("Session is closed.");
                }

                ready = _activeReady;
            }

            return ready.Task.WaitAsync(cancellationToken);
        }

        public void CompleteConnectReply(byte replyCode)
        {
            TaskCompletionSource<byte> connectReply;
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                connectReply = _pendingConnectReply;
                ready = _activeReady;
            }

            connectReply.TrySetResult(replyCode);
            if (replyCode == 0x00)
            {
                ready.TrySetResult(true);
            }
            else
            {
                ready.TrySetException(new IOException($"Remote CONNECT failed with code {replyCode}."));
            }
        }

        public void FailConnect(Exception ex)
        {
            TaskCompletionSource<byte> connectReply;
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                connectReply = _pendingConnectReply;
                ready = _activeReady;
            }

            connectReply.TrySetException(ex);
            ready.TrySetException(ex);
        }

        public void NotifyConnectionFault(Exception ex)
        {
            lock (_sequenceLock)
            {
                if (!_awaitingConnectReply)
                {
                    return;
                }

                _awaitingConnectReply = false;
            }

            FailConnect(ex);
        }

        public void MarkClosed()
        {
            lock (_connectLock)
            {
                _closed = true;
            }
        }

        public ulong TakeNextSendSequence()
        {
            lock (_sequenceLock)
            {
                return _nextSendSequence++;
            }
        }

        public bool TryAcceptIncomingFrame(FrameType frameType, ulong sequence)
        {
            lock (_sequenceLock)
            {
                if (_awaitingConnectReply)
                {
                    if (frameType != FrameType.Connect || sequence != 0)
                    {
                        return false;
                    }

                    _awaitingConnectReply = false;
                    _nextExpectedIncomingSequence = 1;
                    return true;
                }

                if (sequence != _nextExpectedIncomingSequence)
                {
                    return false;
                }

                _nextExpectedIncomingSequence++;
                return true;
            }
        }
    }

    private readonly record struct OutboundFrame(ProtocolFrame Frame, TaskCompletionSource<bool> Completion);
}
