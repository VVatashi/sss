using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Maui.Services;

public sealed record ProxyRoutingRule(string Match, TrafficRouteDecision Decision);

public sealed record ProxyOptions(
    int ListenPort,
    string RemoteHost,
    int RemotePort,
    string SharedKey,
    List<ProxyRoutingRule> RoutingRules,
    bool EnableSocks5Authentication,
    string Socks5Username,
    string Socks5Password,
    byte ProtocolVersion,
    bool EnableCompression,
    PayloadCompressionAlgorithm CompressionAlgorithm,
    TunnelCipherAlgorithm TunnelCipherAlgorithm)
{
    public static ProxyOptions CreateDefaults()
    {
        return new ProxyOptions(
            ListenPort: 1080,
            RemoteHost: "127.0.0.1",
            RemotePort: 8388,
            SharedKey: "dev-shared-key",
            RoutingRules:
            [
                new ProxyRoutingRule("*", TrafficRouteDecision.Tunnel)
            ],
            EnableSocks5Authentication: false,
            Socks5Username: string.Empty,
            Socks5Password: string.Empty,
            ProtocolVersion: ProtocolConstants.Version,
            EnableCompression: false,
            CompressionAlgorithm: PayloadCompressionAlgorithm.Deflate,
            TunnelCipherAlgorithm: TunnelCipherAlgorithm.ChaCha20Poly1305);
    }

    public TrafficRoutingPolicy GetTrafficRoutingPolicy()
    {
        if (RoutingRules.Count == 0)
        {
            return new TrafficRoutingPolicy(
            [
                TrafficRoutingRuleFactory.Create("*", TrafficRouteDecision.Tunnel)
            ]);
        }

        return new TrafficRoutingPolicy(RoutingRules.Select(rule =>
            TrafficRoutingRuleFactory.Create(rule.Match, rule.Decision)));
    }
}
