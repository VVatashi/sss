using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer : IAsyncDisposable
{
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelConnectionPolicy _connectionPolicy;
    private readonly byte _protocolVersion;
    private readonly ProtocolWriteOptions _writeOptions;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, SessionState> _sessions = new();

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

    public TunnelClientMultiplexer(
        string remoteHost,
        int remotePort,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
        TunnelConnectionPolicy connectionPolicy,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate)
    {
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _sharedKey = sharedKey;
        _cryptoPolicy = cryptoPolicy;
        _connectionPolicy = connectionPolicy;
        _protocolVersion = protocolVersion;
        _writeOptions = new ProtocolWriteOptions
        {
            Version = protocolVersion,
            EnableCompression = enableCompression,
            CompressionAlgorithm = compressionAlgorithm
        };
        ValidatePolicy(_connectionPolicy);
        TouchIncoming();
    }

    public async Task<(uint SessionId, byte ReplyCode, ChannelReader<byte[]> Reader)> OpenSessionAsync(
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
            if (replyCode != 0x00)
            {
                _sessions.TryRemove(sessionId, out _);
                state.ReaderWriter.Writer.TryComplete();
            }

            return (sessionId, replyCode, state.ReaderWriter.Reader);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            state.ReaderWriter.Writer.TryComplete();
            throw;
        }
    }

    public Task SendDataAsync(uint sessionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        return SendSessionFrameAsync(sessionId, FrameType.Data, payload, cancellationToken);
    }

    public async Task CloseSessionAsync(uint sessionId, byte reasonCode, CancellationToken cancellationToken)
    {
        if (_sessions.TryRemove(sessionId, out var state))
        {
            state.MarkClosed();
            state.FailConnect(new IOException("Session is closed."));
            var sequence = state.TakeNextSendSequence();
            try
            {
                await SendFrameAsync(
                    new ProtocolFrame(FrameType.Close, sessionId, sequence, ProtocolPayloadSerializer.SerializeClose(reasonCode)),
                    cancellationToken);
            }
            catch
            {
            }

            state.ReaderWriter.Writer.TryComplete();
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
