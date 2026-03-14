namespace SimpleShadowsocks.Protocol.Crypto;

public sealed class TunnelCryptoPolicy
{
    public static TunnelCryptoPolicy Default { get; } = new();

    // Allowed absolute clock difference between peers.
    public int HandshakeMaxClockSkewSeconds { get; init; } = 60;

    // Replay cache lifetime for seen client handshake identifiers.
    public int ReplayWindowSeconds { get; init; } = 300;

    // Preferred transport AEAD algorithm for client side. Server supports all known algorithms.
    public TunnelCipherAlgorithm PreferredAlgorithm { get; init; } = TunnelCipherAlgorithm.ChaCha20Poly1305;
}
