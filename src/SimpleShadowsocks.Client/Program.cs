using System.Net;
using System.Text.Json;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

var config = ClientConfig.Load();
var listenPort = config.ListenPort;
var remoteHost = config.RemoteHost;
var remotePort = config.RemotePort;
var sharedKey = config.SharedKey;
var cryptoPolicy = new TunnelCryptoPolicy
{
    HandshakeMaxClockSkewSeconds = config.HandshakeMaxClockSkewSeconds,
    ReplayWindowSeconds = config.ReplayWindowSeconds
};

if (args.Length > 0 && int.TryParse(args[0], out var parsedPort))
{
    listenPort = parsedPort;
}

if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
{
    remoteHost = args[1];
}

if (args.Length > 2 && int.TryParse(args[2], out var parsedRemotePort))
{
    remotePort = parsedRemotePort;
}

if (args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]))
{
    sharedKey = args[3];
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

Console.WriteLine("SimpleShadowsocks.Client");
Console.WriteLine($"SOCKS5 listen: 127.0.0.1:{listenPort}");
Console.WriteLine($"Tunnel server: {remoteHost}:{remotePort}");
Console.WriteLine($"Protocol version: {ProtocolConstants.Version}");
Console.WriteLine("Press Ctrl+C to stop.");

var server = new Socks5Server(IPAddress.Loopback, listenPort, remoteHost, remotePort, sharedKey, cryptoPolicy);
await server.RunAsync(cts.Token);

internal sealed class ClientConfig
{
    public int ListenPort { get; init; } = 1080;
    public string RemoteHost { get; init; } = "127.0.0.1";
    public int RemotePort { get; init; } = 8388;
    public string SharedKey { get; init; } = "dev-shared-key";
    public int HandshakeMaxClockSkewSeconds { get; init; } = 60;
    public int ReplayWindowSeconds { get; init; } = 300;

    public static ClientConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new ClientConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClientConfig>(json) ?? new ClientConfig();
    }
}
