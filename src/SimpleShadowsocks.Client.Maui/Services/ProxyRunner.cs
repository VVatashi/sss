using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Maui.Services;

public sealed class ProxyRunner
{
    private readonly object _sync = new();
    private Socks5Server? _server;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public bool IsRunning { get; private set; }
    public event Action<string>? StatusChanged;

    public void EmitStatus(string message)
    {
        RaiseStatus(message);
    }

    public Task StartAsync(
        ProxyOptions options,
        CancellationToken cancellationToken,
        Action<Socket>? configureTunnelSocket = null)
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("Proxy is already running.");
            }

            var cryptoPolicy = new TunnelCryptoPolicy
            {
                PreferredAlgorithm = options.TunnelCipherAlgorithm
            };

            var connectionPolicy = TunnelConnectionPolicy.Default;
            _server = new Socks5Server(
                IPAddress.Loopback,
                options.ListenPort,
                options.RemoteHost,
                options.RemotePort,
                options.SharedKey,
                cryptoPolicy,
                connectionPolicy,
                options.ProtocolVersion,
                options.EnableCompression,
                options.CompressionAlgorithm,
                configureTunnelSocket: configureTunnelSocket);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var linkedToken = _cts.Token;
            _runTask = RunServerAsync(_server, linkedToken);
            IsRunning = true;
        }

        RaiseStatus($"SOCKS5 started: 127.0.0.1:{options.ListenPort} -> {options.RemoteHost}:{options.RemotePort}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? runTask;
        lock (_sync)
        {
            if (!IsRunning)
            {
                return;
            }

            _cts?.Cancel();
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RaiseStatus($"Proxy stopped with error: {ex.Message}");
            }
        }

        lock (_sync)
        {
            _cts?.Dispose();
            _cts = null;
            _runTask = null;
            _server = null;
            IsRunning = false;
        }

        RaiseStatus("Proxy stopped.");
    }

    private async Task RunServerAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        try
        {
            await server.RunAsync(cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                IsRunning = false;
                _runTask = null;
                _cts?.Dispose();
                _cts = null;
                _server = null;
            }
        }
    }

    private void RaiseStatus(string message)
    {
        AppLog.Write(message);
        StatusChanged?.Invoke(message);
    }
}
