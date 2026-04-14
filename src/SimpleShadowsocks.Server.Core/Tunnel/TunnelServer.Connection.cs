using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed partial class TunnelServer
{
    private const int RelayChunkSize = 64 * 1024;

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

    public async Task<(uint SessionId, HttpResponseStart Response, ChannelReader<byte[]> Reader, Func<Task> CloseAsync)> ExecuteReverseHttpRequestAsync(
        HttpRequestStart requestStart,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var connections = _activeTunnelConnections.Values.ToArray();
        if (connections.Length == 0)
        {
            throw new InvalidOperationException("No active client tunnels are available for HTTP reverse proxy.");
        }

        Exception? lastError = null;
        foreach (var connection in connections)
        {
            try
            {
                var (sessionId, response, reader) = await connection.ExecuteReverseHttpRequestAsync(requestStart, body, cancellationToken);
                return (sessionId, response, reader, () => connection.CloseReverseHttpSessionAsync(sessionId, 0x00, CancellationToken.None));
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new IOException("Unable to execute reverse HTTP request over any active tunnel.", lastError);
    }

    private async Task HandleTunnelAsync(
        TcpClient tunnelClient,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        using var tunnelStream = tunnelClient.GetStream();
        await using var secureStream = await TunnelCryptoHandshake.AsServerAsync(
            tunnelStream,
            sharedKey,
            cryptoPolicy,
            cancellationToken);

        using var writeLock = new SemaphoreSlim(1, 1);
        var sessions = new ConcurrentDictionary<uint, SessionContext>();
        var pendingConnectSessions = new ConcurrentDictionary<uint, byte>();
        byte? connectionVersion = null;
        var connectionCompressionEnabled = false;
        var connectionCompressionAlgorithm = PayloadCompressionAlgorithm.Deflate;
        var connectionId = Interlocked.Increment(ref _nextTunnelConnectionId);
        var activeConnection = new ActiveTunnelConnection(connectionId, secureStream, writeLock);
        _activeTunnelConnections[connectionId] = activeConnection;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var leasedFrameResult = await ProtocolFrameCodec.ReadDetailedLeasedAsync(secureStream, cancellationToken);
                if (leasedFrameResult is null)
                {
                    break;
                }
                using var leasedFrame = leasedFrameResult.Value;
                var frame = leasedFrame.Frame;

                if (connectionVersion is null)
                {
                    connectionVersion = leasedFrame.Version;
                    connectionCompressionEnabled = (leasedFrame.Flags & ProtocolFlags.CompressionEnabled) != 0;
                    if (connectionCompressionEnabled)
                    {
                        connectionCompressionAlgorithm = ProtocolFrameCodec.GetCompressionAlgorithm(leasedFrame.Flags);
                    }
                }
                else if (connectionVersion.Value != leasedFrame.Version)
                {
                    throw new InvalidDataException(
                        $"Protocol version changed in one tunnel: {connectionVersion.Value} -> {leasedFrame.Version}.");
                }

                var writeOptions = new ProtocolWriteOptions
                {
                    Version = connectionVersion.Value,
                    EnableCompression = ProtocolConstants.SupportsCompression(connectionVersion.Value) && connectionCompressionEnabled,
                    CompressionAlgorithm = connectionCompressionAlgorithm
                };
                activeConnection.SetWriteOptions(writeOptions);

                if (activeConnection.HasReverseHttpSession(frame.SessionId)
                    && await TryHandleReverseHttpFrameAsync(activeConnection, leasedFrame.Materialize().Frame, cancellationToken))
                {
                    continue;
                }

                if (frame.Type is FrameType.Connect or FrameType.UdpAssociate or FrameType.HttpRequest)
                {
                    var materializedFrame = leasedFrame.Materialize().Frame;
                    await HandleFrameAsync(
                        secureStream,
                        writeLock,
                        sessions,
                        pendingConnectSessions,
                        materializedFrame,
                        writeOptions,
                        serverPolicy,
                        cancellationToken);
                    continue;
                }

                await HandleFrameAsync(
                    secureStream,
                    writeLock,
                    sessions,
                    pendingConnectSessions,
                    frame,
                    writeOptions,
                    serverPolicy,
                    cancellationToken);
            }
        }
        finally
        {
            _activeTunnelConnections.TryRemove(connectionId, out _);
            activeConnection.FailAllReverseHttpSessions(_connectionClosedException);

            var closeWriteOptions = new ProtocolWriteOptions
            {
                Version = connectionVersion ?? ProtocolConstants.LegacyVersion,
                EnableCompression = connectionVersion.HasValue
                    && ProtocolConstants.SupportsCompression(connectionVersion.Value)
                    && connectionCompressionEnabled,
                CompressionAlgorithm = connectionCompressionAlgorithm
            };

            foreach (var sessionId in sessions.Keys)
            {
                await CloseSessionAsync(
                    sessions,
                    sessionId,
                    sendCloseToClient: false,
                    secureStream,
                    writeLock,
                    closeWriteOptions,
                    CancellationToken.None);
            }
        }
    }

    private static readonly IOException _connectionClosedException = new("Tunnel connection closed.");

    private async Task<bool> TryHandleReverseHttpFrameAsync(
        ActiveTunnelConnection connection,
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        if (!connection.TryGetReverseHttpSession(frame.SessionId, out var session))
        {
            return false;
        }

        switch (frame.Type)
        {
            case FrameType.ReverseHttpResponse:
                if (!session.TryAcceptIncomingFrame(frame.Type, frame.Sequence))
                {
                    connection.FailReverseHttpSession(frame.SessionId, new IOException("Invalid reverse HTTP response sequence."));
                    return true;
                }

                try
                {
                    session.CompleteResponse(ProtocolPayloadSerializer.DeserializeHttpResponseStart(frame.Payload.Span));
                }
                catch (Exception ex)
                {
                    connection.FailReverseHttpSession(frame.SessionId, ex);
                }

                return true;

            case FrameType.Data:
                if (!session.TryAcceptIncomingFrame(frame.Type, frame.Sequence))
                {
                    connection.FailReverseHttpSession(frame.SessionId, new IOException("Invalid reverse HTTP data sequence."));
                    return true;
                }

                if (!frame.Payload.IsEmpty)
                {
                    await session.ReaderWriter.Writer.WriteAsync(DetachPayload(frame.Payload), cancellationToken);
                }

                return true;

            case FrameType.Close:
                if (!session.TryAcceptIncomingFrame(frame.Type, frame.Sequence))
                {
                    connection.FailReverseHttpSession(frame.SessionId, new IOException("Invalid reverse HTTP close sequence."));
                    return true;
                }

                session.MarkClosed();
                session.ReaderWriter.Writer.TryComplete();
                connection.RemoveReverseHttpSession(frame.SessionId);
                StructuredLog.Info("tunnel-server", "TUNNEL/HTTP", "reverse session closed by client", frame.SessionId);
                return true;

            default:
                return false;
        }
    }

    private static Task HandleFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        return frame.Type switch
        {
            FrameType.Connect => HandleConnectFrameDispatchAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            FrameType.UdpAssociate => HandleUdpAssociateFrameDispatchAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            FrameType.HttpRequest => HandleHttpRequestFrameAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.HttpRequestEnd => HandleHttpRequestEndFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.Data => HandleDataFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.UdpData => HandleUdpDataFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.Ack => HandleAckFrameAsync(
                sessions,
                frame,
                cancellationToken),
            FrameType.Recover => HandleRecoverFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.Close => HandleCloseFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.Ping => HandlePingFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private static Task HandleConnectFrameDispatchAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () => HandleConnectFrameSafelyAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            cancellationToken);

        return Task.CompletedTask;
    }

    private static Task HandleUdpAssociateFrameDispatchAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () => HandleUdpAssociateFrameSafelyAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            cancellationToken);

        return Task.CompletedTask;
    }

    private static async Task HandleDataFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(frame.SessionId, out var session))
        {
            return;
        }

        if (session is not TcpSessionContext tcpSession)
        {
            if (session is HttpSessionContext httpSession)
            {
                var sequenceResult = session.EvaluateIncomingSequence(frame.Sequence);
                if (sequenceResult != IncomingSequenceResult.Accepted || httpSession.HasCompletedRequest)
                {
                    await HandleIncomingSequenceMismatchAsync(
                        secureStream,
                        writeLock,
                        sessions,
                        session,
                        frame.SessionId,
                        sequenceResult,
                        writeOptions,
                        cancellationToken);
                    return;
                }

                if (!frame.Payload.IsEmpty)
                {
                    await httpSession.RequestBody.WriteAsync(frame.Payload, cancellationToken);
                }

                if (ProtocolConstants.SupportsSelectiveRecovery(writeOptions.Version))
                {
                    await SendAckAsync(
                        secureStream,
                        frame.SessionId,
                        frame.Sequence,
                        writeLock,
                        writeOptions,
                        cancellationToken);
                }

                return;
            }

            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        var incomingSequenceResult = session.EvaluateIncomingSequence(frame.Sequence);
        if (incomingSequenceResult != IncomingSequenceResult.Accepted)
        {
            await HandleIncomingSequenceMismatchAsync(
                secureStream,
                writeLock,
                sessions,
                session,
                frame.SessionId,
                incomingSequenceResult,
                writeOptions,
                cancellationToken);
            return;
        }

        if (!frame.Payload.IsEmpty)
        {
            await tcpSession.UpstreamStream.WriteAsync(frame.Payload, cancellationToken);
            await tcpSession.UpstreamStream.FlushAsync(cancellationToken);
        }

        if (ProtocolConstants.SupportsSelectiveRecovery(writeOptions.Version))
        {
            await SendAckAsync(
                secureStream,
                frame.SessionId,
                frame.Sequence,
                writeLock,
                writeOptions,
                cancellationToken);
        }
    }

    private static async Task HandleUdpDataFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(frame.SessionId, out var session))
        {
            return;
        }

        if (session is not UdpSessionContext udpSession)
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        var incomingSequenceResult = session.EvaluateIncomingSequence(frame.Sequence);
        if (incomingSequenceResult != IncomingSequenceResult.Accepted)
        {
            await HandleIncomingSequenceMismatchAsync(
                secureStream,
                writeLock,
                sessions,
                session,
                frame.SessionId,
                incomingSequenceResult,
                writeOptions,
                cancellationToken);
            return;
        }

        if (frame.Payload.IsEmpty)
        {
            return;
        }

        UdpDatagram datagram;
        IPEndPoint remoteEndPoint;
        try
        {
            datagram = ProtocolPayloadSerializer.DeserializeUdpDatagram(frame.Payload);
            remoteEndPoint = await ResolveRemoteEndPointAsync(datagram, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StructuredLog.Error("tunnel-server", "TUNNEL/UDP", "invalid UDP payload", ex, frame.SessionId);
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        try
        {
            await udpSession.UpstreamClient.SendAsync(datagram.Payload, remoteEndPoint, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StructuredLog.Error("tunnel-server", "TUNNEL/UDP", "upstream UDP send failed", ex, frame.SessionId);
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        if (ProtocolConstants.SupportsSelectiveRecovery(writeOptions.Version))
        {
            await SendAckAsync(
                secureStream,
                frame.SessionId,
                frame.Sequence,
                writeLock,
                writeOptions,
                cancellationToken);
        }
    }

    private static async Task HandleCloseFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (sessions.TryGetValue(frame.SessionId, out var closingSession))
        {
            var incomingSequenceResult = closingSession.EvaluateIncomingSequence(frame.Sequence);
            if (incomingSequenceResult != IncomingSequenceResult.Accepted)
            {
                await HandleIncomingSequenceMismatchAsync(
                    secureStream,
                    writeLock,
                    sessions,
                    closingSession,
                    frame.SessionId,
                    incomingSequenceResult,
                    writeOptions,
                    cancellationToken);
                return;
            }
        }

        await CloseSessionAsync(
            sessions,
            frame.SessionId,
            sendCloseToClient: false,
            secureStream,
            writeLock,
            writeOptions,
            cancellationToken);
    }

    private static async Task HandlePingFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (sessions.TryGetValue(frame.SessionId, out var pingSession)
            && pingSession.EvaluateIncomingSequence(frame.Sequence) is var incomingSequenceResult
            && incomingSequenceResult != IncomingSequenceResult.Accepted)
        {
            await HandleIncomingSequenceMismatchAsync(
                secureStream,
                writeLock,
                sessions,
                pingSession,
                frame.SessionId,
                incomingSequenceResult,
                writeOptions,
                cancellationToken);
            return;
        }

        var pongSequence = sessions.TryGetValue(frame.SessionId, out pingSession)
            ? pingSession.TakeNextSendSequence()
            : 0UL;
        await SendFrameLockedAsync(
            secureStream,
            new ProtocolFrame(FrameType.Pong, frame.SessionId, pongSequence, frame.Payload),
            writeLock,
            writeOptions,
            cancellationToken);

        if (frame.SessionId != 0 && ProtocolConstants.SupportsSelectiveRecovery(writeOptions.Version))
        {
            await SendAckAsync(
                secureStream,
                frame.SessionId,
                frame.Sequence,
                writeLock,
                writeOptions,
                cancellationToken);
        }
    }

    private static Task HandleAckFrameAsync(
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        if (sessions.TryGetValue(frame.SessionId, out var session))
        {
            session.AcknowledgeOutgoingThrough(frame.Sequence);
        }

        return Task.CompletedTask;
    }

    private static async Task HandleRecoverFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(frame.SessionId, out var session))
        {
            return;
        }

        var pendingFrames = session.SnapshotPendingOutgoingFramesFrom(frame.Sequence);
        if (pendingFrames.Count == 0)
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        foreach (var pendingFrame in pendingFrames)
        {
            await SendFrameLockedAsync(
                secureStream,
                pendingFrame,
                writeLock,
                writeOptions,
                cancellationToken);
        }
    }

    private static async Task HandleIncomingSequenceMismatchAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        SessionContext session,
        uint sessionId,
        IncomingSequenceResult incomingSequenceResult,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!ProtocolConstants.SupportsSelectiveRecovery(writeOptions.Version))
        {
            await CloseSessionAsync(
                sessions,
                sessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        switch (incomingSequenceResult)
        {
            case IncomingSequenceResult.Duplicate:
                var acknowledgedSequence = session.LastAcceptedIncomingSequence;
                if (acknowledgedSequence > 0)
                {
                    await SendAckAsync(
                        secureStream,
                        sessionId,
                        acknowledgedSequence,
                        writeLock,
                        writeOptions,
                        cancellationToken);
                }

                return;

            case IncomingSequenceResult.Gap:
                await SendRecoverAsync(
                    secureStream,
                    sessionId,
                    session.LastAcceptedIncomingSequence + 1,
                    writeLock,
                    writeOptions,
                    cancellationToken);
                return;

            default:
                await CloseSessionAsync(
                    sessions,
                    sessionId,
                    sendCloseToClient: true,
                    secureStream,
                    writeLock,
                    writeOptions,
                    CancellationToken.None);
                return;
        }
    }

    private static async Task<IPEndPoint> ResolveRemoteEndPointAsync(UdpDatagram datagram, CancellationToken cancellationToken)
    {
        if (datagram.AddressType is AddressType.IPv4 or AddressType.IPv6)
        {
            return new IPEndPoint(IPAddress.Parse(datagram.Address), datagram.Port);
        }

        var addresses = await Dns.GetHostAddressesAsync(datagram.Address, cancellationToken);
        var selected = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
        if (selected is null)
        {
            throw new InvalidDataException($"Unable to resolve UDP destination '{datagram.Address}'.");
        }

        return new IPEndPoint(selected, datagram.Port);
    }

    private sealed class ActiveTunnelConnection
    {
        private readonly Stream _secureStream;
        private readonly SemaphoreSlim _writeLock;
        private readonly ConcurrentDictionary<uint, ReverseHttpSessionState> _reverseHttpSessions = new();
        private readonly TaskCompletionSource<ProtocolWriteOptions> _writeOptionsReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextReverseSessionId = int.MinValue;
        private ProtocolWriteOptions? _writeOptions;

        public ActiveTunnelConnection(long connectionId, Stream secureStream, SemaphoreSlim writeLock)
        {
            ConnectionId = connectionId;
            _secureStream = secureStream;
            _writeLock = writeLock;
        }

        public long ConnectionId { get; }
        public int ReverseHttpSessionCount => _reverseHttpSessions.Count;

        public bool HasReverseHttpSession(uint sessionId)
        {
            return _reverseHttpSessions.ContainsKey(sessionId);
        }

        public void SetWriteOptions(ProtocolWriteOptions writeOptions)
        {
            _writeOptions = writeOptions;
            _writeOptionsReady.TrySetResult(writeOptions);
        }

        public async Task<(uint SessionId, HttpResponseStart Response, ChannelReader<byte[]> Reader)> ExecuteReverseHttpRequestAsync(
            HttpRequestStart requestStart,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            var writeOptions = await _writeOptionsReady.Task.WaitAsync(cancellationToken);
            if (!ProtocolConstants.SupportsHttpRelay(writeOptions.Version))
            {
                throw new InvalidOperationException($"HTTP reverse proxy requires protocol v{ProtocolConstants.Version}.");
            }

            var sessionId = unchecked((uint)Interlocked.Increment(ref _nextReverseSessionId));
            var state = new ReverseHttpSessionState();
            if (!_reverseHttpSessions.TryAdd(sessionId, state))
            {
                throw new InvalidOperationException("Failed to register reverse HTTP session.");
            }

            try
            {
                await SendFrameLockedAsync(
                    _secureStream,
                    new ProtocolFrame(
                        FrameType.ReverseHttpRequest,
                        sessionId,
                        0,
                        ProtocolPayloadSerializer.SerializeHttpRequestStart(requestStart)),
                    _writeLock,
                    writeOptions,
                    cancellationToken);

                var offset = 0;
                while (offset < body.Length)
                {
                    var length = Math.Min(RelayChunkSize, body.Length - offset);
                    await SendFrameLockedAsync(
                        _secureStream,
                        new ProtocolFrame(
                            FrameType.Data,
                            sessionId,
                            state.TakeNextSendSequence(),
                            body.Slice(offset, length)),
                        _writeLock,
                        writeOptions,
                        cancellationToken);
                    offset += length;
                }

                await SendFrameLockedAsync(
                    _secureStream,
                    new ProtocolFrame(
                        FrameType.ReverseHttpRequestEnd,
                        sessionId,
                        state.TakeNextSendSequence(),
                        Array.Empty<byte>()),
                    _writeLock,
                    writeOptions,
                    cancellationToken);

                var response = await state.WaitForResponseAsync(cancellationToken);
                return (sessionId, response, state.ReaderWriter.Reader);
            }
            catch
            {
                FailReverseHttpSession(sessionId, new IOException("Reverse HTTP session aborted."));
                throw;
            }
        }

        public async Task CloseReverseHttpSessionAsync(uint sessionId, byte reasonCode, CancellationToken cancellationToken)
        {
            if (!_reverseHttpSessions.TryRemove(sessionId, out var state))
            {
                return;
            }

            state.MarkClosed();
            state.ReaderWriter.Writer.TryComplete();
            state.Fail(new IOException("Reverse HTTP session closed."));

            if (_writeOptions is null)
            {
                return;
            }

            try
            {
                await SendFrameLockedAsync(
                    _secureStream,
                    new ProtocolFrame(
                        FrameType.Close,
                        sessionId,
                        state.TakeNextSendSequence(),
                        ProtocolPayloadSerializer.SerializeClose(reasonCode)),
                    _writeLock,
                    _writeOptions,
                    cancellationToken);
            }
            catch
            {
            }
        }

        public bool TryGetReverseHttpSession(uint sessionId, out ReverseHttpSessionState state)
        {
            return _reverseHttpSessions.TryGetValue(sessionId, out state!);
        }

        public void RemoveReverseHttpSession(uint sessionId)
        {
            _reverseHttpSessions.TryRemove(sessionId, out _);
        }

        public void FailReverseHttpSession(uint sessionId, Exception error)
        {
            if (!_reverseHttpSessions.TryRemove(sessionId, out var state))
            {
                return;
            }

            state.MarkClosed();
            state.Fail(error);
            state.ReaderWriter.Writer.TryComplete(error);
        }

        public void FailAllReverseHttpSessions(Exception error)
        {
            foreach (var sessionId in _reverseHttpSessions.Keys.ToArray())
            {
                FailReverseHttpSession(sessionId, error);
            }
        }
    }

    private sealed class ReverseHttpSessionState
    {
        private readonly object _sequenceLock = new();
        private readonly TaskCompletionSource<HttpResponseStart> _pendingResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong _nextSendSequence = 1;
        private ulong _nextExpectedIncomingSequence;
        private bool _awaitingResponse = true;
        private bool _closed;

        public ReverseHttpSessionState()
        {
            ReaderWriter = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public Channel<byte[]> ReaderWriter { get; }

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
                if (_closed)
                {
                    return false;
                }

                if (_awaitingResponse)
                {
                    if (frameType != FrameType.ReverseHttpResponse || sequence != 0)
                    {
                        return false;
                    }

                    _awaitingResponse = false;
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

        public Task<HttpResponseStart> WaitForResponseAsync(CancellationToken cancellationToken)
        {
            return _pendingResponse.Task.WaitAsync(cancellationToken);
        }

        public void CompleteResponse(HttpResponseStart response)
        {
            _pendingResponse.TrySetResult(response);
        }

        public void Fail(Exception error)
        {
            _pendingResponse.TrySetException(error);
        }

        public void MarkClosed()
        {
            lock (_sequenceLock)
            {
                _closed = true;
            }
        }
    }
}
