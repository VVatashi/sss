using System.Net;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Protocol;

const int defaultListenPort = 1080;
const string defaultRemoteHost = "127.0.0.1";
const int defaultRemotePort = 8388;

var listenPort = defaultListenPort;
var remoteHost = defaultRemoteHost;
var remotePort = defaultRemotePort;

if (args.Length > 0 && int.TryParse(args[0], out var parsedPort))
{
    listenPort = parsedPort;
}

if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
{
    remoteHost = args[1];
}

if (args.Length > 2 && int.TryParse(args[2], out var parsedRemotePort))
{
    remotePort = parsedRemotePort;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

Console.WriteLine("SimpleShadowsocks.Client");
Console.WriteLine($"SOCKS5 listen: 127.0.0.1:{listenPort}");
Console.WriteLine($"Tunnel server: {remoteHost}:{remotePort}");
Console.WriteLine($"Protocol version: {ProtocolConstants.Version}");
Console.WriteLine("Press Ctrl+C to stop.");

var server = new Socks5Server(IPAddress.Loopback, listenPort, remoteHost, remotePort);
await server.RunAsync(cts.Token);
