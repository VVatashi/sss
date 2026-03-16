namespace SimpleShadowsocks.Client.Socks5;

internal readonly record struct Socks5ConnectRequest(byte Command, string Host, int Port, byte AddressType);
