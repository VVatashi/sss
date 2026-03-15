using Android.Content;
using Android.App;
using Android.Net;
using Android.OS;
using Microsoft.Maui.ApplicationModel;
using System.Text.Json;
using SimpleShadowsocks.Client.Maui.Services;

namespace SimpleShadowsocks.Client.Maui;

public static class SocksVpnServiceClient
{
    private const int VpnPermissionRequestCode = 40123;
    private static TaskCompletionSource<bool>? _vpnPermissionTcs;

    public static async Task StartAsync(ProxyOptions options, CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("No active Android activity.");

        AppLog.Write("StartAsync requested for VPN.");
        var hasPermission = await EnsureVpnPermissionAsync(activity, cancellationToken);
        if (!hasPermission)
        {
            throw new InvalidOperationException("VPN permission denied.");
        }
        AppLog.Write("VPN permission granted.");

        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(SocksVpnService));
        intent.SetAction(SocksVpnService.ActionStart);
        intent.PutExtra(SocksVpnService.ExtraProxyOptionsJson, JsonSerializer.Serialize(options));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
        AppLog.Write("SocksVpnService start intent sent.");
    }

    internal static void HandleActivityResult(int requestCode, Result resultCode)
    {
        if (requestCode != VpnPermissionRequestCode)
        {
            return;
        }

        var tcs = Interlocked.Exchange(ref _vpnPermissionTcs, null);
        tcs?.TrySetResult(resultCode == Result.Ok);
    }

    private static Task<bool> EnsureVpnPermissionAsync(Activity activity, CancellationToken cancellationToken)
    {
        var prepareIntent = VpnService.Prepare(activity);
        if (prepareIntent is null)
        {
            return Task.FromResult(true);
        }

        if (_vpnPermissionTcs is not null)
        {
            return _vpnPermissionTcs.Task;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _vpnPermissionTcs = tcs;
        AppLog.Write("Requesting VPN permission from system dialog.");
        activity.StartActivityForResult(prepareIntent, VpnPermissionRequestCode);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                var source = Interlocked.Exchange(ref _vpnPermissionTcs, null);
                source?.TrySetCanceled(cancellationToken);
            });
        }

        return tcs.Task;
    }

    public static void Stop()
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(SocksVpnService));
        intent.SetAction(SocksVpnService.ActionStop);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }
}
