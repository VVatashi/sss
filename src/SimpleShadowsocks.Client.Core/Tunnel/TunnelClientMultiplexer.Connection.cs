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
                    StructuredLog.Info(
                        "tunnel-client",
                        "TUNNEL/TCP",
                        $"connecting remote={_remoteHost}:{_remotePort} attempt={attempt}/{_connectionPolicy.ReconnectMaxAttempts}");
                    await ConnectOnceAsync(cancellationToken);
                    await RestoreSessionsAsync(cancellationToken);
                    StructuredLog.Info("tunnel-client", "TUNNEL/TCP", "connected");
                    return;
                }
                catch (Exception ex)
                {
                    StructuredLog.Error("tunnel-client", "TUNNEL/TCP", "connect attempt failed", ex);
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
        await SendFrameAsync(new OutboundFrame(
            frame,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            OwnedPayload.Empty),
            cancellationToken);
    }

    private async Task SendOwnedFrameAsync(
        FrameType frameType,
        uint sessionId,
        ulong sequence,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var ownedPayload = OwnedPayload.Create(payload);
        await SendFrameAsync(
            new OutboundFrame(
                new ProtocolFrame(frameType, sessionId, sequence, ownedPayload.AsMemory()),
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                ownedPayload),
            cancellationToken);
    }

    private async Task SendFrameAsync(OutboundFrame outboundFrame, CancellationToken cancellationToken)
    {
        var outboundFrames = _outboundFrames;
        if (outboundFrames is null || _connectionCts is null || _connectionCts.IsCancellationRequested)
        {
            outboundFrame.Dispose();
            throw new InvalidOperationException("Tunnel is not connected.");
        }

        if (!outboundFrames.Writer.TryWrite(outboundFrame))
        {
            outboundFrame.Dispose();
            throw new IOException("Failed to enqueue outbound frame.");
        }

        await outboundFrame.Completion.Task.WaitAsync(cancellationToken);
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
                pending.Dispose();
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
            StructuredLog.Error("tunnel-client", "TUNNEL/TCP", "connection fault; preserving sessions", error);
            DisposeAllIncomingReverseHttpSessions(error);
            foreach (var (sessionId, state) in _sessions.ToArray())
            {
                if (!state.IsResumable)
                {
                    state.MarkClosed();
                    state.FailConnect(error);
                    state.ReaderWriter?.Writer.TryComplete(error);
                    DisposePayloadChannel(state.ReaderWriter);
                    state.Dispose();
                    _sessions.TryRemove(sessionId, out _);
                    continue;
                }

                state.NotifyConnectionFault(error);
            }
            return;
        }

        if (_connectionError is not null)
        {
            StructuredLog.Error("tunnel-client", "TUNNEL/TCP", "connection closed; dropping all sessions", _connectionError);
        }
        else
        {
            StructuredLog.Warn("tunnel-client", "TUNNEL/TCP", "connection closed; dropping all sessions");
        }

        DisposeAllIncomingReverseHttpSessions(_connectionError);
        foreach (var (sessionId, state) in _sessions.ToArray())
        {
            state.MarkClosed();
            state.ReaderWriter?.Writer.TryComplete();
            state.UdpReaderWriter?.Writer.TryComplete();
            DisposePayloadChannel(state.ReaderWriter);
            if (_connectionError is not null)
            {
                state.FailConnect(_connectionError);
            }
            else
            {
                state.FailConnect(new IOException("Tunnel connection closed."));
            }

            state.Dispose();
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
