using System.Collections.Concurrent;

namespace SimpleShadowsocks.Protocol.Crypto;

internal static class ReplayProtectionCache
{
    private const int MaxEntries = 200_000;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, long> Seen = new();
    private static long _nextCleanupUnixSeconds;

    public static bool TryRegister(ReadOnlySpan<byte> clientNonce, ulong handshakeCounter, int replayWindowSeconds)
    {
        CleanupExpiredIfNeeded();

        var keyBytes = new byte[clientNonce.Length + sizeof(ulong)];
        clientNonce.CopyTo(keyBytes);
        BitConverter.GetBytes(handshakeCounter).CopyTo(keyBytes, clientNonce.Length);
        var key = Convert.ToBase64String(keyBytes);

        if (Seen.Count >= MaxEntries)
        {
            CleanupExpired();
            if (Seen.Count >= MaxEntries)
            {
                return false;
            }
        }

        var expiresAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + replayWindowSeconds;
        return Seen.TryAdd(key, expiresAtUnixSeconds);
    }

    private static void CleanupExpiredIfNeeded()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < Interlocked.Read(ref _nextCleanupUnixSeconds))
        {
            return;
        }

        if (Interlocked.Exchange(ref _nextCleanupUnixSeconds, now + (long)CleanupInterval.TotalSeconds) > now)
        {
            return;
        }

        CleanupExpired();
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
