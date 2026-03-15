using System.Net;
using System.Text;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class Socks5ServerTests
{
    [Fact]
    public async Task ConnectCommand_ToReachableTarget_EnablesDataRelay()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x00, connectResponse.ReplyCode);

        var payload = Encoding.ASCII.GetBytes("ping-through-socks");
        await stream.WriteAsync(payload);
        var echoed = await TestNetwork.ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task ConnectCommand_ToUnreachableTarget_ReturnsHostUnreachable()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var closedPort = TestNetwork.AllocateUnusedPort();
        var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, closedPort);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x05, connectResponse.ReplyCode);
    }

    [Fact]
    public async Task UnsupportedCommand_ReturnsCommandNotSupported()
    {
        await using var socks = await TestNetwork.StartStandaloneSocksServerAsync();
        using var tcpClient = await TestNetwork.ConnectAsync(socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greetingResponse);

        var bindRequest = TestNetwork.BuildBindRequestIPv4(IPAddress.Loopback, 80);
        await stream.WriteAsync(bindRequest);
        var response = await TestNetwork.ReadSocks5ReplyAsync(stream);
        Assert.Equal((byte)0x07, response.ReplyCode);
    }
}
