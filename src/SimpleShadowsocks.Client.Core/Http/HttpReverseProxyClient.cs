using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Http;

public sealed class HttpReverseProxyClient : IAsyncDisposable
{
    private readonly List<TunnelClientMultiplexer> _multiplexers = new();

    internal int ActiveReverseHttpSessionCount
    {
        get
        {
            var total = 0;
            foreach (var multiplexer in _multiplexers)
            {
                total += multiplexer.ActiveReverseHttpSessionCount;
            }

            return total;
        }
    }

    public HttpReverseProxyClient(
        IReadOnlyList<(string Host, int Port)> remoteServers,
        string sharedKey,
        IEnumerable<HttpReverseProxyTunnelHandler.Route> routes,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TunnelConnectionPolicy? connectionPolicy = null,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate,
        Action<Socket>? configureTunnelSocket = null)
    {
        ArgumentNullException.ThrowIfNull(remoteServers);

        var reverseHandler = new HttpReverseProxyTunnelHandler(routes);
        var sharedKeyBytes = PreSharedKey.Derive32Bytes(sharedKey);
        foreach (var (host, port) in remoteServers)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0)
            {
                continue;
            }

            _multiplexers.Add(new TunnelClientMultiplexer(
                host,
                port,
                sharedKeyBytes,
                cryptoPolicy ?? TunnelCryptoPolicy.Default,
                connectionPolicy ?? TunnelConnectionPolicy.Default,
                protocolVersion,
                enableCompression,
                compressionAlgorithm,
                configureTunnelSocket,
                reverseHandler));
        }
    }

    public HttpReverseProxyClient(
        string remoteServerHost,
        int remoteServerPort,
        string sharedKey,
        IEnumerable<HttpReverseProxyTunnelHandler.Route> routes,
        TunnelCryptoPolicy? cryptoPolicy = null,
        TunnelConnectionPolicy? connectionPolicy = null,
        byte protocolVersion = ProtocolConstants.Version,
        bool enableCompression = false,
        PayloadCompressionAlgorithm compressionAlgorithm = PayloadCompressionAlgorithm.Deflate,
        Action<Socket>? configureTunnelSocket = null)
        : this(
            [(remoteServerHost, remoteServerPort)],
            sharedKey,
            routes,
            cryptoPolicy,
            connectionPolicy,
            protocolVersion,
            enableCompression,
            compressionAlgorithm,
            configureTunnelSocket)
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_multiplexers.Count == 0)
        {
            throw new InvalidOperationException("At least one remote server must be configured for HTTP reverse proxy.");
        }

        try
        {
            await Task.WhenAll(_multiplexers.Select(multiplexer => multiplexer.RunPersistentAsync(cancellationToken)));
        }
        finally
        {
            await DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var multiplexer in _multiplexers)
        {
            await multiplexer.DisposeAsync();
        }
    }
}
