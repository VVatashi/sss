using System.Collections.Concurrent;
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

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, SessionState> _sessions = new();

    private TcpClient? _tcpClient;
    private Stream? _secureStream;
    private CancellationTokenSource? _connectionCts;
    private Task? _readLoopTask;
    private Task? _heartbeatLoopTask;
    private int _nextSessionId;
    private long _lastIncomingUtcTicks;
    private long _controlSendSequence;

    public TunnelClientMultiplexer(
        string remoteHost,
        int remotePort,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
        TunnelConnectionPolicy connectionPolicy)
    {
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _sharedKey = sharedKey;
        _cryptoPolicy = cryptoPolicy;
        _connectionPolicy = connectionPolicy;
        TouchIncoming();
    }

    public async Task<(uint SessionId, byte ReplyCode, ChannelReader<byte[]> Reader)> OpenSessionAsync(
        ConnectRequest connectRequest,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);

        var sessionId = (uint)Interlocked.Increment(ref _nextSessionId);
        var state = new SessionState();
        if (!_sessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException("Failed to register tunnel session.");
        }

        try
        {
            var payload = ProtocolPayloadSerializer.SerializeConnectRequest(connectRequest);
            await SendSessionFrameAsync(sessionId, FrameType.Connect, payload, cancellationToken);

            var replyCode = await state.ConnectReply.Task.WaitAsync(cancellationToken);
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

    public Task SendDataAsync(uint sessionId, byte[] payload, CancellationToken cancellationToken)
    {
        return SendSessionFrameAsync(sessionId, FrameType.Data, payload, cancellationToken);
    }

    public async Task CloseSessionAsync(uint sessionId, byte reasonCode, CancellationToken cancellationToken)
    {
        if (_sessions.TryRemove(sessionId, out var state))
        {
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
        await HandleConnectionFaultAsync();

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask; } catch { }
        }

        if (_heartbeatLoopTask is not null)
        {
            try { await _heartbeatLoopTask; } catch { }
        }

        _connectLock.Dispose();
        _writeLock.Dispose();
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
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await HandleConnectionFaultAsync();

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
        TouchIncoming();

        _readLoopTask = Task.Run(() => ReadLoopAsync(_connectionCts.Token));
        _heartbeatLoopTask = Task.Run(() => HeartbeatLoopAsync(_connectionCts.Token));
    }

    private bool IsConnected()
    {
        return _secureStream is not null
            && _connectionCts is not null
            && !_connectionCts.IsCancellationRequested
            && _readLoopTask is { IsCompleted: false };
    }

    private async Task SendFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (_secureStream is null)
        {
            throw new InvalidOperationException("Tunnel is not connected.");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await ProtocolFrameCodec.WriteAsync(_secureStream, frame, cancellationToken);
        }
        catch
        {
            await HandleConnectionFaultAsync();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Task SendSessionFrameAsync(uint sessionId, FrameType frameType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            throw new InvalidOperationException($"Session {sessionId} is not active.");
        }

        var sequence = state.TakeNextSendSequence();
        return SendFrameAsync(new ProtocolFrame(frameType, sessionId, sequence, payload), cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_secureStream is null)
                {
                    return;
                }

                var frame = await ProtocolFrameCodec.ReadAsync(_secureStream, cancellationToken);
                if (frame is null)
                {
                    break;
                }

                TouchIncoming();

                if (frame.Value.SessionId == 0)
                {
                    if (frame.Value.Type == FrameType.Ping)
                    {
                        var sequence = (ulong)Interlocked.Increment(ref _controlSendSequence);
                        await SendFrameAsync(new ProtocolFrame(FrameType.Pong, 0, sequence, frame.Value.Payload), cancellationToken);
                    }

                    continue;
                }

                if (!_sessions.TryGetValue(frame.Value.SessionId, out var state))
                {
                    continue;
                }

                if (!state.TryAcceptIncomingSequence(frame.Value.Sequence))
                {
                    await CloseSessionAsync(frame.Value.SessionId, 0x21, CancellationToken.None);
                    continue;
                }

                switch (frame.Value.Type)
                {
                    case FrameType.Connect:
                        state.ConnectReply.TrySetResult(frame.Value.Payload.Length >= 1 ? frame.Value.Payload.Span[0] : (byte)0x01);
                        break;

                    case FrameType.Data:
                        state.ReaderWriter.Writer.TryWrite(frame.Value.Payload.ToArray());
                        break;

                    case FrameType.Close:
                        state.ReaderWriter.Writer.TryComplete();
                        _sessions.TryRemove(frame.Value.SessionId, out _);
                        break;

                    case FrameType.Ping:
                        await SendSessionFrameAsync(frame.Value.SessionId, FrameType.Pong, frame.Value.Payload, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await HandleConnectionFaultAsync();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
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
            await HandleConnectionFaultAsync();
        }
    }

    private void TouchIncoming()
    {
        Interlocked.Exchange(ref _lastIncomingUtcTicks, DateTime.UtcNow.Ticks);
    }

    private async Task HandleConnectionFaultAsync()
    {
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;

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

        foreach (var (sessionId, state) in _sessions.ToArray())
        {
            state.ReaderWriter.Writer.TryComplete();
            state.ConnectReply.TrySetResult(0x01);
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private sealed class SessionState
    {
        private readonly object _sequenceLock = new();
        private ulong _nextSendSequence;
        private ulong _nextExpectedIncomingSequence;

        public TaskCompletionSource<byte> ConnectReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<byte[]> ReaderWriter { get; } =
            Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        public ulong TakeNextSendSequence()
        {
            lock (_sequenceLock)
            {
                return _nextSendSequence++;
            }
        }

        public bool TryAcceptIncomingSequence(ulong sequence)
        {
            lock (_sequenceLock)
            {
                if (sequence != _nextExpectedIncomingSequence)
                {
                    return false;
                }

                _nextExpectedIncomingSequence++;
                return true;
            }
        }
    }
}
