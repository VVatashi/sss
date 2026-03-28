namespace SimpleShadowsocks.Client.Socks5;

public static class TrafficRoutingRuleFactory
{
    public static TrafficRoutingRule Create(
        string match,
        TrafficRouteDecision decision,
        TrafficRouteMatchType? matchType = null)
    {
        var normalizedMatch = match?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedMatch))
        {
            throw new InvalidDataException("Routing rule match must not be empty.");
        }

        return new TrafficRoutingRule
        {
            MatchType = matchType ?? InferMatchType(normalizedMatch),
            Match = normalizedMatch,
            Decision = decision
        };
    }

    public static TrafficRouteMatchType InferMatchType(string match)
    {
        var normalizedMatch = match?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedMatch))
        {
            throw new InvalidDataException("Routing rule match must not be empty.");
        }

        if (string.Equals(normalizedMatch, "*", StringComparison.Ordinal))
        {
            return TrafficRouteMatchType.Any;
        }

        return normalizedMatch.Contains('/', StringComparison.Ordinal)
            ? TrafficRouteMatchType.Subnet
            : TrafficRouteMatchType.Host;
    }
}
