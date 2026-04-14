using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed class TunnelServerPolicy
{
    public static TunnelServerPolicy Default { get; } = new();

    public int MaxConcurrentTunnels { get; init; } = 1024;
    public int MaxSessionsPerTunnel { get; init; } = 1024;
    public int ConnectTimeoutMs { get; init; } = 10_000;
    internal Func<ConnectRequest, int, CancellationToken, ValueTask<byte?>>? ConnectReplyOverrideAsync { get; init; }
}

public sealed partial class TunnelServer
{
    private readonly TcpListener _listener;
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private readonly TunnelServerPolicy _serverPolicy;
    private readonly TaskCompletionSource<bool> _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, ActiveTunnelConnection> _activeTunnelConnections = new();
    private int _acceptedTunnelConnections;
    private int _activeTunnelConnectionCount;
    private long _nextTunnelConnectionId;

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
        _startedTcs.TrySetResult(true);
        StructuredLog.Info("tunnel-server", "TUNNEL/TCP", $"listening on {_listener.LocalEndpoint}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tunnelClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                if (!TryAcquireTunnelSlot())
                {
                    StructuredLog.Warn(
                        "tunnel-server",
                        "TUNNEL/TCP",
                        $"reject connection remote={tunnelClient.Client.RemoteEndPoint}: max concurrent tunnels reached");
                    tunnelClient.Dispose();
                    continue;
                }

                Interlocked.Increment(ref _acceptedTunnelConnections);
                StructuredLog.Info(
                    "tunnel-server",
                    "TUNNEL/TCP",
                    $"accepted tunnel connection remote={tunnelClient.Client.RemoteEndPoint} local={tunnelClient.Client.LocalEndPoint}");
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
            StructuredLog.Info("tunnel-server", "TUNNEL/TCP", "listener stopped");
        }
    }

    internal Task WaitUntilStartedAsync()
    {
        return _startedTcs.Task;
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
                StructuredLog.Error("tunnel-server", "TUNNEL/TCP", "tunnel client failed", ex);
            }
            finally
            {
                Interlocked.Decrement(ref _activeTunnelConnectionCount);
            }
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
            var current = Volatile.Read(ref _activeTunnelConnectionCount);
            if (current >= _serverPolicy.MaxConcurrentTunnels)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeTunnelConnectionCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }
}
