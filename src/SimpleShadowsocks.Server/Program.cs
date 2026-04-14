using System.Net;
using System.Text.Json;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Http;
using SimpleShadowsocks.Server.Tunnel;

var config = ServerConfig.Load();
var listenPort = config.ListenPort;
var sharedKey = config.SharedKey;
var httpReverseProxy = config.HttpReverseProxy ?? new ServerConfig.HttpReverseProxyConfig();
var cryptoPolicy = new TunnelCryptoPolicy
{
    HandshakeMaxClockSkewSeconds = config.HandshakeMaxClockSkewSeconds,
    ReplayWindowSeconds = config.ReplayWindowSeconds
};
var serverPolicy = new TunnelServerPolicy
{
    MaxConcurrentTunnels = config.MaxConcurrentTunnels,
    MaxSessionsPerTunnel = config.MaxSessionsPerTunnel,
    ConnectTimeoutMs = config.ConnectTimeoutMs
};

if (args.Length > 0 && int.TryParse(args[0], out var parsedPort))
{
    listenPort = parsedPort;
}

if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
{
    sharedKey = args[1];
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

StructuredLog.Info("server-host", "CONTROL", "SimpleShadowsocks.Server started");
StructuredLog.Info("server-host", "TUNNEL/TCP", $"listen=0.0.0.0:{listenPort}");
StructuredLog.Info(
    "server-host",
    "CONTROL",
    $"protocol_versions=v{ProtocolConstants.LegacyVersion},v{ProtocolConstants.Version2},v{ProtocolConstants.Version}");
if (httpReverseProxy.Enabled)
{
    StructuredLog.Info("server-host", "HTTP", $"reverse_listen={config.GetHttpReverseProxyListenIPAddress()}:{httpReverseProxy.ListenPort}");
}
StructuredLog.Info("server-host", "CONTROL", "press Ctrl+C to stop");

var server = new TunnelServer(IPAddress.Any, listenPort, sharedKey, cryptoPolicy, serverPolicy);
var runTasks = new List<Task> { server.RunAsync(cts.Token) };
if (httpReverseProxy.Enabled)
{
    var reverseProxyServer = new HttpReverseProxyServer(
        config.GetHttpReverseProxyListenIPAddress(),
        httpReverseProxy.ListenPort,
        server);
    runTasks.Add(reverseProxyServer.RunAsync(cts.Token));
}

await Task.WhenAll(runTasks);

internal sealed class ServerConfig
{
    public int ListenPort { get; init; } = 8388;
    public string SharedKey { get; init; } = "dev-shared-key";
    public int HandshakeMaxClockSkewSeconds { get; init; } = 60;
    public int ReplayWindowSeconds { get; init; } = 300;
    public int MaxConcurrentTunnels { get; init; } = 1024;
    public int MaxSessionsPerTunnel { get; init; } = 1024;
    public int ConnectTimeoutMs { get; init; } = 10000;
    public HttpReverseProxyConfig? HttpReverseProxy { get; init; }

    public IPAddress GetHttpReverseProxyListenIPAddress()
    {
        var configuredAddress = HttpReverseProxy?.ListenAddress;
        if (!string.IsNullOrWhiteSpace(configuredAddress) && IPAddress.TryParse(configuredAddress, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Unsupported HttpReverseProxy.ListenAddress: '{configuredAddress}'. Expected a valid IPv4 or IPv6 literal.");
    }

    public static ServerConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new ServerConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
    }

    public sealed class HttpReverseProxyConfig
    {
        public bool Enabled { get; init; }
        public int ListenPort { get; init; } = 8081;
        public string ListenAddress { get; init; } = IPAddress.Loopback.ToString();
    }
}
