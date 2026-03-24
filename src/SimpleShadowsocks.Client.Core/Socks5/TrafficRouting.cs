using System.Net;
using System.Net.Sockets;

namespace SimpleShadowsocks.Client.Socks5;

public enum TrafficRouteDecision
{
    Tunnel = 0,
    Direct = 1,
    Drop = 2
}

public enum TrafficRouteMatchType
{
    Any = 0,
    Host = 1,
    Subnet = 2
}

public sealed class TrafficRoutingRule
{
    public required TrafficRouteMatchType MatchType { get; init; }
    public required string Match { get; init; }
    public required TrafficRouteDecision Decision { get; init; }

    internal bool IsMatch(Socks5ConnectRequest request)
    {
        return MatchType switch
        {
            TrafficRouteMatchType.Any => true,
            TrafficRouteMatchType.Host => IsHostMatch(request.Host),
            TrafficRouteMatchType.Subnet => IsSubnetMatch(request.Host),
            _ => false
        };
    }

    private bool IsHostMatch(string requestHost)
    {
        if (string.IsNullOrWhiteSpace(requestHost))
        {
            return false;
        }

        if (string.Equals(Match, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(requestHost, Match, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Match.StartsWith("*.", StringComparison.Ordinal))
        {
            return requestHost.EndsWith(Match[1..], StringComparison.OrdinalIgnoreCase);
        }

        if (Match.StartsWith(".", StringComparison.Ordinal))
        {
            return requestHost.EndsWith(Match, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool IsSubnetMatch(string requestHost)
    {
        if (!IPAddress.TryParse(requestHost, out var requestAddress))
        {
            return false;
        }

        if (!TryParseCidr(Match, out var networkAddress, out var prefixLength))
        {
            return false;
        }

        if (requestAddress.AddressFamily != networkAddress.AddressFamily)
        {
            return false;
        }

        return IsInSubnet(requestAddress, networkAddress, prefixLength);
    }

    private static bool TryParseCidr(string value, out IPAddress networkAddress, out int prefixLength)
    {
        networkAddress = IPAddress.None;
        prefixLength = 0;

        var separatorIndex = value.IndexOf('/');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            return false;
        }

        var addressPart = value[..separatorIndex];
        var prefixPart = value[(separatorIndex + 1)..];
        if (!IPAddress.TryParse(addressPart, out var parsedNetworkAddress) || !int.TryParse(prefixPart, out prefixLength))
        {
            return false;
        }

        networkAddress = parsedNetworkAddress;

        var maxPrefixLength = networkAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => 0
        };

        return prefixLength >= 0 && prefixLength <= maxPrefixLength;
    }

    private static bool IsInSubnet(IPAddress address, IPAddress networkAddress, int prefixLength)
    {
        var addressBytes = address.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = 0xFF << (8 - remainingBits);
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}

public sealed class TrafficRoutingPolicy
{
    private readonly IReadOnlyList<TrafficRoutingRule> _rules;

    public TrafficRoutingPolicy(IEnumerable<TrafficRoutingRule> rules)
    {
        _rules = rules?.ToArray() ?? [];
    }

    public IReadOnlyList<TrafficRoutingRule> Rules => _rules;

    internal TrafficRoutingRule? Match(Socks5ConnectRequest request)
    {
        foreach (var rule in _rules)
        {
            if (rule.IsMatch(request))
            {
                return rule;
            }
        }

        return null;
    }
}
