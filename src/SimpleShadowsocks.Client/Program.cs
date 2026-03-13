using System.Net;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Protocol;

const int defaultListenPort = 1080;
var listenPort = defaultListenPort;

if (args.Length > 0 && int.TryParse(args[0], out var parsedPort))
{
    listenPort = parsedPort;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

Console.WriteLine("SimpleShadowsocks.Client");
Console.WriteLine($"SOCKS5 listen: 127.0.0.1:{listenPort}");
Console.WriteLine($"Protocol version: {ProtocolConstants.Version}");
Console.WriteLine("Press Ctrl+C to stop.");

var server = new Socks5Server(IPAddress.Loopback, listenPort);
await server.RunAsync(cts.Token);
