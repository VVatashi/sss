using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using System.Text.Json;
using SimpleShadowsocks.Client.Maui.Services;

namespace SimpleShadowsocks.Client.Maui;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class ProxyForegroundService : Service
{
    private const string ChannelId = "ss_proxy_channel";
    private const int NotificationId = 10101;
    public const string ActionStart = "com.simpleshadowsocks.client.maui.action.START_PROXY";
    public const string ActionStop = "com.simpleshadowsocks.client.maui.action.STOP_PROXY";
    public const string ExtraProxyOptionsJson = "proxy_options_json";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureNotificationChannel();
        StartForeground(NotificationId, BuildNotification("Proxy service is starting..."));

        var action = intent?.Action;
        if (string.Equals(action, ActionStop, StringComparison.Ordinal))
        {
            _ = StopProxyAndServiceAsync();
            return StartCommandResult.NotSticky;
        }

        if (string.Equals(action, ActionStart, StringComparison.Ordinal))
        {
            var json = intent?.GetStringExtra(ExtraProxyOptionsJson);
            _ = StartProxyAsync(json);
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _ = ProxyRuntime.Runner.StopAsync();
        base.OnDestroy();
    }

    private async Task StartProxyAsync(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            ProxyRuntime.Runner.EmitStatus("Failed to start proxy: missing options.");
            await StopProxyAndServiceAsync();
            return;
        }

        try
        {
            var options = JsonSerializer.Deserialize<ProxyOptions>(optionsJson);
            if (options is null)
            {
                ProxyRuntime.Runner.EmitStatus("Failed to start proxy: invalid options.");
                await StopProxyAndServiceAsync();
                return;
            }

            await ProxyRuntime.Runner.StartAsync(options, CancellationToken.None);
            UpdateNotification("Proxy is running in background.");
        }
        catch (Exception ex)
        {
            ProxyRuntime.Runner.EmitStatus($"Failed to start proxy service: {ex.Message}");
            await StopProxyAndServiceAsync();
        }
    }

    private async Task StopProxyAndServiceAsync()
    {
        try
        {
            await ProxyRuntime.Runner.StopAsync();
        }
        finally
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        var channel = new NotificationChannel(
            ChannelId,
            "SimpleShadowsocks Proxy",
            NotificationImportance.Low)
        {
            Description = "Foreground service for SimpleShadowsocks SOCKS5 proxy"
        };
        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string text)
    {
        var openAppIntent = new Intent(this, typeof(MainActivity));
        openAppIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            openAppIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
            ?? throw new InvalidOperationException("Failed to create notification PendingIntent.");

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("SimpleShadowsocks Proxy");
        builder.SetContentText(text);
        builder.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo);
        builder.SetContentIntent(pendingIntent);
        builder.SetOngoing(true);

        return builder.Build() ?? throw new InvalidOperationException("Failed to build proxy notification.");
    }

    private void UpdateNotification(string text)
    {
        var manager = NotificationManagerCompat.From(this)
            ?? throw new InvalidOperationException("Notification manager is unavailable.");
        manager.Notify(NotificationId, BuildNotification(text));
    }
}
