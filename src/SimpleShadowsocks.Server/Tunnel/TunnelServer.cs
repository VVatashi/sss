using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed class TunnelServerPolicy
{
    public static TunnelServerPolicy Default { get; } = new();

    public int MaxConcurrentTunnels { get; init; } = 1024;
    public int MaxSessionsPerTunnel { get; init; } = 1024;
    public int ConnectTimeoutMs { get; init; } = 10_000;
}

public sealed class TunnelServer
{
    private readonly TcpListener _listener;
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelServerPolicy _serverPolicy;
    private int _acceptedTunnelConnections;
    private int _activeTunnelConnections;

    public int AcceptedTunnelConnections => Volatile.Read(ref _acceptedTunnelConnections);

    public TunnelServer(IPAddress listenAddress, int port)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes("dev-shared-key");
        _cryptoPolicy = TunnelCryptoPolicy.Default;
        _serverPolicy = TunnelServerPolicy.Default;
        ValidatePolicy(_serverPolicy);
    }

    public TunnelServer(
        IPAddress listenAddress,
        int port,
        string sharedKey,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TunnelServerPolicy? serverPolicy = null)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
        _cryptoPolicy = cryptoPolicy ?? TunnelCryptoPolicy.Default;
        _serverPolicy = serverPolicy ?? TunnelServerPolicy.Default;
        ValidatePolicy(_serverPolicy);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tunnelClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                if (!TryAcquireTunnelSlot())
                {
                    tunnelClient.Dispose();
                    continue;
                }

                Interlocked.Increment(ref _acceptedTunnelConnections);
                _ = Task.Run(
                    () => HandleTunnelSafelyAsync(tunnelClient, cancellationToken),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleTunnelSafelyAsync(TcpClient tunnelClient, CancellationToken cancellationToken)
    {
        using (tunnelClient)
        {
            try
            {
                await HandleTunnelAsync(tunnelClient, _sharedKey, _cryptoPolicy, _serverPolicy, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[tunnel] client failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeTunnelConnections);
            }
        }
    }

    private static async Task HandleTunnelAsync(
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

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameResult = await ProtocolFrameCodec.ReadDetailedAsync(secureStream, cancellationToken);
                if (frameResult is null)
                {
                    break;
                }
                var frame = frameResult.Value.Frame;

                if (connectionVersion is null)
                {
                    connectionVersion = frameResult.Value.Version;
                    connectionCompressionEnabled = (frameResult.Value.Flags & ProtocolFlags.CompressionEnabled) != 0;
                    if (connectionCompressionEnabled)
                    {
                        connectionCompressionAlgorithm = ProtocolFrameCodec.GetCompressionAlgorithm(frameResult.Value.Flags);
                    }
                }
                else if (connectionVersion.Value != frameResult.Value.Version)
                {
                    throw new InvalidDataException(
                        $"Protocol version changed in one tunnel: {connectionVersion.Value} -> {frameResult.Value.Version}.");
                }

                var writeOptions = new ProtocolWriteOptions
                {
                    Version = connectionVersion.Value,
                    EnableCompression = connectionVersion.Value == ProtocolConstants.Version && connectionCompressionEnabled,
                    CompressionAlgorithm = connectionCompressionAlgorithm
                };

                switch (frame.Type)
                {
                    case FrameType.Connect:
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
                        break;

                    case FrameType.Data:
                        if (sessions.TryGetValue(frame.SessionId, out var session))
                        {
                            if (!session.TryAcceptIncomingSequence(frame.Sequence))
                            {
                                await CloseSessionAsync(
                                    sessions,
                                    frame.SessionId,
                                    sendCloseToClient: true,
                                    secureStream,
                                    writeLock,
                                    writeOptions,
                                    CancellationToken.None);
                                break;
                            }

                            if (!frame.Payload.IsEmpty)
                            {
                                await session.UpstreamStream.WriteAsync(frame.Payload, cancellationToken);
                                await session.UpstreamStream.FlushAsync(cancellationToken);
                            }
                        }
                        break;

                    case FrameType.Close:
                        if (sessions.TryGetValue(frame.SessionId, out var closingSession)
                            && !closingSession.TryAcceptIncomingSequence(frame.Sequence))
                        {
                            await CloseSessionAsync(
                                sessions,
                                frame.SessionId,
                                sendCloseToClient: true,
                                secureStream,
                                writeLock,
                                writeOptions,
                                CancellationToken.None);
                            break;
                        }

                        await CloseSessionAsync(
                            sessions,
                            frame.SessionId,
                            sendCloseToClient: false,
                            secureStream,
                            writeLock,
                            writeOptions,
                            cancellationToken);
                        break;

                    case FrameType.Ping:
                        if (sessions.TryGetValue(frame.SessionId, out var pingSession)
                            && !pingSession.TryAcceptIncomingSequence(frame.Sequence))
                        {
                            await CloseSessionAsync(
                                sessions,
                                frame.SessionId,
                                sendCloseToClient: true,
                                secureStream,
                                writeLock,
                                writeOptions,
                                CancellationToken.None);
                            break;
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
                        break;
                }
            }
        }
        finally
        {
            var closeWriteOptions = new ProtocolWriteOptions
            {
                Version = connectionVersion ?? ProtocolConstants.LegacyVersion,
                EnableCompression = connectionVersion == ProtocolConstants.Version && connectionCompressionEnabled,
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

    private static async Task HandleConnectFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        if (!pendingConnectSessions.TryAdd(frame.SessionId, 0))
        {
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
            return;
        }

        try
        {
        if (sessions.Count >= serverPolicy.MaxSessionsPerTunnel)
        {
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
            return;
        }

        if (sessions.ContainsKey(frame.SessionId))
        {
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
            return;
        }

        ConnectRequest request;
        try
        {
            if (frame.Sequence != 0)
            {
                await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                return;
            }

            request = ProtocolPayloadSerializer.DeserializeConnectRequest(frame.Payload.Span);
        }
        catch
        {
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x08, writeLock, writeOptions, cancellationToken);
            return;
        }

        var upstreamClient = new TcpClient();
        try
        {
            await ConnectUpstreamAsync(upstreamClient, request, serverPolicy.ConnectTimeoutMs, cancellationToken);
        }
        catch (Exception ex)
        {
            upstreamClient.Dispose();
            Console.WriteLine($"[tunnel] connect failed {request.Address}:{request.Port} ({ex.Message})");
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x05, writeLock, writeOptions, cancellationToken);
            return;
        }

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new SessionContext(frame.SessionId, upstreamClient, upstreamClient.GetStream(), sessionCts);
        context.MarkConnectReceived();
        if (!sessions.TryAdd(frame.SessionId, context))
        {
            context.Dispose();
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
            return;
        }

        context.UpstreamToClientTask = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!context.Cancellation.Token.IsCancellationRequested)
                {
                    var read = await context.UpstreamStream.ReadAsync(buffer, context.Cancellation.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    var sequence = context.TakeNextSendSequence();
                    await SendFrameLockedAsync(
                        secureStream,
                        new ProtocolFrame(FrameType.Data, context.SessionId, sequence, buffer.AsMemory(0, read)),
                        writeLock,
                        writeOptions,
                        context.Cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await CloseSessionAsync(
                    sessions,
                    context.SessionId,
                    sendCloseToClient: true,
                    secureStream,
                    writeLock,
                    writeOptions,
                    CancellationToken.None);
            }
        }, context.Cancellation.Token);

        Console.WriteLine($"[tunnel] proxy {request.Address}:{request.Port} session={frame.SessionId}");
        await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x00, writeLock, writeOptions, cancellationToken);
        }
        finally
        {
            pendingConnectSessions.TryRemove(frame.SessionId, out _);
        }
    }

    private static async Task HandleConnectFrameSafelyAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleConnectFrameAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            try
            {
                await SendConnectReplyAsync(
                    secureStream,
                    frame.SessionId,
                    replyCode: 0x05,
                    writeLock,
                    writeOptions,
                    CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static async Task ConnectUpstreamAsync(
        TcpClient upstream,
        ConnectRequest request,
        int connectTimeoutMs,
        CancellationToken cancellationToken)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(connectTimeoutMs);

        try
        {
            if (request.AddressType is AddressType.IPv4 or AddressType.IPv6)
            {
                var ipAddress = IPAddress.Parse(request.Address);
                await upstream.ConnectAsync(ipAddress, request.Port, connectCts.Token);
                return;
            }

            await upstream.ConnectAsync(request.Address, request.Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connect timeout after {connectTimeoutMs} ms.");
        }
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

    private static void ValidatePolicy(TunnelServerPolicy policy)
    {
        if (policy.MaxConcurrentTunnels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.MaxConcurrentTunnels), "MaxConcurrentTunnels must be > 0.");
        }

        if (policy.MaxSessionsPerTunnel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.MaxSessionsPerTunnel), "MaxSessionsPerTunnel must be > 0.");
        }

        if (policy.ConnectTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.ConnectTimeoutMs), "ConnectTimeoutMs must be > 0.");
        }
    }

    private bool TryAcquireTunnelSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeTunnelConnections);
            if (current >= _serverPolicy.MaxConcurrentTunnels)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeTunnelConnections, current + 1, current) == current)
            {
                return true;
            }
        }
    }
}
