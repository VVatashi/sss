namespace SimpleShadowsocks.Protocol.Crypto;

public enum TunnelCipherAlgorithm : byte
{
    ChaCha20Poly1305 = 1,
    Aes256Gcm = 2,
    Aegis128L = 3,
    Aegis256 = 4
}
