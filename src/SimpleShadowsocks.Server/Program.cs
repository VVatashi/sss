using System.Net;
using System.Text.Json;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Server.Tunnel;

var config = ServerConfig.Load();
var listenPort = config.ListenPort;
var sharedKey = config.SharedKey;
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

Console.WriteLine("SimpleShadowsocks.Server");
Console.WriteLine($"Tunnel listen: 0.0.0.0:{listenPort}");
Console.WriteLine($"Protocol versions: v{ProtocolConstants.LegacyVersion}, v{ProtocolConstants.Version}");
Console.WriteLine("Press Ctrl+C to stop.");

var server = new TunnelServer(IPAddress.Any, listenPort, sharedKey, cryptoPolicy, serverPolicy);
await server.RunAsync(cts.Token);

internal sealed class ServerConfig
{
    public int ListenPort { get; init; } = 8388;
    public string SharedKey { get; init; } = "dev-shared-key";
    public int HandshakeMaxClockSkewSeconds { get; init; } = 60;
    public int ReplayWindowSeconds { get; init; } = 300;
    public int MaxConcurrentTunnels { get; init; } = 1024;
    public int MaxSessionsPerTunnel { get; init; } = 1024;
    public int ConnectTimeoutMs { get; init; } = 10000;

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
}
