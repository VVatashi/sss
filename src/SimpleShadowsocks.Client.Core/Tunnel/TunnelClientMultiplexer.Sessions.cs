using System.Runtime.InteropServices;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer
{
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
                continue;
            }
        }
    }
}
