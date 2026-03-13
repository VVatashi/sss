using SimpleShadowsocks.Protocol;

const int defaultListenPort = 8388;
var listenPort = defaultListenPort;

if (args.Length > 0 && int.TryParse(args[0], out var parsedPort))
{
    listenPort = parsedPort;
}

Console.WriteLine("SimpleShadowsocks.Server");
Console.WriteLine($"Encrypted tunnel listen: 0.0.0.0:{listenPort}");
Console.WriteLine($"Protocol version: {ProtocolConstants.Version}");
Console.WriteLine("TODO: accept client sessions, decrypt frames, and proxy traffic to target hosts.");
