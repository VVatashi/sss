using Microsoft.Maui.Storage;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Maui.Services;

public sealed class UiConfigStore
{
    private const string ListenPortKey = "ui.listen_port";
    private const string RemoteHostKey = "ui.remote_host";
    private const string RemotePortKey = "ui.remote_port";
    private const string SharedKeyKey = "ui.shared_key";
    private const string EnableCompressionKey = "ui.enable_compression";
    private const string CompressionAlgorithmKey = "ui.compression_algorithm";
    private const string CipherAlgorithmKey = "ui.cipher_algorithm";

    public UiConfigDraft Load()
    {
        var defaults = ProxyOptions.CreateDefaults();
        return new UiConfigDraft(
            ListenPortText: Preferences.Default.Get(ListenPortKey, defaults.ListenPort.ToString()),
            RemoteHost: Preferences.Default.Get(RemoteHostKey, defaults.RemoteHost),
            RemotePortText: Preferences.Default.Get(RemotePortKey, defaults.RemotePort.ToString()),
            SharedKey: Preferences.Default.Get(SharedKeyKey, defaults.SharedKey),
            EnableCompression: Preferences.Default.Get(EnableCompressionKey, defaults.EnableCompression),
            CompressionAlgorithm: Preferences.Default.Get(CompressionAlgorithmKey, defaults.CompressionAlgorithm.ToString()),
            CipherAlgorithm: Preferences.Default.Get(CipherAlgorithmKey, defaults.TunnelCipherAlgorithm.ToString()));
    }

    public void Save(UiConfigDraft draft)
    {
        Preferences.Default.Set(ListenPortKey, draft.ListenPortText);
        Preferences.Default.Set(RemoteHostKey, draft.RemoteHost);
        Preferences.Default.Set(RemotePortKey, draft.RemotePortText);
        Preferences.Default.Set(SharedKeyKey, draft.SharedKey);
        Preferences.Default.Set(EnableCompressionKey, draft.EnableCompression);
        Preferences.Default.Set(CompressionAlgorithmKey, draft.CompressionAlgorithm);
        Preferences.Default.Set(CipherAlgorithmKey, draft.CipherAlgorithm);
    }

    public sealed record UiConfigDraft(
        string ListenPortText,
        string RemoteHost,
        string RemotePortText,
        string SharedKey,
        bool EnableCompression,
        string CompressionAlgorithm,
        string CipherAlgorithm)
    {
        public static UiConfigDraft FromControls(
            string listenPortText,
            string remoteHost,
            string remotePortText,
            string sharedKey,
            bool enableCompression,
            PayloadCompressionAlgorithm compressionAlgorithm,
            TunnelCipherAlgorithm cipherAlgorithm)
        {
            return new UiConfigDraft(
                listenPortText,
                remoteHost,
                remotePortText,
                sharedKey,
                enableCompression,
                compressionAlgorithm.ToString(),
                cipherAlgorithm.ToString());
        }

        public PayloadCompressionAlgorithm ResolveCompressionAlgorithm(PayloadCompressionAlgorithm fallback)
        {
            return Enum.TryParse<PayloadCompressionAlgorithm>(CompressionAlgorithm, true, out var parsed)
                ? parsed
                : fallback;
        }

        public TunnelCipherAlgorithm ResolveCipherAlgorithm(TunnelCipherAlgorithm fallback)
        {
            return Enum.TryParse<TunnelCipherAlgorithm>(CipherAlgorithm, true, out var parsed)
                ? parsed
                : fallback;
        }
    }
}
