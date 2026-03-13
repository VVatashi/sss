namespace SimpleShadowsocks.Protocol.Crypto;

public sealed class TunnelCryptoPolicy
{
    public static TunnelCryptoPolicy Default { get; } = new();

    // Allowed absolute clock difference between peers.
    public int HandshakeMaxClockSkewSeconds { get; init; } = 60;

    // Replay cache lifetime for seen client handshake identifiers.
    public int ReplayWindowSeconds { get; init; } = 300;
}
