using System.Collections.Concurrent;
using System.Net.Sockets;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed partial class TunnelServer
{
    private static Task SendConnectReplyAsync(
        Stream stream,
        uint sessionId,
        byte replyCode,
        SemaphoreSlim writeLock,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        return SendFrameLockedAsync(
            stream,
            new ProtocolFrame(FrameType.Connect, sessionId, 0, new[] { replyCode }),
            writeLock,
            writeOptions,
            cancellationToken);
    }

    private static async Task CloseSessionAsync(
        ConcurrentDictionary<uint, SessionContext> sessions,
        uint sessionId,
        bool sendCloseToClient,
        Stream secureStream,
        SemaphoreSlim writeLock,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryRemove(sessionId, out var context))
        {
            return;
        }

        context.Cancellation.Cancel();
        context.Dispose();

        if (sendCloseToClient)
        {
            try
            {
                await SendFrameLockedAsync(
                    secureStream,
                    new ProtocolFrame(FrameType.Close, sessionId, context.TakeNextSendSequence(), ProtocolPayloadSerializer.SerializeClose(0x00)),
                    writeLock,
                    writeOptions,
                    cancellationToken);
            }
            catch
            {
            }
        }
    }

    private static async Task SendFrameLockedAsync(
        Stream stream,
        ProtocolFrame frame,
        SemaphoreSlim writeLock,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await ProtocolFrameCodec.WriteAsync(stream, frame, cancellationToken, writeOptions);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private sealed class SessionContext : IDisposable
    {
        private readonly object _sequenceLock = new();
        private ulong _nextSendSequence;
        private ulong _nextExpectedIncomingSequence;

        public SessionContext(uint sessionId, TcpClient upstreamClient, NetworkStream upstreamStream, CancellationTokenSource cancellation)
        {
            SessionId = sessionId;
            UpstreamClient = upstreamClient;
            UpstreamStream = upstreamStream;
            Cancellation = cancellation;
        }

        public uint SessionId { get; }
        public TcpClient UpstreamClient { get; }
        public NetworkStream UpstreamStream { get; }
        public CancellationTokenSource Cancellation { get; }
        public Task? UpstreamToClientTask { get; set; }

        public void MarkConnectReceived()
        {
            lock (_sequenceLock)
            {
                _nextExpectedIncomingSequence = 1;
                _nextSendSequence = 1;
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

        public ulong TakeNextSendSequence()
        {
            lock (_sequenceLock)
            {
                return _nextSendSequence++;
            }
        }

        public void Dispose()
        {
            try { UpstreamStream.Dispose(); } catch { }
            try { UpstreamClient.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
        }
    }
}
