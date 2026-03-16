using System.Net;
using System.Text.Json;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

var config = ClientConfig.Load();
var listenPort = config.ListenPort;
var remoteHost = config.RemoteHost;
var remotePort = config.RemotePort;
var remoteServers = (config.RemoteServers ?? [])
    .Where(s => !string.IsNullOrWhiteSpace(s.Host) && s.Port > 0)
    .Select(s => (s.Host.Trim(), s.Port))
    .ToList();
var sharedKey = config.SharedKey;
var protocolVersion = config.ProtocolVersion;
var enableCompression = config.EnableCompression;
var compressionAlgorithm = config.GetCompressionAlgorithm();
var tunnelCipherAlgorithm = config.GetTunnelCipherAlgorithm();
var cryptoPolicy = new TunnelCryptoPolicy
{
    HandshakeMaxClockSkewSeconds = config.HandshakeMaxClockSkewSeconds,
    ReplayWindowSeconds = config.ReplayWindowSeconds,
    PreferredAlgorithm = tunnelCipherAlgorithm
};
var connectionPolicy = new TunnelConnectionPolicy
{
    HeartbeatIntervalSeconds = config.HeartbeatIntervalSeconds,
    IdleTimeoutSeconds = config.IdleTimeoutSeconds,
    ReconnectBaseDelayMs = config.ReconnectBaseDelayMs,
    ReconnectMaxDelayMs = config.ReconnectMaxDelayMs,
    ReconnectMaxAttempts = config.ReconnectMaxAttempts,
    MaxConcurrentSessions = config.MaxConcurrentSessions,
    SessionReceiveChannelCapacity = config.SessionReceiveChannelCapacity
};

if (args.Length > 0 && int.TryParse(args[0], out var parsedPort))
{
    listenPort = parsedPort;
}

if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
{
    remoteHost = args[1];
    remoteServers.Clear();
}

if (args.Length > 2 && int.TryParse(args[2], out var parsedRemotePort))
{
    remotePort = parsedRemotePort;
    remoteServers.Clear();
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

StructuredLog.Info("client-host", "CONTROL", "SimpleShadowsocks.Client started");
StructuredLog.Info("client-host", "SOCKS5", $"listen=127.0.0.1:{listenPort}");
if (remoteServers.Count > 0)
{
    StructuredLog.Info("client-host", "TUNNEL/TCP", $"servers={string.Join(", ", remoteServers.Select(s => $"{s.Item1}:{s.Item2}"))}");
}
else
{
    StructuredLog.Info("client-host", "TUNNEL/TCP", $"server={remoteHost}:{remotePort}");
}
StructuredLog.Info("client-host", "CONTROL", $"protocol_version={ProtocolConstants.Version}");
StructuredLog.Info(
    "client-host",
    "TUNNEL/TCP",
    $"configured_tunnel_protocol=v{protocolVersion}, compression={(enableCompression ? "on" : "off")}({compressionAlgorithm}), aead={tunnelCipherAlgorithm}");
StructuredLog.Info("client-host", "CONTROL", "press Ctrl+C to stop");

var server = remoteServers.Count > 0
    ? new Socks5Server(
        IPAddress.Loopback,
        listenPort,
        remoteServers,
        sharedKey,
        cryptoPolicy,
        connectionPolicy,
        protocolVersion,
        enableCompression,
        compressionAlgorithm)
    : new Socks5Server(
        IPAddress.Loopback,
        listenPort,
        remoteHost,
        remotePort,
        sharedKey,
        cryptoPolicy,
        connectionPolicy,
        protocolVersion,
        enableCompression,
        compressionAlgorithm);
await server.RunAsync(cts.Token);

internal sealed class ClientConfig
{
    public int ListenPort { get; init; } = 1080;
    public string RemoteHost { get; init; } = "127.0.0.1";
    public int RemotePort { get; init; } = 8388;
    public string SharedKey { get; init; } = "dev-shared-key";
    public int HandshakeMaxClockSkewSeconds { get; init; } = 60;
    public int ReplayWindowSeconds { get; init; } = 300;
    public int HeartbeatIntervalSeconds { get; init; } = 10;
    public int IdleTimeoutSeconds { get; init; } = 45;
    public int ReconnectBaseDelayMs { get; init; } = 200;
    public int ReconnectMaxDelayMs { get; init; } = 2000;
    public int ReconnectMaxAttempts { get; init; } = 12;
    public int MaxConcurrentSessions { get; init; } = 1024;
    public int SessionReceiveChannelCapacity { get; init; } = 256;
    public byte ProtocolVersion { get; init; } = ProtocolConstants.Version;
    public bool EnableCompression { get; init; } = false;
    public string CompressionAlgorithm { get; init; } = nameof(SimpleShadowsocks.Protocol.PayloadCompressionAlgorithm.Deflate);
    public string TunnelCipherAlgorithm { get; init; } = nameof(SimpleShadowsocks.Protocol.Crypto.TunnelCipherAlgorithm.ChaCha20Poly1305);
    public List<RemoteServerConfig>? RemoteServers { get; init; }

    public SimpleShadowsocks.Protocol.Crypto.TunnelCipherAlgorithm GetTunnelCipherAlgorithm()
    {
        if (Enum.TryParse<SimpleShadowsocks.Protocol.Crypto.TunnelCipherAlgorithm>(TunnelCipherAlgorithm, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Unsupported TunnelCipherAlgorithm: '{TunnelCipherAlgorithm}'. " +
            $"Supported: {nameof(SimpleShadowsocks.Protocol.Crypto.TunnelCipherAlgorithm.ChaCha20Poly1305)}, " +
            $"{nameof(SimpleShadowsocks.Protocol.Crypto.TunnelCipherAlgorithm.Aes256Gcm)}, " +
            $"{nameof(SimpleShadowsocks.Protocol.Crypto.TunnelCipherAlgorithm.Aegis128L)}, " +
            $"{nameof(SimpleShadowsocks.Protocol.Crypto.TunnelCipherAlgorithm.Aegis256)}");
    }

    public SimpleShadowsocks.Protocol.PayloadCompressionAlgorithm GetCompressionAlgorithm()
    {
        if (Enum.TryParse<SimpleShadowsocks.Protocol.PayloadCompressionAlgorithm>(CompressionAlgorithm, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Unsupported CompressionAlgorithm: '{CompressionAlgorithm}'. " +
            $"Supported: {nameof(SimpleShadowsocks.Protocol.PayloadCompressionAlgorithm.Deflate)}, " +
            $"{nameof(SimpleShadowsocks.Protocol.PayloadCompressionAlgorithm.Gzip)}, " +
            $"{nameof(SimpleShadowsocks.Protocol.PayloadCompressionAlgorithm.Brotli)}");
    }

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

    public sealed class RemoteServerConfig
    {
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
    }
}
