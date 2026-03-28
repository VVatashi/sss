using SimpleShadowsocks.Client.Socks5;

namespace SimpleShadowsocks.Client.Tests.Unit.ClientCore.Socks5;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class TrafficRoutingRuleFactoryTests
{
    [Fact]
    public void Create_WithWildcard_InfersAny()
    {
        var rule = TrafficRoutingRuleFactory.Create("*", TrafficRouteDecision.Tunnel);

        Assert.Equal(TrafficRouteMatchType.Any, rule.MatchType);
        Assert.Equal("*", rule.Match);
        Assert.Equal(TrafficRouteDecision.Tunnel, rule.Decision);
    }

    [Fact]
    public void Create_WithCidr_InfersSubnet()
    {
        var rule = TrafficRoutingRuleFactory.Create("10.0.0.0/8", TrafficRouteDecision.Direct);

        Assert.Equal(TrafficRouteMatchType.Subnet, rule.MatchType);
    }

    [Fact]
    public void Create_WithHost_InfersHost()
    {
        var rule = TrafficRoutingRuleFactory.Create("*.example.com", TrafficRouteDecision.Drop);

        Assert.Equal(TrafficRouteMatchType.Host, rule.MatchType);
    }

    [Fact]
    public void Create_WithExplicitType_UsesExplicitType()
    {
        var rule = TrafficRoutingRuleFactory.Create("10.0.0.0/8", TrafficRouteDecision.Direct, TrafficRouteMatchType.Host);

        Assert.Equal(TrafficRouteMatchType.Host, rule.MatchType);
    }

    [Fact]
    public void Create_WithEmptyMatch_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TrafficRoutingRuleFactory.Create(" ", TrafficRouteDecision.Tunnel));

        Assert.Contains("must not be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
