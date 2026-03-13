using SimpleShadowsocks.Protocol;

const int defaultListenPort = 1080;
var listenPort = defaultListenPort;

if (args.Length > 0 && int.TryParse(args[0], out var parsedPort))
{
    listenPort = parsedPort;
}

Console.WriteLine("SimpleShadowsocks.Client");
Console.WriteLine($"SOCKS5 listen: 127.0.0.1:{listenPort}");
Console.WriteLine($"Protocol version: {ProtocolConstants.Version}");
Console.WriteLine("TODO: implement SOCKS5 handshake and TCP relay to remote server.");
