namespace SimpleShadowsocks.Client.Tunnel;

public sealed class TunnelConnectionPolicy
{
    public static TunnelConnectionPolicy Default { get; } = new();

    public int HeartbeatIntervalSeconds { get; init; } = 10;
    public int IdleTimeoutSeconds { get; init; } = 45;
    public int ReconnectBaseDelayMs { get; init; } = 200;
    public int ReconnectMaxDelayMs { get; init; } = 2000;
    public int ReconnectMaxAttempts { get; init; } = 12;
    public int MaxConcurrentSessions { get; init; } = 1024;
    public int SessionReceiveChannelCapacity { get; init; } = 256;
}
