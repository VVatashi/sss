using System.Threading.Channels;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer
{
    private async Task<byte> ConnectSessionAsync(
        uint sessionId,
        SessionState state,
        CancellationToken cancellationToken,
        bool preservePendingFrames = false)
    {
        state.BeginConnectAttempt(sessionId, preservePendingFrames);
        var frameType = state.Kind switch
        {
            SessionKind.Udp => FrameType.UdpAssociate,
            SessionKind.Http => FrameType.HttpRequest,
            _ => FrameType.Connect
        };
        var payload = state.Kind switch
        {
            SessionKind.Udp => Array.Empty<byte>(),
            SessionKind.Http => ProtocolPayloadSerializer.SerializeHttpRequestStart(
                state.HttpRequestStart ?? throw new InvalidOperationException("HTTP session does not have request metadata.")),
            _ => ProtocolPayloadSerializer.SerializeConnectRequest(
                state.ConnectRequest ?? throw new InvalidOperationException("TCP session does not have CONNECT payload."))
        };
        try
        {
            await SendFrameAsync(new ProtocolFrame(frameType, sessionId, 0, payload), cancellationToken);
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

            if (!state.IsResumable)
            {
                state.MarkClosed();
                state.FailConnect(new IOException("HTTP session cannot be resumed after reconnect."));
                state.ReaderWriter?.Writer.TryComplete(new IOException("HTTP session cannot be resumed after reconnect."));
                _sessions.TryRemove(sessionId, out _);
                DisposePayloadChannel(state.ReaderWriter);
                state.Dispose();
                continue;
            }

            byte replyCode;
            try
            {
                replyCode = await ConnectSessionAsync(sessionId, state, cancellationToken, preservePendingFrames: true);
            }
            catch (Exception ex)
            {
                // Keep sessions alive across transient reconnect failures:
                // EnsureConnected() will retry with a fresh tunnel connection.
                state.NotifyConnectionFault(ex);
                throw;
            }

            if (replyCode != 0x00)
            {
                state.MarkClosed();
                state.FailConnect(new IOException($"Session resume failed with remote reply code {replyCode}."));
                state.ReaderWriter?.Writer.TryComplete(
                    new IOException($"Session resume failed with remote reply code {replyCode}."));
                state.UdpReaderWriter?.Writer.TryComplete(
                    new IOException($"Session resume failed with remote reply code {replyCode}."));
                _sessions.TryRemove(sessionId, out _);
                DisposePayloadChannel(state.ReaderWriter);
                state.Dispose();
                continue;
            }

            await ReplayPendingSessionFramesAsync(sessionId, state, cancellationToken);
        }
    }

    private Task SendSessionFrameAsync(uint sessionId, FrameType frameType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            throw new InvalidOperationException($"Session {sessionId} is not active.");
        }

        return SendSessionFrameAsync(sessionId, state, frameType, payload, cancellationToken);
    }

    private async Task SendSessionFrameAsync(
        uint sessionId,
        SessionState state,
        FrameType frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var pendingFrame = state.CreateTrackedOutboundFrame(sessionId, frameType, payload);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureConnectedAsync(cancellationToken);
            await state.WaitUntilActiveAsync(cancellationToken);

            if (!_sessions.ContainsKey(sessionId) || state.IsClosed)
            {
                throw new InvalidOperationException($"Session {sessionId} is not active.");
            }

            try
            {
                await SendFrameAsync(pendingFrame.ToProtocolFrame(sessionId), cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (!_disposing)
            {
                continue;
            }
        }
    }

    private async Task SendPendingSessionFrameAsync(
        uint sessionId,
        SessionState state,
        FrameType frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);

        if (!_sessions.ContainsKey(sessionId) || state.IsClosed)
        {
            throw new InvalidOperationException($"Session {sessionId} is not active.");
        }

        var pendingFrame = state.CreateTrackedOutboundFrame(sessionId, frameType, payload);
        await SendFrameAsync(pendingFrame.ToProtocolFrame(sessionId), cancellationToken);
    }

    private async Task ReplayPendingSessionFramesAsync(uint sessionId, SessionState state, CancellationToken cancellationToken)
    {
        foreach (var frame in state.SnapshotPendingOutgoingFrames())
        {
            if (!_sessions.ContainsKey(sessionId) || state.IsClosed)
            {
                return;
            }

            await SendFrameAsync(frame.ToProtocolFrame(sessionId), cancellationToken);
        }
    }

    private async Task ReplayPendingSessionFramesFromAsync(
        uint sessionId,
        SessionState state,
        ulong fromSequence,
        CancellationToken cancellationToken)
    {
        foreach (var frame in state.SnapshotPendingOutgoingFramesFrom(fromSequence))
        {
            if (!_sessions.ContainsKey(sessionId) || state.IsClosed)
            {
                return;
            }

            await SendFrameAsync(frame.ToProtocolFrame(sessionId), cancellationToken);
        }
    }

    private Task SendAckFrameAsync(uint sessionId, ulong sequence, CancellationToken cancellationToken)
    {
        return SendFrameAsync(new ProtocolFrame(FrameType.Ack, sessionId, sequence, Array.Empty<byte>()), cancellationToken);
    }

    private Task SendRecoverFrameAsync(uint sessionId, ulong sequence, CancellationToken cancellationToken)
    {
        return SendFrameAsync(new ProtocolFrame(FrameType.Recover, sessionId, sequence, Array.Empty<byte>()), cancellationToken);
    }
}
