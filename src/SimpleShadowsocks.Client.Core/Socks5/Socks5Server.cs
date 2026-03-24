using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Socks5;

public sealed partial class Socks5Server
{
    private const byte SocksVersion = 0x05;
    private const byte AuthNone = 0x00;
    private const byte AuthNoAcceptableMethods = 0xFF;
    private const byte CommandConnect = 0x01;
    private const byte CommandUdpAssociate = 0x03;
    private const byte AddressTypeIPv4 = 0x01;
    private const byte AddressTypeDomain = 0x03;
    private const byte AddressTypeIPv6 = 0x04;
    private static readonly Meter Socks5Meter = new("SimpleShadowsocks.Client.Core.Socks5", "1.0.0");
    private static readonly Counter<long> UdpAssociateRejectedNoTunnelBackendCounter = Socks5Meter.CreateCounter<long>(
        "socks5_udp_associate_rejected_no_tunnel_backend_total");

    private readonly TcpListener _listener;
    private readonly List<(string Host, int Port)> _remoteServers = new();
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelConnectionPolicy _connectionPolicy;
    private readonly byte _protocolVersion;
    private readonly bool _enableCompression;
    private readonly PayloadCompressionAlgorithm _compressionAlgorithm;
    private readonly TrafficRoutingPolicy? _routingPolicy;
    private readonly Action<Socket>? _configureTunnelSocket;
    private List<TunnelClientMultiplexer>? _multiplexers;
    private int _nextMultiplexerIndex = -1;
    private long _udpAssociateRejectedNoTunnelBackendCount;

    public long UdpAssociateRejectedNoTunnelBackendCount => Volatile.Read(ref _udpAssociateRejectedNoTunnelBackendCount);

    public Socks5Server(IPAddress listenAddress, int port)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes("dev-shared-key");
        _cryptoPolicy = TunnelCryptoPolicy.Default;
        _connectionPolicy = TunnelConnectionPolicy.Default;
        _protocolVersion = ProtocolConstants.Version;
        _enableCompression = false;
        _compressionAlgorithm = PayloadCompressionAlgorithm.Deflate;
    }

    public Socks5Server(
        IPAddress listenAddress,
        int port,
        string remoteServerHost,
        int remoteServerPort,
        string sharedKey,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TunnelConnectionPolicy? connectionPolicy = null,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate,
        TrafficRoutingPolicy? routingPolicy = null,
        Action<Socket>? configureTunnelSocket = null)
    {
        _listener = new TcpListener(listenAddress, port);
        _remoteServers.Add((remoteServerHost, remoteServerPort));
        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
        _cryptoPolicy = cryptoPolicy ?? TunnelCryptoPolicy.Default;
        _connectionPolicy = connectionPolicy ?? TunnelConnectionPolicy.Default;
        _protocolVersion = protocolVersion;
        _enableCompression = enableCompression;
        _compressionAlgorithm = compressionAlgorithm;
        _routingPolicy = routingPolicy;
        _configureTunnelSocket = configureTunnelSocket;
    }

    public Socks5Server(
        IPAddress listenAddress,
        int port,
        IReadOnlyList<(string Host, int Port)> remoteServers,
        string sharedKey,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TunnelConnectionPolicy? connectionPolicy = null,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate,
        TrafficRoutingPolicy? routingPolicy = null,
        Action<Socket>? configureTunnelSocket = null)
    {
        _listener = new TcpListener(listenAddress, port);
        foreach (var (host, serverPort) in remoteServers)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            _remoteServers.Add((host, serverPort));
        }

        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
        _cryptoPolicy = cryptoPolicy ?? TunnelCryptoPolicy.Default;
        _connectionPolicy = connectionPolicy ?? TunnelConnectionPolicy.Default;
        _protocolVersion = protocolVersion;
        _enableCompression = enableCompression;
        _compressionAlgorithm = compressionAlgorithm;
        _routingPolicy = routingPolicy;
        _configureTunnelSocket = configureTunnelSocket;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        StructuredLog.Info("socks5-server", "SOCKS5", $"listening on {_listener.LocalEndpoint}");
        if (_remoteServers.Count > 0)
        {
            _multiplexers = new List<TunnelClientMultiplexer>(_remoteServers.Count);
            foreach (var (host, serverPort) in _remoteServers)
            {
                _multiplexers.Add(new TunnelClientMultiplexer(
                    host,
                    serverPort,
                    _sharedKey,
                    _cryptoPolicy,
                    _connectionPolicy,
                    _protocolVersion,
                    _enableCompression,
                    _compressionAlgorithm,
                    _configureTunnelSocket));
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                StructuredLog.Info(
                    "socks5-server",
                    "SOCKS5",
                    $"accepted client remote={client.Client.RemoteEndPoint} local={client.Client.LocalEndPoint}");
                _ = Task.Run(() => HandleClientSafelyAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_multiplexers is not null)
            {
                foreach (var multiplexer in _multiplexers)
                {
                    await multiplexer.DisposeAsync();
                }
            }

            _listener.Stop();
            StructuredLog.Info("socks5-server", "SOCKS5", "listener stopped");
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StructuredLog.Error("socks5-server", "SOCKS5", "client handling failed", ex);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientStream = client.GetStream();
        var candidateMultiplexers = SelectMultiplexersForClient();

        if (!await HandleGreetingAsync(clientStream, cancellationToken))
        {
            return;
        }

        var request = await ReadConnectRequestAsync(clientStream, cancellationToken);
        if (request is null)
        {
            return;
        }

        var matchedRule = _routingPolicy?.Match(request.Value);

        switch (request.Value.Command)
        {
            case CommandConnect:
                StructuredLog.Info("socks5-server", "SOCKS5/TCP", $"connect request target={request.Value.Host}:{request.Value.Port}");
                if (matchedRule is null && _routingPolicy is not null)
                {
                    StructuredLog.Warn(
                        "socks5-server",
                        "SOCKS5/TCP",
                        $"connect request rejected: no routing rule matched target={request.Value.Host}:{request.Value.Port}");
                    await SendReplyAsync(clientStream, replyCode: 0x02, null, cancellationToken);
                    return;
                }

                if (matchedRule?.Decision == TrafficRouteDecision.Direct)
                {
                    await HandleDirectAsync(clientStream, request.Value, cancellationToken);
                    return;
                }

                if (matchedRule?.Decision == TrafficRouteDecision.Drop)
                {
                    StructuredLog.Warn(
                        "socks5-server",
                        "SOCKS5/TCP",
                        $"connect request dropped by routing rule target={request.Value.Host}:{request.Value.Port}");
                    await SendReplyAsync(clientStream, replyCode: 0x02, null, cancellationToken);
                    return;
                }

                if (matchedRule?.Decision == TrafficRouteDecision.Tunnel)
                {
                    if (candidateMultiplexers.Count == 0)
                    {
                        StructuredLog.Warn(
                            "socks5-server",
                            "SOCKS5/TCP",
                            $"connect request rejected: routed to tunnel but no tunnel backend is configured target={request.Value.Host}:{request.Value.Port}");
                        await SendReplyAsync(clientStream, replyCode: 0x01, null, cancellationToken);
                        return;
                    }

                    await HandleViaTunnelAsync(clientStream, request.Value, candidateMultiplexers, cancellationToken);
                    return;
                }

                if (candidateMultiplexers.Count == 0)
                {
                    await HandleDirectAsync(clientStream, request.Value, cancellationToken);
                    return;
                }

                await HandleViaTunnelAsync(clientStream, request.Value, candidateMultiplexers, cancellationToken);
                return;

            case CommandUdpAssociate:
                StructuredLog.Info("socks5-server", "SOCKS5/UDP", $"udp associate request client={request.Value.Host}:{request.Value.Port}");
                if (_routingPolicy is not null)
                {
                    await HandleUdpAssociateRoutedAsync(clientStream, request.Value, candidateMultiplexers, cancellationToken);
                    return;
                }

                if (candidateMultiplexers.Count == 0)
                {
                    Interlocked.Increment(ref _udpAssociateRejectedNoTunnelBackendCount);
                    UdpAssociateRejectedNoTunnelBackendCounter.Add(1);
                    StructuredLog.Warn("socks5-server", "SOCKS5/UDP", "UDP disabled: no tunnel backend");
                    await SendReplyAsync(clientStream, replyCode: 0x01, null, cancellationToken);
                    return;
                }

                await HandleUdpAssociateViaTunnelAsync(clientStream, request.Value, candidateMultiplexers[0], cancellationToken);
                return;

            default:
                await SendReplyAsync(clientStream, replyCode: 0x07, null, cancellationToken);
                return;
        }
    }

    private IReadOnlyList<TunnelClientMultiplexer> SelectMultiplexersForClient()
    {
        var multiplexers = _multiplexers;
        if (multiplexers is null || multiplexers.Count == 0)
        {
            return Array.Empty<TunnelClientMultiplexer>();
        }

        var index = Interlocked.Increment(ref _nextMultiplexerIndex);
        var startIndex = (index & int.MaxValue) % multiplexers.Count;

        if (multiplexers.Count == 1)
        {
            return multiplexers;
        }

        var ordered = new TunnelClientMultiplexer[multiplexers.Count];
        for (var offset = 0; offset < multiplexers.Count; offset++)
        {
            ordered[offset] = multiplexers[(startIndex + offset) % multiplexers.Count];
        }

        return ordered;
    }
}
