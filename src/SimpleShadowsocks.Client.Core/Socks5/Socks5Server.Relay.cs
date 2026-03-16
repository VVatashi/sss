using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Socks5;

public sealed partial class Socks5Server
{
    private static async Task HandleDirectAsync(
        NetworkStream clientStream,
        Socks5ConnectRequest request,
        CancellationToken cancellationToken)
    {
        using var upstream = new TcpClient();

        try
        {
            await ConnectDirectAsync(upstream, request, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[socks5] connect failed {request.Host}:{request.Port} ({ex.Message})");
            await SendReplyAsync(clientStream, replyCode: 0x05, null, cancellationToken);
            return;
        }

        var boundEndPoint = upstream.Client.LocalEndPoint as IPEndPoint;
        await SendReplyAsync(clientStream, replyCode: 0x00, boundEndPoint, cancellationToken);

        using var upstreamStream = upstream.GetStream();
        await RelayRawAsync(clientStream, upstreamStream, cancellationToken);
    }

    private async Task HandleViaTunnelAsync(
        NetworkStream clientStream,
        Socks5ConnectRequest request,
        TunnelClientMultiplexer multiplexer,
        CancellationToken cancellationToken)
    {
        var connectRequest = new ConnectRequest(ToProtocolAddressType(request.AddressType), request.Host, (ushort)request.Port);
        uint sessionId;
        byte replyCode;
        ChannelReader<byte[]> reader;
        try
        {
            (sessionId, replyCode, reader) = await multiplexer.OpenSessionAsync(connectRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[socks5] tunnel connect failed ({ex.Message})");
            await SendReplyAsync(clientStream, replyCode: 0x01, null, cancellationToken);
            return;
        }

        if (replyCode != 0x00)
        {
            Console.WriteLine($"[socks5] connect failed {request.Host}:{request.Port} (remote reply={replyCode})");
            await SendReplyAsync(clientStream, replyCode, null, cancellationToken);
            return;
        }

        await SendReplyAsync(clientStream, replyCode: 0x00, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
        await RelayViaTunnelAsync(clientStream, multiplexer, reader, sessionId, cancellationToken);
    }

    private static async Task RelayViaTunnelAsync(
        NetworkStream clientStream,
        TunnelClientMultiplexer multiplexer,
        ChannelReader<byte[]> reader,
        uint sessionId,
        CancellationToken cancellationToken)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;

        var clientToTunnel = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            while (!relayToken.IsCancellationRequested)
            {
                var read = await clientStream.ReadAsync(buffer, relayToken);
                if (read == 0)
                {
                    await multiplexer.CloseSessionAsync(sessionId, 0x00, relayToken);
                    break;
                }

                await multiplexer.SendDataAsync(sessionId, buffer.AsMemory(0, read), relayToken);
            }
        }, relayToken);

        var tunnelToClient = Task.Run(async () =>
        {
            await foreach (var data in reader.ReadAllAsync(relayToken))
            {
                if (data.Length > 0)
                {
                    await clientStream.WriteAsync(data, relayToken);
                }
            }
        }, relayToken);

        await Task.WhenAny(clientToTunnel, tunnelToClient);
        relayCts.Cancel();

        try
        {
            await Task.WhenAll(clientToTunnel, tunnelToClient);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                await multiplexer.CloseSessionAsync(sessionId, 0x00, CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static async Task HandleUdpAssociateDirectAsync(
        NetworkStream clientStream,
        Socks5ConnectRequest request,
        CancellationToken cancellationToken)
    {
        using var udpRelay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)udpRelay.Client.LocalEndPoint!;
        await SendReplyAsync(clientStream, replyCode: 0x00, relayEndPoint, cancellationToken);

        var requestedClientEndPoint = TryGetRequestedUdpClientEndPoint(request);
        IPEndPoint? activeClientEndPoint = requestedClientEndPoint;

        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;

        var tcpMonitorTask = WaitForTcpDisconnectAsync(clientStream, relayToken);
        var udpRelayTask = Task.Run(async () =>
        {
            while (!relayToken.IsCancellationRequested)
            {
                var received = await udpRelay.ReceiveAsync(relayToken);
                if (activeClientEndPoint is null)
                {
                    activeClientEndPoint = received.RemoteEndPoint;
                }

                if (AreSameEndpoint(received.RemoteEndPoint, activeClientEndPoint))
                {
                    if (!TryParseUdpRequestDatagram(received.Buffer, out var requestDatagram))
                    {
                        continue;
                    }

                    var destinationEndPoint = await ResolveUdpRemoteEndPointAsync(requestDatagram, relayToken);
                    await udpRelay.SendAsync(requestDatagram.Payload.ToArray(), destinationEndPoint, relayToken);
                    continue;
                }

                if (activeClientEndPoint is null)
                {
                    continue;
                }

                var responseDatagram = BuildUdpRequestDatagram(
                    ToProtocolAddressType(received.RemoteEndPoint.Address),
                    received.RemoteEndPoint.Address.ToString(),
                    (ushort)received.RemoteEndPoint.Port,
                    received.Buffer);
                await udpRelay.SendAsync(responseDatagram, activeClientEndPoint, relayToken);
            }
        }, relayToken);

        await Task.WhenAny(tcpMonitorTask, udpRelayTask);
        relayCts.Cancel();

        try
        {
            await Task.WhenAll(tcpMonitorTask, udpRelayTask);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task HandleUdpAssociateViaTunnelAsync(
        NetworkStream clientStream,
        Socks5ConnectRequest request,
        TunnelClientMultiplexer multiplexer,
        CancellationToken cancellationToken)
    {
        uint sessionId;
        byte replyCode;
        ChannelReader<UdpDatagram> reader;
        try
        {
            (sessionId, replyCode, reader) = await multiplexer.OpenUdpSessionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[socks5] tunnel udp associate failed ({ex.Message})");
            await SendReplyAsync(clientStream, replyCode: 0x01, null, cancellationToken);
            return;
        }

        if (replyCode != 0x00)
        {
            await SendReplyAsync(clientStream, replyCode, null, cancellationToken);
            return;
        }

        using var udpRelay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)udpRelay.Client.LocalEndPoint!;
        await SendReplyAsync(clientStream, replyCode: 0x00, relayEndPoint, cancellationToken);

        var requestedClientEndPoint = TryGetRequestedUdpClientEndPoint(request);
        IPEndPoint? activeClientEndPoint = requestedClientEndPoint;

        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;

        var tcpMonitorTask = WaitForTcpDisconnectAsync(clientStream, relayToken);
        var udpClientToTunnelTask = Task.Run(async () =>
        {
            while (!relayToken.IsCancellationRequested)
            {
                var received = await udpRelay.ReceiveAsync(relayToken);
                if (activeClientEndPoint is null)
                {
                    activeClientEndPoint = received.RemoteEndPoint;
                }

                if (!AreSameEndpoint(received.RemoteEndPoint, activeClientEndPoint))
                {
                    continue;
                }

                if (!TryParseUdpRequestDatagram(received.Buffer, out var requestDatagram))
                {
                    continue;
                }

                await multiplexer.SendUdpDataAsync(sessionId, requestDatagram, relayToken);
            }
        }, relayToken);

        var tunnelToUdpClientTask = Task.Run(async () =>
        {
            await foreach (var datagram in reader.ReadAllAsync(relayToken))
            {
                if (activeClientEndPoint is null)
                {
                    continue;
                }

                var socksDatagram = BuildUdpRequestDatagram(
                    datagram.AddressType,
                    datagram.Address,
                    datagram.Port,
                    datagram.Payload.Span);
                await udpRelay.SendAsync(socksDatagram, activeClientEndPoint, relayToken);
            }
        }, relayToken);

        await Task.WhenAny(tcpMonitorTask, udpClientToTunnelTask, tunnelToUdpClientTask);
        relayCts.Cancel();

        try
        {
            await Task.WhenAll(tcpMonitorTask, udpClientToTunnelTask, tunnelToUdpClientTask);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                await multiplexer.CloseSessionAsync(sessionId, 0x00, CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static async Task ConnectDirectAsync(TcpClient client, Socks5ConnectRequest request, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(request.Host, out var ipAddress))
        {
            await client.ConnectAsync(ipAddress, request.Port, cancellationToken);
            return;
        }

        await client.ConnectAsync(request.Host, request.Port, cancellationToken);
    }

    private static IPEndPoint? TryGetRequestedUdpClientEndPoint(Socks5ConnectRequest request)
    {
        if (request.Port <= 0)
        {
            return null;
        }

        if (!IPAddress.TryParse(request.Host, out var address))
        {
            return null;
        }

        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            return null;
        }

        return new IPEndPoint(address, request.Port);
    }

    private static bool AreSameEndpoint(IPEndPoint left, IPEndPoint right)
    {
        return left.Port == right.Port && left.Address.Equals(right.Address);
    }

    private static async Task WaitForTcpDisconnectAsync(NetworkStream clientStream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await clientStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }
        }
    }

    private static async Task<IPEndPoint> ResolveUdpRemoteEndPointAsync(UdpDatagram datagram, CancellationToken cancellationToken)
    {
        if (datagram.AddressType is AddressType.IPv4 or AddressType.IPv6)
        {
            return new IPEndPoint(IPAddress.Parse(datagram.Address), datagram.Port);
        }

        var addresses = await Dns.GetHostAddressesAsync(datagram.Address, cancellationToken);
        var selected = addresses.FirstOrDefault(ip => ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
        if (selected is null)
        {
            throw new InvalidDataException($"Failed to resolve UDP destination: {datagram.Address}");
        }

        return new IPEndPoint(selected, datagram.Port);
    }

    private static async Task RelayRawAsync(Stream clientStream, Stream upstreamStream, CancellationToken cancellationToken)
    {
        var toUpstream = PumpRawAsync(clientStream, upstreamStream, cancellationToken);
        var toClient = PumpRawAsync(upstreamStream, clientStream, cancellationToken);

        await Task.WhenAny(toUpstream, toClient);
    }

    private static async Task PumpRawAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
