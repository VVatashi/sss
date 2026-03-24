using System.Net;
using SimpleShadowsocks.Client.Socks5;

namespace SimpleShadowsocks.Client.Tests.Unit.ClientCore.Socks5;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class TrafficRoutingPolicyTests
{
    [Fact]
    public void Match_WithWildcardRule_MatchesAnyRequest()
    {
        var policy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Any,
                Match = "*",
                Decision = TrafficRouteDecision.Tunnel
            }
        ]);

        var matched = policy.Match(new Socks5ConnectRequest(0x01, "example.org", 443, 0x03));

        Assert.NotNull(matched);
        Assert.Equal(TrafficRouteDecision.Tunnel, matched!.Decision);
    }

    [Fact]
    public void Match_WithSubnetRule_MatchesIpv4LiteralInsideCidr()
    {
        var policy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Subnet,
                Match = "10.0.0.0/8",
                Decision = TrafficRouteDecision.Direct
            }
        ]);

        var matched = policy.Match(new Socks5ConnectRequest(0x01, IPAddress.Parse("10.23.45.67").ToString(), 80, 0x01));

        Assert.NotNull(matched);
        Assert.Equal(TrafficRouteDecision.Direct, matched!.Decision);
    }

    [Fact]
    public void Match_WithHostSuffixRule_MatchesSubdomainCaseInsensitively()
    {
        var policy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Host,
                Match = "*.example.com",
                Decision = TrafficRouteDecision.Direct
            }
        ]);

        var matched = policy.Match(new Socks5ConnectRequest(0x01, "Api.Example.Com", 443, 0x03));

        Assert.NotNull(matched);
        Assert.Equal(TrafficRouteDecision.Direct, matched!.Decision);
    }

    [Fact]
    public void Match_UsesFirstMatchingRule()
    {
        var policy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Any,
                Match = "*",
                Decision = TrafficRouteDecision.Direct
            },
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Host,
                Match = "example.com",
                Decision = TrafficRouteDecision.Tunnel
            }
        ]);

        var matched = policy.Match(new Socks5ConnectRequest(0x01, "example.com", 443, 0x03));

        Assert.NotNull(matched);
        Assert.Equal(TrafficRouteDecision.Direct, matched!.Decision);
    }

    [Fact]
    public void Match_WithoutMatchingRule_ReturnsNull()
    {
        var policy = new TrafficRoutingPolicy(
        [
            new TrafficRoutingRule
            {
                MatchType = TrafficRouteMatchType.Host,
                Match = "*.example.com",
                Decision = TrafficRouteDecision.Tunnel
            }
        ]);

        var matched = policy.Match(new Socks5ConnectRequest(0x01, "contoso.test", 443, 0x03));

        Assert.Null(matched);
    }
}
