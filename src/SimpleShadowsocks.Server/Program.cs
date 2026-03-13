using System.Net;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Server.Tunnel;

const int defaultListenPort = 8388;
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

Console.WriteLine("SimpleShadowsocks.Server");
Console.WriteLine($"Tunnel listen: 0.0.0.0:{listenPort}");
Console.WriteLine($"Protocol version: {ProtocolConstants.Version}");
Console.WriteLine("Press Ctrl+C to stop.");

var server = new TunnelServer(IPAddress.Any, listenPort);
await server.RunAsync(cts.Token);
