using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer : IAsyncDisposable
{
    private const int RelayChunkSize = 64 * 1024;

    private readonly Action<Socket>? _configureTcpSocket;
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelConnectionPolicy _connectionPolicy;
    private readonly byte _protocolVersion;
    private readonly ProtocolWriteOptions _writeOptions;
    private readonly ITunnelReverseHttpHandler? _reverseHttpHandler;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, SessionState> _sessions = new();
    private readonly ConcurrentDictionary<uint, IncomingReverseHttpSession> _incomingReverseHttpSessions = new();

    private TcpClient? _tcpClient;
    private Stream? _secureStream;
    private CancellationTokenSource? _connectionCts;
    private Task? _readLoopTask;
    private Task? _writeLoopTask;
    private Task? _heartbeatLoopTask;
    private Channel<OutboundFrame>? _outboundFrames;
    private Exception? _connectionError;
    private int _nextSessionId;
    private int _connectionGeneration;
    private long _lastIncomingUtcTicks;
    private long _controlSendSequence;
    private volatile bool _disposing;

    internal int ActiveHttpSessionCount
    {
        get
        {
            var total = 0;
            foreach (var state in _sessions.Values)
            {
                if (state.IsHttp)
                {
                    total++;
                }
            }

            return total;
        }
    }

    internal int ActiveReverseHttpSessionCount => _incomingReverseHttpSessions.Count;

    public TunnelClientMultiplexer(
        string remoteHost,
        int remotePort,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
        TunnelConnectionPolicy connectionPolicy,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate,
        Action<Socket>? configureTcpSocket = null,
        ITunnelReverseHttpHandler? reverseHttpHandler = null)
    {
        _configureTcpSocket = configureTcpSocket;
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _sharedKey = sharedKey;
        _cryptoPolicy = cryptoPolicy;
        _connectionPolicy = connectionPolicy;
        _protocolVersion = protocolVersion;
        _reverseHttpHandler = reverseHttpHandler;
        _writeOptions = new ProtocolWriteOptions
        {
            Version = protocolVersion,
            EnableCompression = enableCompression,
            CompressionAlgorithm = compressionAlgorithm
        };
        ValidatePolicy(_connectionPolicy);
        TouchIncoming();
    }

    public async Task<(uint SessionId, byte ReplyCode, ChannelReader<OwnedPayloadChunk> Reader)> OpenSessionAsync(
        ConnectRequest connectRequest,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sessions.Count >= _connectionPolicy.MaxConcurrentSessions)
        {
            throw new InvalidOperationException(
                $"Max concurrent sessions limit reached: {_connectionPolicy.MaxConcurrentSessions}.");
        }

        var sessionId = (uint)Interlocked.Increment(ref _nextSessionId);
        var state = new SessionState(connectRequest, _connectionPolicy.SessionReceiveChannelCapacity);
        if (!_sessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException("Failed to register tunnel session.");
        }

        try
        {
            var replyCode = await ConnectSessionAsync(sessionId, state, cancellationToken);
            if (state.ReaderWriter is null)
            {
                throw new InvalidOperationException("TCP session is not initialized.");
            }

            if (replyCode != 0x00)
            {
                _sessions.TryRemove(sessionId, out _);
                state.ReaderWriter.Writer.TryComplete();
                DisposePayloadChannel(state.ReaderWriter);
                state.Dispose();
                StructuredLog.Warn("tunnel-client", "TUNNEL/TCP", $"session open rejected reply={replyCode}", sessionId);
            }
            else
            {
                StructuredLog.Info("tunnel-client", "TUNNEL/TCP", $"session opened target={connectRequest.Address}:{connectRequest.Port}", sessionId);
            }

            return (sessionId, replyCode, state.ReaderWriter.Reader);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            state.ReaderWriter?.Writer.TryComplete();
            DisposePayloadChannel(state.ReaderWriter);
            state.Dispose();
            throw;
        }
    }

    public async Task<(uint SessionId, byte ReplyCode, ChannelReader<UdpDatagram> Reader)> OpenUdpSessionAsync(
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
        if (_sessions.Count >= _connectionPolicy.MaxConcurrentSessions)
        {
            throw new InvalidOperationException(
                $"Max concurrent sessions limit reached: {_connectionPolicy.MaxConcurrentSessions}.");
        }

        var sessionId = (uint)Interlocked.Increment(ref _nextSessionId);
        var state = new SessionState(_connectionPolicy.SessionReceiveChannelCapacity);
        if (!_sessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException("Failed to register tunnel UDP session.");
        }

        try
        {
            var replyCode = await ConnectSessionAsync(sessionId, state, cancellationToken);
            if (replyCode != 0x00)
            {
                _sessions.TryRemove(sessionId, out _);
                state.UdpReaderWriter?.Writer.TryComplete();
                state.Dispose();
                StructuredLog.Warn("tunnel-client", "TUNNEL/UDP", $"session open rejected reply={replyCode}", sessionId);
            }

            if (state.UdpReaderWriter is null)
            {
                throw new InvalidOperationException("UDP session is not initialized.");
            }

            if (replyCode == 0x00)
            {
                StructuredLog.Info("tunnel-client", "TUNNEL/UDP", "session opened", sessionId);
            }

            return (sessionId, replyCode, state.UdpReaderWriter.Reader);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            state.UdpReaderWriter?.Writer.TryComplete();
            state.Dispose();
            throw;
        }
    }

    public async Task<(uint SessionId, HttpResponseStart Response, ChannelReader<OwnedPayloadChunk> Reader)> ExecuteHttpRequestAsync(
        HttpRequestStart requestStart,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (!ProtocolConstants.SupportsHttpRelay(_protocolVersion))
        {
            throw new InvalidOperationException($"HTTP relay requires protocol v{ProtocolConstants.Version}.");
        }

        await EnsureConnectedAsync(cancellationToken);
        if (_sessions.Count >= _connectionPolicy.MaxConcurrentSessions)
        {
            throw new InvalidOperationException(
                $"Max concurrent sessions limit reached: {_connectionPolicy.MaxConcurrentSessions}.");
        }

        var sessionId = (uint)Interlocked.Increment(ref _nextSessionId);
        var state = new SessionState(requestStart, _connectionPolicy.SessionReceiveChannelCapacity);
        if (!_sessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException("Failed to register tunnel HTTP session.");
        }

        try
        {
            state.BeginConnectAttempt(sessionId, preservePendingFrames: false);
            await SendFrameAsync(
                new ProtocolFrame(
                    FrameType.HttpRequest,
                    sessionId,
                    0,
                    ProtocolPayloadSerializer.SerializeHttpRequestStart(requestStart)),
                cancellationToken);

            var offset = 0;
            while (offset < body.Length)
            {
                var length = Math.Min(RelayChunkSize, body.Length - offset);
                await SendPendingSessionFrameAsync(
                    sessionId,
                    state,
                    FrameType.Data,
                    body.Slice(offset, length),
                    cancellationToken);
                offset += length;
            }

            await SendPendingSessionFrameAsync(
                sessionId,
                state,
                FrameType.HttpRequestEnd,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken);

            var response = await state.WaitForHttpResponseAsync(cancellationToken);
            if (state.ReaderWriter is null)
            {
                throw new InvalidOperationException("HTTP session is not initialized.");
            }

            StructuredLog.Info("tunnel-client", "TUNNEL/HTTP", $"{requestStart.Method} {requestStart.Authority}{requestStart.PathAndQuery}", sessionId);
            return (sessionId, response, state.ReaderWriter.Reader);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            state.ReaderWriter?.Writer.TryComplete();
            DisposePayloadChannel(state.ReaderWriter);
            state.Dispose();
            throw;
        }
    }

    public Task SendDataAsync(uint sessionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        return SendSessionFrameAsync(sessionId, FrameType.Data, payload, cancellationToken);
    }

    public Task SendUdpDataAsync(uint sessionId, UdpDatagram datagram, CancellationToken cancellationToken)
    {
        var payload = ProtocolPayloadSerializer.SerializeUdpDatagram(datagram);
        return SendSessionFrameAsync(sessionId, FrameType.UdpData, payload, cancellationToken);
    }

    public async Task CloseSessionAsync(uint sessionId, byte reasonCode, CancellationToken cancellationToken)
    {
        if (_sessions.TryRemove(sessionId, out var state))
        {
            var closeSequence = state.TakeNextSendSequence();
            state.MarkClosed();
            state.FailConnect(new IOException("Session is closed."));
            try
            {
                await SendFrameAsync(
                    new ProtocolFrame(
                        FrameType.Close,
                        sessionId,
                        closeSequence,
                        ProtocolPayloadSerializer.SerializeClose(reasonCode)),
                    cancellationToken);
            }
            catch
            {
            }

            state.ReaderWriter?.Writer.TryComplete();
            state.UdpReaderWriter?.Writer.TryComplete();
            DisposePayloadChannel(state.ReaderWriter);
            state.Dispose();
            StructuredLog.Info(
                "tunnel-client",
                state.IsUdp ? "TUNNEL/UDP" : state.IsHttp ? "TUNNEL/HTTP" : "TUNNEL/TCP",
                "session closed",
                sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        await HandleConnectionFaultAsync(preserveSessions: false);

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask; } catch { }
        }

        if (_heartbeatLoopTask is not null)
        {
            try { await _heartbeatLoopTask; } catch { }
        }

        if (_writeLoopTask is not null)
        {
            try { await _writeLoopTask; } catch { }
        }

        _connectLock.Dispose();
    }
}
