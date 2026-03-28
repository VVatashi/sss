using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using System.Text.Json;
using SimpleShadowsocks.Client.Maui.Services;
using Socket = System.Net.Sockets.Socket;

namespace SimpleShadowsocks.Client.Maui;

[Service(
    Name = "com.simpleshadowsocks.client.maui.SocksVpnService",
    Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = true,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class SocksVpnService : VpnService
{
    private const string ChannelId = "ss_vpn_channel";
    private const int NotificationId = 20202;
    public const string ActionStart = "com.simpleshadowsocks.client.maui.action.START_VPN";
    public const string ActionStop = "com.simpleshadowsocks.client.maui.action.STOP_VPN";
    public const string ExtraProxyOptionsJson = "proxy_options_json";

    private ParcelFileDescriptor? _tunInterface;
    private int _tunFd = -1;
    private HevSocks5TunnelProcess? _hevSocks5Tunnel;
    private bool _running;

    public override IBinder? OnBind(Intent? intent) => base.OnBind(intent);

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureNotificationChannel();
        StartForeground(NotificationId, BuildNotification("VPN service is starting..."));
        AppLog.Write("SocksVpnService OnStartCommand.");

        var action = intent?.Action;
        if (string.Equals(action, ActionStop, StringComparison.Ordinal))
        {
            AppLog.Write("SocksVpnService stop requested.");
            _ = StopVpnAsync();
            return StartCommandResult.NotSticky;
        }

        if (string.Equals(action, ActionStart, StringComparison.Ordinal))
        {
            AppLog.Write("SocksVpnService start requested.");
            var json = intent?.GetStringExtra(ExtraProxyOptionsJson);
            _ = StartVpnAsync(json);
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        AppLog.Write("SocksVpnService OnDestroy.");
        _ = StopVpnAsync();
        base.OnDestroy();
    }

    private async Task StartVpnAsync(string? optionsJson)
    {
        if (_running)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(optionsJson))
            {
                throw new InvalidOperationException("Missing VPN options.");
            }

            var options = JsonSerializer.Deserialize<ProxyOptions>(optionsJson)
                ?? throw new InvalidOperationException("Invalid VPN options payload.");
            AppLog.Write($"VPN options parsed. SOCKS={options.ListenPort}, tunnel={options.RemoteHost}:{options.RemotePort}.");

            await ProxyRuntime.Runner.StartAsync(options, CancellationToken.None, ProtectTunnelSocket);
            AppLog.Write("Local SOCKS5 proxy started.");

            var builder = new Builder(this)
                .SetSession("SimpleShadowsocks TCP VPN")
                .SetMtu(1500)
                .AddAddress("198.18.0.2", 15)
                .AddDnsServer("198.18.0.1");
            ConfigureRoutes(builder);

            _tunInterface = builder.Establish()
                ?? throw new InvalidOperationException("Failed to establish VPN interface.");
            _tunFd = _tunInterface.DetachFd();
            _tunInterface.Dispose();
            _tunInterface = null;
            AppLog.Write($"TUN interface established. Detached fd={_tunFd}.");

            _hevSocks5Tunnel = new HevSocks5TunnelProcess();
            _hevSocks5Tunnel.Start(
                _tunFd,
                options.ListenPort,
                53,
                options.EnableSocks5Authentication,
                options.Socks5Username,
                options.Socks5Password);
            AppLog.Write("hev-socks5-tunnel started.");

            _running = true;
            ProxyRuntime.Runner.EmitStatus("VPN is running (TCP via hev-socks5-tunnel, DNS via mapdns).");
            UpdateNotification("VPN is active.");
        }
        catch (Exception ex)
        {
            ProxyRuntime.Runner.EmitStatus($"Failed to start VPN: {ex.Message}");
            AppLog.Write($"VPN startup exception: {ex}");
            await StopVpnAsync();
        }
    }

    private async Task StopVpnAsync()
    {
        if (!_running && _tunInterface is null && _tunFd < 0 && _hevSocks5Tunnel is null)
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return;
        }

        _running = false;
        AppLog.Write("Stopping VPN components.");

        if (_hevSocks5Tunnel is not null)
        {
            _hevSocks5Tunnel.Dispose();
            _hevSocks5Tunnel = null;
            AppLog.Write("hev-socks5-tunnel stopped.");
        }

        if (_tunInterface is not null)
        {
            _tunInterface.Close();
            _tunInterface.Dispose();
            _tunInterface = null;
            AppLog.Write("TUN interface closed.");
        }

        if (_tunFd >= 0)
        {
            try
            {
                using var pfd = ParcelFileDescriptor.AdoptFd(_tunFd);
                if (pfd is not null)
                {
                    pfd.Close();
                }
            }
            catch (System.Exception ex)
            {
                AppLog.Write($"Failed to close detached TUN fd {_tunFd}: {ex.Message}");
            }
            finally
            {
                _tunFd = -1;
            }
        }

        await ProxyRuntime.Runner.StopAsync();
        ProxyRuntime.Runner.EmitStatus("VPN stopped.");

        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
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
            "SimpleShadowsocks VPN",
            NotificationImportance.Low)
        {
            Description = "Foreground VPN service for SimpleShadowsocks"
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
        builder.SetContentTitle("SimpleShadowsocks VPN");
        builder.SetContentText(text);
        builder.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo);
        builder.SetContentIntent(pendingIntent);
        builder.SetOngoing(true);

        return builder.Build() ?? throw new InvalidOperationException("Failed to build VPN notification.");
    }

    private void UpdateNotification(string text)
    {
        var manager = NotificationManagerCompat.From(this)
            ?? throw new InvalidOperationException("Notification manager is unavailable.");
        manager.Notify(NotificationId, BuildNotification(text));
    }

    private void ConfigureRoutes(Builder builder)
    {
        builder.AddRoute("0.0.0.0", 0);
        builder.AddRoute("198.18.0.0", 15);
        builder.AddRoute("100.64.0.0", 10);
    }

    private void ProtectTunnelSocket(Socket socket)
    {
        var socketHandle = socket.Handle;
        var fd = checked((int)socketHandle);
        if (!Protect(fd))
        {
            throw new InvalidOperationException(
                $"VpnService.Protect failed for tunnel socket fd={fd}.");
        }
    }

}
