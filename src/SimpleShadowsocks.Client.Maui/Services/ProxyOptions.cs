using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Maui.Services;

public sealed record ProxyOptions(
    int ListenPort,
    string RemoteHost,
    int RemotePort,
    string SharedKey,
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
            ProtocolVersion: ProtocolConstants.Version,
            EnableCompression: false,
            CompressionAlgorithm: PayloadCompressionAlgorithm.Deflate,
            TunnelCipherAlgorithm: TunnelCipherAlgorithm.ChaCha20Poly1305);
    }
}
