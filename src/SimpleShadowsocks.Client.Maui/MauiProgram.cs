using SimpleShadowsocks.Client.Maui.Services;

namespace SimpleShadowsocks.Client.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        AppLog.Initialize();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.Services.AddSingleton(_ => ProxyRuntime.Runner);
        builder.Services.AddSingleton<UiConfigStore>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
