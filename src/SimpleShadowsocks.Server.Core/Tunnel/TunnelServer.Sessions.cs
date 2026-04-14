using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed partial class TunnelServer
{
    private enum IncomingSequenceResult
    {
        Accepted = 0,
        Duplicate = 1,
        Gap = 2
    }

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

    private static Task SendHttpResponseStartAsync(
        Stream stream,
        uint sessionId,
        HttpResponseStart response,
        SemaphoreSlim writeLock,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        return SendFrameLockedAsync(
            stream,
            new ProtocolFrame(
                FrameType.HttpResponse,
                sessionId,
                0,
                ProtocolPayloadSerializer.SerializeHttpResponseStart(response)),
            writeLock,
            writeOptions,
            cancellationToken);
    }

    private static Task SendAckAsync(
        Stream stream,
        uint sessionId,
        ulong sequence,
        SemaphoreSlim writeLock,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        return SendFrameLockedAsync(
            stream,
            new ProtocolFrame(FrameType.Ack, sessionId, sequence, Array.Empty<byte>()),
            writeLock,
            writeOptions,
            cancellationToken);
    }

    private static Task SendRecoverAsync(
        Stream stream,
        uint sessionId,
        ulong sequence,
        SemaphoreSlim writeLock,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        return SendFrameLockedAsync(
            stream,
            new ProtocolFrame(FrameType.Recover, sessionId, sequence, Array.Empty<byte>()),
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
        StructuredLog.Info(
            "tunnel-server",
            context is UdpSessionContext ? "TUNNEL/UDP" : context is HttpSessionContext ? "TUNNEL/HTTP" : "TUNNEL/TCP",
            "session disposed",
            sessionId);

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
        private readonly SortedDictionary<ulong, PendingOutboundFrame> _pendingOutgoingFrames = new();
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
                DisposePendingOutgoingFramesLocked();
                _nextExpectedIncomingSequence = 1;
                _nextSendSequence = 1;
            }
        }

        public IncomingSequenceResult EvaluateIncomingSequence(ulong sequence)
        {
            lock (_sequenceLock)
            {
                if (sequence == _nextExpectedIncomingSequence)
                {
                    _nextExpectedIncomingSequence++;
                    return IncomingSequenceResult.Accepted;
                }

                return sequence < _nextExpectedIncomingSequence
                    ? IncomingSequenceResult.Duplicate
                    : IncomingSequenceResult.Gap;
            }
        }

        public ulong TakeNextSendSequence()
        {
            lock (_sequenceLock)
            {
                return _nextSendSequence++;
            }
        }

        public ProtocolFrame CreateTrackedOutboundFrame(FrameType frameType, ReadOnlyMemory<byte> payload)
        {
            lock (_sequenceLock)
            {
                var pendingFrame = new PendingOutboundFrame(frameType, _nextSendSequence++, OwnedPayload.Create(payload));
                _pendingOutgoingFrames[pendingFrame.Sequence] = pendingFrame;
                return pendingFrame.ToProtocolFrame(SessionId);
            }
        }

        public void AcknowledgeOutgoingThrough(ulong sequence)
        {
            lock (_sequenceLock)
            {
                if (sequence == 0 || _pendingOutgoingFrames.Count == 0)
                {
                    return;
                }

                while (_pendingOutgoingFrames.Count > 0)
                {
                    var first = _pendingOutgoingFrames.First();
                    if (first.Key > sequence)
                    {
                        break;
                    }

                    first.Value.Dispose();
                    _pendingOutgoingFrames.Remove(first.Key);
                }
            }
        }

        public IReadOnlyList<ProtocolFrame> SnapshotPendingOutgoingFramesFrom(ulong sequence)
        {
            lock (_sequenceLock)
            {
                return _pendingOutgoingFrames
                    .Where(pair => pair.Key >= sequence)
                    .Select(pair => pair.Value.ToProtocolFrame(SessionId))
                    .ToArray();
            }
        }

        public ulong LastAcceptedIncomingSequence
        {
            get
            {
                lock (_sequenceLock)
                {
                    return _nextExpectedIncomingSequence == 0 ? 0 : _nextExpectedIncomingSequence - 1;
                }
            }
        }

        protected void DisposeTrackedOutgoingFrames()
        {
            lock (_sequenceLock)
            {
                DisposePendingOutgoingFramesLocked();
            }
        }

        private void DisposePendingOutgoingFramesLocked()
        {
            foreach (var frame in _pendingOutgoingFrames.Values)
            {
                frame.Dispose();
            }

            _pendingOutgoingFrames.Clear();
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
            DisposeTrackedOutgoingFrames();
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
            DisposeTrackedOutgoingFrames();
            try { UpstreamClient.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
        }
    }

    private sealed class HttpSessionContext : SessionContext
    {
        private int _requestCompleted;
        private int _responseStarted;

        public HttpSessionContext(
            uint sessionId,
            HttpRequestStart requestStart,
            CancellationTokenSource cancellation)
            : base(sessionId, cancellation)
        {
            RequestStart = requestStart;
            RequestBody = new MemoryStream();
        }

        public HttpRequestStart RequestStart { get; }
        public MemoryStream RequestBody { get; }

        public bool TryMarkRequestCompleted()
        {
            return Interlocked.CompareExchange(ref _requestCompleted, 1, 0) == 0;
        }

        public bool HasCompletedRequest => Volatile.Read(ref _requestCompleted) == 1;

        public bool TryStartResponse()
        {
            return Interlocked.CompareExchange(ref _responseStarted, 1, 0) == 0;
        }

        public override void Dispose()
        {
            DisposeTrackedOutgoingFrames();
            try { RequestBody.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
        }
    }

    private sealed class PendingOutboundFrame : IDisposable
    {
        public PendingOutboundFrame(FrameType type, ulong sequence, OwnedPayload payload)
        {
            Type = type;
            Sequence = sequence;
            Payload = payload;
        }

        public FrameType Type { get; }
        public ulong Sequence { get; }
        public OwnedPayload Payload { get; }

        public ProtocolFrame ToProtocolFrame(uint sessionId)
        {
            return new ProtocolFrame(Type, sessionId, Sequence, Payload.AsMemory());
        }

        public void Dispose()
        {
            Payload.Dispose();
        }
    }

    private readonly struct OwnedPayload : IDisposable
    {
        private readonly byte[] _buffer;
        private readonly int _length;
        private readonly bool _pooled;

        private OwnedPayload(byte[] buffer, int length, bool pooled)
        {
            _buffer = buffer;
            _length = length;
            _pooled = pooled;
        }

        public static OwnedPayload Create(ReadOnlyMemory<byte> payload)
        {
            if (payload.IsEmpty)
            {
                return new OwnedPayload(Array.Empty<byte>(), 0, pooled: false);
            }

            var rented = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.Span.CopyTo(rented.AsSpan(0, payload.Length));
            return new OwnedPayload(rented, payload.Length, pooled: true);
        }

        public ReadOnlyMemory<byte> AsMemory()
        {
            return _buffer.AsMemory(0, _length);
        }

        public void Dispose()
        {
            if (_pooled)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }
    }
}
