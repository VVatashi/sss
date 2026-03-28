using Microsoft.Maui.Storage;
using System.Text.Json;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Maui.Services;

public sealed class UiConfigStore
{
    private const string ListenPortKey = "ui.listen_port";
    private const string RemoteHostKey = "ui.remote_host";
    private const string RemotePortKey = "ui.remote_port";
    private const string SharedKeyKey = "ui.shared_key";
    private const string RoutingRulesKey = "ui.routing_rules";
    private const string EnableSocks5AuthenticationKey = "ui.enable_socks5_authentication";
    private const string Socks5UsernameKey = "ui.socks5_username";
    private const string Socks5PasswordKey = "ui.socks5_password";
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
            RoutingRules: LoadRoutingRules(defaults.RoutingRules),
            EnableSocks5Authentication: Preferences.Default.Get(EnableSocks5AuthenticationKey, defaults.EnableSocks5Authentication),
            Socks5Username: Preferences.Default.Get(Socks5UsernameKey, defaults.Socks5Username),
            Socks5Password: Preferences.Default.Get(Socks5PasswordKey, defaults.Socks5Password),
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
        Preferences.Default.Set(RoutingRulesKey, JsonSerializer.Serialize(draft.RoutingRules));
        Preferences.Default.Set(EnableSocks5AuthenticationKey, draft.EnableSocks5Authentication);
        Preferences.Default.Set(Socks5UsernameKey, draft.Socks5Username);
        Preferences.Default.Set(Socks5PasswordKey, draft.Socks5Password);
        Preferences.Default.Set(EnableCompressionKey, draft.EnableCompression);
        Preferences.Default.Set(CompressionAlgorithmKey, draft.CompressionAlgorithm);
        Preferences.Default.Set(CipherAlgorithmKey, draft.CipherAlgorithm);
    }

    public sealed record UiConfigDraft(
        string ListenPortText,
        string RemoteHost,
        string RemotePortText,
        string SharedKey,
        IReadOnlyList<ProxyRoutingRule> RoutingRules,
        bool EnableSocks5Authentication,
        string Socks5Username,
        string Socks5Password,
        bool EnableCompression,
        string CompressionAlgorithm,
        string CipherAlgorithm)
    {
        public static UiConfigDraft FromControls(
            string listenPortText,
            string remoteHost,
            string remotePortText,
            string sharedKey,
            IReadOnlyList<ProxyRoutingRule> routingRules,
            bool enableSocks5Authentication,
            string socks5Username,
            string socks5Password,
            bool enableCompression,
            PayloadCompressionAlgorithm compressionAlgorithm,
            TunnelCipherAlgorithm cipherAlgorithm)
        {
            return new UiConfigDraft(
                listenPortText,
                remoteHost,
                remotePortText,
                sharedKey,
                routingRules,
                enableSocks5Authentication,
                socks5Username,
                socks5Password,
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

    private static List<ProxyRoutingRule> LoadRoutingRules(IReadOnlyList<ProxyRoutingRule> fallback)
    {
        var json = Preferences.Default.Get(RoutingRulesKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback.ToList();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<ProxyRoutingRule>>(json);
            return parsed is { Count: > 0 }
                ? parsed
                : fallback.ToList();
        }
        catch
        {
            return fallback.ToList();
        }
    }
}
