using SimpleShadowsocks.Client.Socks5;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class Socks5ServerTests
{
    [Fact]
    public async Task Greeting_WithNoAuthMethod_ReturnsNoAuthAccepted()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        var response = await TestNetwork.SendSocks5GreetingAsync(stream, 0x00);
        Assert.Equal(new byte[] { 0x05, 0x00 }, response);
    }

    [Fact]
    public async Task Greeting_WithoutSupportedMethods_ReturnsNoAcceptableMethods()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        var response = await TestNetwork.SendSocks5GreetingAsync(stream, 0x02);
        Assert.Equal(new byte[] { 0x05, 0xFF }, response);
    }

    [Fact]
    public async Task Greeting_WithUsernamePasswordAuthenticationEnabled_SelectsAuthMethodAndAcceptsValidCredentials()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync(
            authenticationOptions: new Socks5AuthenticationOptions("local-user", "local-pass"));
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        var greeting = await TestNetwork.SendSocks5GreetingAsync(stream, 0x00, 0x02);
        Assert.Equal(new byte[] { 0x05, 0x02 }, greeting);

        var authResponse = await TestNetwork.SendUsernamePasswordAuthAsync(stream, "local-user", "local-pass");
        Assert.Equal(new byte[] { 0x01, 0x00 }, authResponse);
    }

    [Fact]
    public async Task Greeting_WithUsernamePasswordAuthenticationEnabled_RejectsInvalidCredentials()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync(
            authenticationOptions: new Socks5AuthenticationOptions("local-user", "local-pass"));
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        var greeting = await TestNetwork.SendSocks5GreetingAsync(stream, 0x02);
        Assert.Equal(new byte[] { 0x05, 0x02 }, greeting);

        var authResponse = await TestNetwork.SendUsernamePasswordAuthAsync(stream, "local-user", "wrong-pass");
        Assert.Equal(new byte[] { 0x01, 0x01 }, authResponse);
    }
}
