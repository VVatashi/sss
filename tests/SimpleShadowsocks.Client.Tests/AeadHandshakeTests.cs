using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tests;

public sealed class AeadHandshakeTests
{
    [Fact]
    public async Task Aegis128_Handshake_And_Record_RoundTrip_Works()
    {
        if (!AeadDuplexStream.IsSupported(TunnelCipherAlgorithm.Aegis128L))
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverSecure = await TunnelCryptoHandshake.AsServerAsync(
                serverClient.GetStream(),
                PreSharedKey.Derive32Bytes("dev-shared-key"),
                new TunnelCryptoPolicy { PreferredAlgorithm = TunnelCipherAlgorithm.Aegis128L },
                cts.Token);

            var incoming = new byte[5];
            var read = 0;
            while (read < incoming.Length)
            {
                read += await serverSecure.ReadAsync(incoming.AsMemory(read), cts.Token);
            }

            await serverSecure.WriteAsync(incoming, cts.Token);
            await serverSecure.FlushAsync(cts.Token);
        }, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var clientSecure = await TunnelCryptoHandshake.AsClientAsync(
            client.GetStream(),
            PreSharedKey.Derive32Bytes("dev-shared-key"),
            new TunnelCryptoPolicy { PreferredAlgorithm = TunnelCipherAlgorithm.Aegis128L },
            cts.Token);

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await clientSecure.WriteAsync(payload, cts.Token);
        await clientSecure.FlushAsync(cts.Token);

        var echoed = new byte[5];
        var echoedRead = 0;
        while (echoedRead < echoed.Length)
        {
            echoedRead += await clientSecure.ReadAsync(echoed.AsMemory(echoedRead), cts.Token);
        }

        Assert.Equal(payload, echoed);
        await serverTask;
    }
}
