using System.Net;
using System.Text.Json;
using SimpleShadowsocks.Client.Http;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

var config = ClientConfig.Load();
var listenPort = config.ListenPort;
var listenAddress = config.GetListenIPAddress();
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
var routingPolicy = config.GetTrafficRoutingPolicy();
var socks5Authentication = config.GetSocks5AuthenticationOptions();
var httpProxy = config.HttpProxy ?? new ClientConfig.HttpProxyConfig();
var httpReverseProxy = config.HttpReverseProxy ?? new ClientConfig.HttpReverseProxyConfig();
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
StructuredLog.Info("client-host", "SOCKS5", $"listen={listenAddress}:{listenPort}");
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
StructuredLog.Info(
    "client-host",
    "SOCKS5",
    $"routing_rules={string.Join("; ", routingPolicy.Rules.Select((rule, index) => $"#{index + 1}:{rule.MatchType}:{rule.Match}->{rule.Decision}"))}");
StructuredLog.Info(
    "client-host",
    "SOCKS5/AUTH",
    socks5Authentication.Enabled
        ? $"incoming_auth=username-password(username={socks5Authentication.Username})"
        : "incoming_auth=disabled");
if (httpProxy.Enabled)
{
    var httpListenAddress = config.GetHttpProxyListenIPAddress();
    StructuredLog.Info("client-host", "HTTP", $"listen={httpListenAddress}:{httpProxy.ListenPort}");
}
if (httpReverseProxy.Enabled)
{
    StructuredLog.Info(
        "client-host",
        "HTTP",
        $"reverse_routes={string.Join("; ", config.GetHttpReverseProxyRoutes().Select((route, index) => $"#{index + 1}:{route.Host ?? "*"}:{route.PathPrefix ?? "*"}->{route.TargetBaseUri}"))}");
}
StructuredLog.Info("client-host", "CONTROL", "press Ctrl+C to stop");

var socksServer = remoteServers.Count > 0
    ? new Socks5Server(
        listenAddress,
        listenPort,
        remoteServers,
        sharedKey,
        cryptoPolicy,
        connectionPolicy,
        protocolVersion,
        enableCompression,
        compressionAlgorithm,
        routingPolicy,
        authenticationOptions: socks5Authentication)
    : new Socks5Server(
        listenAddress,
        listenPort,
        remoteHost,
        remotePort,
        sharedKey,
        cryptoPolicy,
        connectionPolicy,
        protocolVersion,
        enableCompression,
        compressionAlgorithm,
        routingPolicy,
        authenticationOptions: socks5Authentication);

var runTasks = new List<Task> { socksServer.RunAsync(cts.Token) };
if (httpProxy.Enabled)
{
    var httpListenAddress = config.GetHttpProxyListenIPAddress();
    var httpServer = remoteServers.Count > 0
        ? new HttpProxyServer(
            httpListenAddress,
            httpProxy.ListenPort,
            remoteServers,
            sharedKey,
            cryptoPolicy,
            connectionPolicy,
            protocolVersion,
            enableCompression,
            compressionAlgorithm,
            routingPolicy)
        : new HttpProxyServer(
            httpListenAddress,
            httpProxy.ListenPort,
            remoteHost,
            remotePort,
            sharedKey,
            cryptoPolicy,
            connectionPolicy,
            protocolVersion,
            enableCompression,
            compressionAlgorithm,
            routingPolicy);
    runTasks.Add(httpServer.RunAsync(cts.Token));
}
if (httpReverseProxy.Enabled)
{
    var reverseRoutes = config.GetHttpReverseProxyRoutes();
    var reverseProxyClient = remoteServers.Count > 0
        ? new HttpReverseProxyClient(
            remoteServers,
            sharedKey,
            reverseRoutes,
            cryptoPolicy,
            connectionPolicy,
            protocolVersion,
            enableCompression,
            compressionAlgorithm)
        : new HttpReverseProxyClient(
            remoteHost,
            remotePort,
            sharedKey,
            reverseRoutes,
            cryptoPolicy,
            connectionPolicy,
            protocolVersion,
            enableCompression,
            compressionAlgorithm);
    runTasks.Add(reverseProxyClient.RunAsync(cts.Token));
}

await Task.WhenAll(runTasks);

internal sealed class ClientConfig
{
    public int ListenPort { get; init; } = 1080;
    public string ListenAddress { get; init; } = IPAddress.Loopback.ToString();
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
    public List<TrafficRoutingRuleConfig>? TrafficRoutingRules { get; init; }
    public Socks5AuthenticationConfig? Socks5Authentication { get; init; }
    public HttpProxyConfig? HttpProxy { get; init; }
    public HttpReverseProxyConfig? HttpReverseProxy { get; init; }

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

    public IPAddress GetListenIPAddress()
    {
        return ParseListenIPAddress(ListenAddress, nameof(ListenAddress));
    }

    public IPAddress GetHttpProxyListenIPAddress()
    {
        var configuredAddress = HttpProxy?.ListenAddress;
        return string.IsNullOrWhiteSpace(configuredAddress)
            ? GetListenIPAddress()
            : ParseListenIPAddress(configuredAddress, "HttpProxy.ListenAddress");
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

    public TrafficRoutingPolicy GetTrafficRoutingPolicy()
    {
        var rules = TrafficRoutingRules;
        if (rules is null || rules.Count == 0)
        {
            return new TrafficRoutingPolicy(
            [
                new TrafficRoutingRule
                {
                    MatchType = TrafficRouteMatchType.Any,
                    Match = "*",
                    Decision = TrafficRouteDecision.Tunnel
                }
            ]);
        }

        return new TrafficRoutingPolicy(rules.Select((rule, index) => rule.ToRuntimeRule(index)));
    }

    public Socks5AuthenticationOptions GetSocks5AuthenticationOptions()
    {
        var auth = Socks5Authentication;
        if (auth is null || !auth.Enabled)
        {
            return Socks5AuthenticationOptions.Disabled;
        }

        return new Socks5AuthenticationOptions(auth.Username, auth.Password);
    }

    public IReadOnlyList<HttpReverseProxyTunnelHandler.Route> GetHttpReverseProxyRoutes()
    {
        var config = HttpReverseProxy;
        if (config is null || !config.Enabled)
        {
            return [];
        }

        if (config.Routes is null || config.Routes.Count == 0)
        {
            throw new InvalidDataException("HttpReverseProxy.Routes must contain at least one route when reverse proxy is enabled.");
        }

        return config.Routes.Select((route, index) => route.ToRuntimeRoute(index)).ToArray();
    }

    public sealed class RemoteServerConfig
    {
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
    }

    public sealed class TrafficRoutingRuleConfig
    {
        public string? Type { get; init; }
        public string Match { get; init; } = "*";
        public string Decision { get; init; } = nameof(TrafficRouteDecision.Tunnel);

        public TrafficRoutingRule ToRuntimeRule(int index)
        {
            var normalizedMatch = Match?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedMatch))
            {
                throw new InvalidDataException($"TrafficRoutingRules[{index}].Match must not be empty.");
            }

            var matchType = ResolveMatchType(Type, normalizedMatch, index);
            return TrafficRoutingRuleFactory.Create(
                normalizedMatch,
                ParseDecision(index),
                matchType);
        }

        private TrafficRouteDecision ParseDecision(int index)
        {
            if (Enum.TryParse<TrafficRouteDecision>(Decision, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            throw new InvalidDataException(
                $"Unsupported TrafficRoutingRules[{index}].Decision: '{Decision}'. " +
                $"Supported: {nameof(TrafficRouteDecision.Tunnel)}, {nameof(TrafficRouteDecision.Direct)}, {nameof(TrafficRouteDecision.Drop)}");
        }

        private static TrafficRouteMatchType? ResolveMatchType(string? configuredType, string match, int index)
        {
            if (!string.IsNullOrWhiteSpace(configuredType))
            {
                if (Enum.TryParse<TrafficRouteMatchType>(configuredType, ignoreCase: true, out var parsed))
                {
                    return parsed;
                }

                throw new InvalidDataException(
                    $"Unsupported TrafficRoutingRules[{index}].Type: '{configuredType}'. " +
                    $"Supported: {nameof(TrafficRouteMatchType.Any)}, {nameof(TrafficRouteMatchType.Host)}, {nameof(TrafficRouteMatchType.Subnet)}");
            }

            return null;
        }
    }

    public sealed class Socks5AuthenticationConfig
    {
        public bool Enabled { get; init; }
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
    }

    public sealed class HttpProxyConfig
    {
        public bool Enabled { get; init; }
        public int ListenPort { get; init; } = 8080;
        public string? ListenAddress { get; init; }
    }

    public sealed class HttpReverseProxyConfig
    {
        public bool Enabled { get; init; }
        public List<HttpReverseProxyRouteConfig>? Routes { get; init; }
    }

    public sealed class HttpReverseProxyRouteConfig
    {
        public string? Host { get; init; }
        public string? PathPrefix { get; init; }
        public string TargetBaseUrl { get; init; } = string.Empty;
        public bool StripPathPrefix { get; init; }

        public HttpReverseProxyTunnelHandler.Route ToRuntimeRoute(int index)
        {
            var normalizedHost = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
            var normalizedPathPrefix = string.IsNullOrWhiteSpace(PathPrefix) ? null : NormalizePathPrefix(PathPrefix.Trim(), index);
            if (normalizedHost is null && normalizedPathPrefix is null)
            {
                throw new InvalidDataException(
                    $"HttpReverseProxy.Routes[{index}] must specify at least Host or PathPrefix.");
            }

            if (!Uri.TryCreate(TargetBaseUrl, UriKind.Absolute, out var targetBaseUri)
                || !string.Equals(targetBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"HttpReverseProxy.Routes[{index}].TargetBaseUrl must be an absolute http:// URL.");
            }

            return new HttpReverseProxyTunnelHandler.Route(
                normalizedHost,
                normalizedPathPrefix,
                targetBaseUri,
                StripPathPrefix);
        }

        private static string NormalizePathPrefix(string prefix, int index)
        {
            if (!prefix.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"HttpReverseProxy.Routes[{index}].PathPrefix must start with '/'.");
            }

            return prefix.Length > 1 && prefix.EndsWith("/", StringComparison.Ordinal)
                ? prefix.TrimEnd('/')
                : prefix;
        }
    }

    private static IPAddress ParseListenIPAddress(string? value, string configKey)
    {
        if (!string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Unsupported {configKey}: '{value}'. " +
            "Expected a valid IPv4 or IPv6 literal.");
    }
}
