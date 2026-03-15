using System.Runtime.InteropServices;
using System.Text;
using SimpleShadowsocks.Client.Maui.Services;

namespace SimpleShadowsocks.Client.Maui;

public sealed class HevSocks5TunnelProcess : IDisposable
{
    private readonly object _sync = new();
    private Task<int>? _runTask;
    private bool _started;

    public void Start(int tunFileDescriptor, int socksPort, int dnsPort)
    {
        lock (_sync)
        {
            if (_started)
            {
                throw new InvalidOperationException("hev-socks5-tunnel is already started.");
            }

            HevSocks5TunnelNative.EnsureLoaded();
            var config = BuildConfig(socksPort, dnsPort);
            var configBytes = Encoding.UTF8.GetBytes(config);

            _runTask = Task.Run(() =>
            {
                AppLog.Write("Starting hev-socks5-tunnel.");
                return HevSocks5TunnelNative.RunFromString(configBytes, tunFileDescriptor);
            });
            _started = true;
        }
    }

    public void Dispose()
    {
        Task<int>? runTask;

        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            runTask = _runTask;
            _runTask = null;
            _started = false;
        }

        try
        {
            HevSocks5TunnelNative.Quit();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Failed to stop hev-socks5-tunnel: {ex.Message}");
        }

        if (runTask is null)
        {
            return;
        }

        try
        {
            if (!runTask.Wait(TimeSpan.FromSeconds(5)))
            {
                AppLog.Write("hev-socks5-tunnel did not stop within timeout.");
                return;
            }

            var exitCode = runTask.GetAwaiter().GetResult();
            AppLog.Write($"hev-socks5-tunnel exited with code {exitCode}.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"hev-socks5-tunnel wait failed: {ex.Message}");
        }
    }

    private static string BuildConfig(int socksPort, int dnsPort)
    {
        return
            """
            tunnel:
              mtu: 1500
              ipv4: 198.18.0.2
            socks5:
              address: 127.0.0.1
              port: __SOCKS_PORT__
            mapdns:
              address: 198.18.0.1
              port: __DNS_PORT__
              network: 100.64.0.0
              netmask: 255.192.0.0
              cache-size: 10000
            misc:
              log-file: stderr
              log-level: warn
            """
            .Replace("__SOCKS_PORT__", socksPort.ToString(), StringComparison.Ordinal)
            .Replace("__DNS_PORT__", dnsPort.ToString(), StringComparison.Ordinal);
    }

    private static class HevSocks5TunnelNative
    {
        private static readonly object LoadSync = new();
        private static nint _libraryHandle;
        private static HevSocks5TunnelMainFromStringDelegate? _mainFromString;
        private static HevSocks5TunnelQuitDelegate? _quit;

        public static void EnsureLoaded()
        {
            if (_libraryHandle != 0)
            {
                return;
            }

            lock (LoadSync)
            {
                if (_libraryHandle != 0)
                {
                    return;
                }

                var libraryPath = ResolveLibraryPath()
                    ?? throw new DllNotFoundException(
                        "hev-socks5-tunnel native library is missing. " +
                        "Expected packaged native library: lib/<abi>/libhev-socks5-tunnel.so");
                _libraryHandle = NativeLibrary.Load(libraryPath);
                _mainFromString = ResolveExport<HevSocks5TunnelMainFromStringDelegate>("hev_socks5_tunnel_main_from_str");
                _quit = ResolveExport<HevSocks5TunnelQuitDelegate>("hev_socks5_tunnel_quit");
                AppLog.Write($"Loaded hev-socks5-tunnel library: {libraryPath}");
            }
        }

        public static int RunFromString(byte[] configBytes, int tunFileDescriptor)
        {
            var main = _mainFromString
                ?? throw new InvalidOperationException("hev-socks5-tunnel main entry point is not loaded.");
            return main(configBytes, (uint)configBytes.Length, tunFileDescriptor);
        }

        public static void Quit()
        {
            var quit = _quit
                ?? throw new InvalidOperationException("hev-socks5-tunnel quit entry point is not loaded.");
            quit();
        }

        private static T ResolveExport<T>(string symbol)
            where T : Delegate
        {
            var export = NativeLibrary.GetExport(_libraryHandle, symbol);
            return Marshal.GetDelegateForFunctionPointer<T>(export);
        }

        private static string? ResolveLibraryPath()
        {
            var context = Android.App.Application.Context;
            var nativeDir = context.ApplicationInfo?.NativeLibraryDir;
            if (string.IsNullOrWhiteSpace(nativeDir))
            {
                return null;
            }

            var path = Path.Combine(nativeDir, "libhev-socks5-tunnel.so");
            return File.Exists(path) ? path : null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int HevSocks5TunnelMainFromStringDelegate(byte[] config, uint configLength, int tunFileDescriptor);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void HevSocks5TunnelQuitDelegate();
    }
}
