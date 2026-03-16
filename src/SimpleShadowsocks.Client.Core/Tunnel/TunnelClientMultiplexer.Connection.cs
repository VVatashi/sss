using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer
{
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
        _configureTcpSocket?.Invoke(_tcpClient.Client);
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
            state.ReaderWriter?.Writer.TryComplete();
            state.UdpReaderWriter?.Writer.TryComplete();
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
}
