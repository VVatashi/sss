using System.Collections.Concurrent;

namespace SimpleShadowsocks.Protocol.Crypto;

internal static class ReplayProtectionCache
{
    private static readonly ConcurrentDictionary<string, long> Seen = new();

    public static bool TryRegister(ReadOnlySpan<byte> clientNonce, ulong handshakeCounter, int replayWindowSeconds)
    {
        CleanupExpired();

        var keyBytes = new byte[clientNonce.Length + sizeof(ulong)];
        clientNonce.CopyTo(keyBytes);
        BitConverter.GetBytes(handshakeCounter).CopyTo(keyBytes, clientNonce.Length);
        var key = Convert.ToBase64String(keyBytes);

        var expiresAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + replayWindowSeconds;
        return Seen.TryAdd(key, expiresAtUnixSeconds);
    }

    private static void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var item in Seen)
        {
            if (item.Value < now)
            {
                Seen.TryRemove(item.Key, out _);
            }
        }
    }
}
