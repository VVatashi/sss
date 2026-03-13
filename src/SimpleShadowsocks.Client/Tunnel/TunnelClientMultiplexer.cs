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

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, SessionState> _sessions = new();

    private TcpClient? _tcpClient;
    private Stream? _secureStream;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private int _nextSessionId;

    public TunnelClientMultiplexer(string remoteHost, int remotePort, byte[] sharedKey, TunnelCryptoPolicy cryptoPolicy)
    {
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _sharedKey = sharedKey;
        _cryptoPolicy = cryptoPolicy;
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
            await SendFrameAsync(new ProtocolFrame(FrameType.Connect, sessionId, payload), cancellationToken);

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
        return SendFrameAsync(new ProtocolFrame(FrameType.Data, sessionId, payload), cancellationToken);
    }

    public async Task CloseSessionAsync(uint sessionId, byte reasonCode, CancellationToken cancellationToken)
    {
        await SendFrameAsync(
            new ProtocolFrame(FrameType.Close, sessionId, ProtocolPayloadSerializer.SerializeClose(reasonCode)),
            cancellationToken);

        if (_sessions.TryRemove(sessionId, out var state))
        {
            state.ReaderWriter.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_readLoopCts is not null)
        {
            _readLoopCts.Cancel();
        }

        if (_readLoopTask is not null)
        {
            try
            {
                await _readLoopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var session in _sessions.Values)
        {
                session.ReaderWriter.Writer.TryComplete();
        }

        _sessions.Clear();
        _readLoopCts?.Dispose();
        if (_secureStream is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _secureStream?.Dispose();
        }

        _tcpClient?.Dispose();
        _connectLock.Dispose();
        _writeLock.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_secureStream is not null && _readLoopTask is { IsCompleted: false })
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_secureStream is not null && _readLoopTask is { IsCompleted: false })
            {
                return;
            }

            _tcpClient?.Dispose();
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_remoteHost, _remotePort, cancellationToken);

            var networkStream = _tcpClient.GetStream();
            _secureStream = await TunnelCryptoHandshake.AsClientAsync(
                networkStream,
                _sharedKey,
                _cryptoPolicy,
                cancellationToken);

            _readLoopCts?.Dispose();
            _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);
        }
        finally
        {
            _connectLock.Release();
        }
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
        finally
        {
            _writeLock.Release();
        }
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

                if (!_sessions.TryGetValue(frame.Value.SessionId, out var state))
                {
                    continue;
                }

                switch (frame.Value.Type)
                {
                    case FrameType.Connect:
                        if (frame.Value.Payload.Length >= 1)
                        {
                            state.ConnectReply.TrySetResult(frame.Value.Payload.Span[0]);
                        }
                        else
                        {
                            state.ConnectReply.TrySetResult(0x01);
                        }
                        break;
                    case FrameType.Data:
                        state.ReaderWriter.Writer.TryWrite(frame.Value.Payload.ToArray());
                        break;
                    case FrameType.Close:
                        state.ReaderWriter.Writer.TryComplete();
                        _sessions.TryRemove(frame.Value.SessionId, out _);
                        break;
                    case FrameType.Ping:
                        await SendFrameAsync(
                            new ProtocolFrame(FrameType.Pong, frame.Value.SessionId, frame.Value.Payload),
                            cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var (sessionId, state) in _sessions.ToArray())
            {
                state.ReaderWriter.Writer.TryComplete();
                state.ConnectReply.TrySetResult(0x01);
                _sessions.TryRemove(sessionId, out _);
            }
        }
    }

    private sealed class SessionState
    {
        public TaskCompletionSource<byte> ConnectReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<byte[]> ReaderWriter { get; } =
            Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
    }
}
