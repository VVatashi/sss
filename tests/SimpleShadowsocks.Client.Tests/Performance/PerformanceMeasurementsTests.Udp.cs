using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class PerformanceMeasurementsTests
{
    [Fact]
    public async Task Measure_UdpAssociate_Throughput_ViaTunnel()
    {
        var payloadBytes = GetPerfIntOverride("SS_PERF_UDP_PAYLOAD_BYTES", 1024);
        var packetCount = GetPerfIntOverride("SS_PERF_UDP_PACKET_COUNT", 3000);

        await using var udpEcho = await TestNetwork.StartUdpEchoServerAsync();
        await using var tunnel = await TestNetwork.StartTunnelServerAsync();
        await using var socks = await TestNetwork.StartSocksServerAsync(tunnel.Port);

        var result = await MeasureUdpModeAsync(
            mode: "udp-tunnel",
            socksPort: socks.Port,
            udpEchoPort: udpEcho.Port,
            payloadBytes: payloadBytes,
            packetCount: packetCount,
            timeout: TimeSpan.FromSeconds(180));

        _output.WriteLine(result.ToString());
        Assert.True(result.ThroughputMibPerSec > 0.5, $"Unexpectedly low UDP throughput via tunnel: {result.ThroughputMibPerSec:F2} MiB/s");
    }

    private async Task<UdpPerfResult> MeasureUdpModeAsync(
        string mode,
        int socksPort,
        int udpEchoPort,
        int payloadBytes,
        int packetCount,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort, timeoutCts.Token);
        using var stream = tcpClient.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, timeoutCts.Token);
        var greeting = await TestNetwork.ReadExactAsync(stream, 2);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 greeting failed in {mode}.");
        }

        var udpAssociateRequest = TestNetwork.BuildUdpAssociateRequestIPv4(IPAddress.Any, 0);
        await stream.WriteAsync(udpAssociateRequest, timeoutCts.Token);
        var associateReply = await TestNetwork.ReadSocks5ReplyAsync(stream);
        if (associateReply.ReplyCode != 0x00 || associateReply.BoundEndPoint is null)
        {
            throw new InvalidOperationException($"SOCKS5 UDP ASSOCIATE failed in {mode} (reply={associateReply.ReplyCode}).");
        }

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = BuildUdpPayload(payloadBytes);

        for (var i = 0; i < 200; i++)
        {
            var warmupPacket = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEchoPort, payload);
            await udpClient.SendAsync(warmupPacket, associateReply.BoundEndPoint, timeoutCts.Token);
            var warmupEcho = await udpClient.ReceiveAsync(timeoutCts.Token);
            var parsedWarmup = TestNetwork.ParseSocks5UdpDatagram(warmupEcho.Buffer);
            if (!parsedWarmup.Payload.AsSpan().SequenceEqual(payload))
            {
                throw new InvalidOperationException($"UDP warmup payload mismatch in {mode}.");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < packetCount; i++)
        {
            var packet = TestNetwork.BuildSocks5UdpDatagram(IPAddress.Loopback, udpEchoPort, payload);
            await udpClient.SendAsync(packet, associateReply.BoundEndPoint, timeoutCts.Token);
            var echoed = await udpClient.ReceiveAsync(timeoutCts.Token);
            var parsed = TestNetwork.ParseSocks5UdpDatagram(echoed.Buffer);
            if (!parsed.Payload.AsSpan().SequenceEqual(payload))
            {
                throw new InvalidOperationException($"UDP payload mismatch at packet {i} in {mode}.");
            }
        }

        stopwatch.Stop();
        var bytesTransferred = (long)payload.Length * packetCount;
        var elapsedSec = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var throughputMibPerSec = (bytesTransferred / 1024d / 1024d) / elapsedSec;
        var packetsPerSec = packetCount / elapsedSec;
        return new UdpPerfResult(mode, payload.Length, packetCount, elapsedSec, throughputMibPerSec, packetsPerSec);
    }

    private static byte[] BuildUdpPayload(int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        var random = new Random(0x00C0FFEE);
        random.NextBytes(payload);
        return payload;
    }

    private readonly record struct UdpPerfResult(
        string Mode,
        int PayloadBytes,
        int PacketCount,
        double Seconds,
        double ThroughputMibPerSec,
        double PacketsPerSec)
    {
        public override string ToString()
        {
            return $"UDP Perf: Mode={Mode}, Payload={PayloadBytes}B, Packets={PacketCount}, Elapsed={Seconds:F3}s, Throughput={ThroughputMibPerSec:F2} MiB/s, PPS={PacketsPerSec:F0}";
        }
    }
}
