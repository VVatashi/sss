using System.Collections.Concurrent;
using System.Net;
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

    private static Task SendUdpAssociateReplyAsync(
        Stream stream,
        uint sessionId,
        byte replyCode,
        SemaphoreSlim writeLock,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        return SendFrameLockedAsync(
            stream,
            new ProtocolFrame(FrameType.UdpAssociate, sessionId, 0, new[] { replyCode }),
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

    private abstract class SessionContext : IDisposable
    {
        private readonly object _sequenceLock = new();
        private ulong _nextSendSequence;
        private ulong _nextExpectedIncomingSequence;

        protected SessionContext(uint sessionId, CancellationTokenSource cancellation)
        {
            SessionId = sessionId;
            Cancellation = cancellation;
        }

        public uint SessionId { get; }
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

        public abstract void Dispose();
    }

    private sealed class TcpSessionContext : SessionContext
    {
        public TcpSessionContext(uint sessionId, TcpClient upstreamClient, NetworkStream upstreamStream, CancellationTokenSource cancellation)
            : base(sessionId, cancellation)
        {
            UpstreamClient = upstreamClient;
            UpstreamStream = upstreamStream;
        }

        public TcpClient UpstreamClient { get; }
        public NetworkStream UpstreamStream { get; }

        public override void Dispose()
        {
            try { UpstreamStream.Dispose(); } catch { }
            try { UpstreamClient.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
        }
    }

    private sealed class UdpSessionContext : SessionContext
    {
        public UdpSessionContext(uint sessionId, UdpClient upstreamClient, CancellationTokenSource cancellation)
            : base(sessionId, cancellation)
        {
            UpstreamClient = upstreamClient;
            LastRemoteEndPoint = null;
        }

        public UdpClient UpstreamClient { get; }
        public IPEndPoint? LastRemoteEndPoint { get; set; }

        public override void Dispose()
        {
            try { UpstreamClient.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
        }
    }
}
