using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class TunnelIntegrationTests
{
    [Fact]
    public async Task Socks5Client_UsesProtocolTunnel_ToReachTarget()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
        await stream.WriteAsync(connectRequest);

        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var payload = Encoding.ASCII.GetBytes("hello-over-tunnel");
        await stream.WriteAsync(payload);
        var echoed = await TestNetwork.ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Socks5Client_UsesProtocolTunnel_WithAes256Gcm()
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(
            tunnel.Port,
            connectionPolicy: null,
            cryptoPolicy: new TunnelCryptoPolicy
            {
                HandshakeMaxClockSkewSeconds = TunnelCryptoPolicy.Default.HandshakeMaxClockSkewSeconds,
                ReplayWindowSeconds = TunnelCryptoPolicy.Default.ReplayWindowSeconds,
                PreferredAlgorithm = TunnelCipherAlgorithm.Aes256Gcm
            });

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var payload = Encoding.ASCII.GetBytes("hello-over-aesgcm-tunnel");
        await stream.WriteAsync(payload);
        var echoed = await TestNetwork.ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Socks5Client_UsesProtocolTunnel_WithAegis128L()
    {
        if (!AeadDuplexStream.IsSupported(TunnelCipherAlgorithm.Aegis128L))
        {
            return;
        }

        await RunSingleCipherRoundTripAsync(TunnelCipherAlgorithm.Aegis128L, "hello-over-aegis128l-tunnel");
    }

    [Fact]
    public async Task Socks5Client_UsesProtocolTunnel_WithAegis256()
    {
        if (!AeadDuplexStream.IsSupported(TunnelCipherAlgorithm.Aegis256))
        {
            return;
        }

        await RunSingleCipherRoundTripAsync(TunnelCipherAlgorithm.Aegis256, "hello-over-aegis256-tunnel");
    }

    private static async Task RunSingleCipherRoundTripAsync(TunnelCipherAlgorithm algorithm, string payloadText)
    {
        await using var echo = await TestNetwork.StartEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(
            tunnel.Port,
            connectionPolicy: null,
            cryptoPolicy: new TunnelCryptoPolicy
            {
                HandshakeMaxClockSkewSeconds = TunnelCryptoPolicy.Default.HandshakeMaxClockSkewSeconds,
                ReplayWindowSeconds = TunnelCryptoPolicy.Default.ReplayWindowSeconds,
                PreferredAlgorithm = algorithm
            });

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socks.Port);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var connectRequest = TestNetwork.BuildConnectRequestIPv4(IPAddress.Loopback, echo.Port);
        await stream.WriteAsync(connectRequest);
        var connectResponse = await TestNetwork.ReadExactAsync(stream, 10);
        Assert.Equal((byte)0x00, connectResponse[1]);

        var payload = Encoding.ASCII.GetBytes(payloadText);
        await stream.WriteAsync(payload);
        var echoed = await TestNetwork.ReadExactAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);
    }
}
