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
    private const byte AddressTypeIPv4 = 0x01;
    private const byte AddressTypeDomain = 0x03;
    private const byte AddressTypeIPv6 = 0x04;

    private readonly TcpListener _listener;
    private readonly List<(string Host, int Port)> _remoteServers = new();
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelConnectionPolicy _connectionPolicy;
    private readonly byte _protocolVersion;
    private readonly bool _enableCompression;
    private readonly PayloadCompressionAlgorithm _compressionAlgorithm;
    private readonly Action<Socket>? _configureTunnelSocket;
    private List<TunnelClientMultiplexer>? _multiplexers;
    private int _nextMultiplexerIndex = -1;

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
        _configureTunnelSocket = configureTunnelSocket;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
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
                Console.WriteLine($"[socks5] client failed: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientStream = client.GetStream();
        var selectedMultiplexer = SelectMultiplexerForClient();

        if (!await HandleGreetingAsync(clientStream, cancellationToken))
        {
            return;
        }

        var request = await ReadConnectRequestAsync(clientStream, cancellationToken);
        if (request is null)
        {
            return;
        }

        Console.WriteLine($"[socks5] proxy {request.Value.Host}:{request.Value.Port}");

        if (selectedMultiplexer is null)
        {
            await HandleDirectAsync(clientStream, request.Value, cancellationToken);
            return;
        }

        await HandleViaTunnelAsync(clientStream, request.Value, selectedMultiplexer, cancellationToken);
    }

    private TunnelClientMultiplexer? SelectMultiplexerForClient()
    {
        var multiplexers = _multiplexers;
        if (multiplexers is null || multiplexers.Count == 0)
        {
            return null;
        }

        var index = Interlocked.Increment(ref _nextMultiplexerIndex);
        var normalizedIndex = (index & int.MaxValue) % multiplexers.Count;
        return multiplexers[normalizedIndex];
    }
}
