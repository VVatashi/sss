using Android.Content;
using Android.OS;
using System.Text.Json;
using SimpleShadowsocks.Client.Maui.Services;

namespace SimpleShadowsocks.Client.Maui;

public static class ProxyForegroundServiceClient
{
    public static void Start(ProxyOptions options)
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(ProxyForegroundService));
        intent.SetAction(ProxyForegroundService.ActionStart);
        intent.PutExtra(ProxyForegroundService.ExtraProxyOptionsJson, JsonSerializer.Serialize(options));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Stop()
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(ProxyForegroundService));
        intent.SetAction(ProxyForegroundService.ActionStop);

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
