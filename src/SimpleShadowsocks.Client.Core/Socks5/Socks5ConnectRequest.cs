namespace SimpleShadowsocks.Client.Socks5;

internal readonly record struct Socks5ConnectRequest(string Host, int Port, byte AddressType);
