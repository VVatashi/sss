namespace SimpleShadowsocks.Client.Tests;

public sealed partial class Socks5ServerTests
{
    [Fact]
    public async Task Greeting_WithNoAuthMethod_ReturnsNoAuthAccepted()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });

        var response = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, response);
    }

    [Fact]
    public async Task Greeting_WithoutSupportedMethods_ReturnsNoAcceptableMethods()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x02 });

        var response = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0xFF }, response);
    }
}
