using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Socks5;

public sealed partial class Socks5Server
{
    private const int RelayBufferSize = 64 * 1024;

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
            StructuredLog.Error("socks5-server", "SOCKS5/TCP", $"direct connect failed target={request.Host}:{request.Port}", ex);
            await SendReplyAsync(clientStream, replyCode: 0x05, null, cancellationToken);
            return;
        }

        var boundEndPoint = upstream.Client.LocalEndPoint as IPEndPoint;
        StructuredLog.Info("socks5-server", "SOCKS5/TCP", $"direct connect established target={request.Host}:{request.Port}");
        await SendReplyAsync(clientStream, replyCode: 0x00, boundEndPoint, cancellationToken);

        using var upstreamStream = upstream.GetStream();
        await RelayRawAsync(clientStream, upstreamStream, cancellationToken);
    }

    private async Task HandleViaTunnelAsync(
        NetworkStream clientStream,
        Socks5ConnectRequest request,
        IReadOnlyList<TunnelClientMultiplexer> multiplexers,
        CancellationToken cancellationToken)
    {
        var connectRequest = new ConnectRequest(ToProtocolAddressType(request.AddressType), request.Host, (ushort)request.Port);
        byte lastReplyCode = 0x01;
        Exception? lastOpenError = null;

        for (var attempt = 0; attempt < multiplexers.Count; attempt++)
        {
            var multiplexer = multiplexers[attempt];
            var isFinalAttempt = attempt == multiplexers.Count - 1;

            uint sessionId;
            byte replyCode;
            ChannelReader<OwnedPayloadChunk> reader;
            try
            {
                (sessionId, replyCode, reader) = await multiplexer.OpenSessionAsync(connectRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                lastOpenError = ex;
                if (isFinalAttempt)
                {
                    StructuredLog.Error("socks5-server", "TUNNEL/TCP", "all tunnel servers failed to open CONNECT session", ex);
                }
                else
                {
                    StructuredLog.Warn("socks5-server", "TUNNEL/TCP", "tunnel server failed to open CONNECT session; trying next");
                }

                continue;
            }

            if (replyCode != 0x00)
            {
                lastReplyCode = replyCode;
                if (isFinalAttempt)
                {
                    StructuredLog.Error(
                        "socks5-server",
                        "TUNNEL/TCP",
                        $"all tunnel servers rejected CONNECT target={request.Host}:{request.Port} final_reply={replyCode}",
                        new IOException("No tunnel server accepted CONNECT."),
                        sessionId);
                }
                else
                {
                    StructuredLog.Warn(
                        "socks5-server",
                        "TUNNEL/TCP",
                        $"tunnel server rejected CONNECT target={request.Host}:{request.Port} reply={replyCode}; trying next",
                        sessionId);
                }

                continue;
            }

            StructuredLog.Info(
                "socks5-server",
                "TUNNEL/TCP",
                $"tunnel session opened target={request.Host}:{request.Port}",
                sessionId);
            await SendReplyAsync(clientStream, replyCode: 0x00, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            await RelayViaTunnelAsync(clientStream, multiplexer, reader, sessionId, cancellationToken);
            return;
        }

        if (lastOpenError is not null && lastReplyCode == 0x01)
        {
            await SendReplyAsync(clientStream, replyCode: 0x01, null, cancellationToken);
            return;
        }

        await SendReplyAsync(clientStream, replyCode: lastReplyCode, null, cancellationToken);
    }

    private static async Task RelayViaTunnelAsync(
        NetworkStream clientStream,
        TunnelClientMultiplexer multiplexer,
        ChannelReader<OwnedPayloadChunk> reader,
        uint sessionId,
        CancellationToken cancellationToken)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;
        var clientInitiatedClose = false;

        var clientToTunnel = Task.Run(async () =>
        {
            var buffer = new byte[RelayBufferSize];
            while (!relayToken.IsCancellationRequested)
            {
                var read = await clientStream.ReadAsync(buffer, relayToken);
                if (read == 0)
                {
                    clientInitiatedClose = true;
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
                using (data)
                {
                    if (data.Length > 0)
                    {
                        await clientStream.WriteAsync(data.Memory, relayToken);
                    }
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
        catch
        {
        }

        if (clientInitiatedClose)
        {
            StructuredLog.Info("socks5-server", "TUNNEL/TCP", "tunnel session closed", sessionId);
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
        var fragmentReassembler = new Socks5UdpFragmentReassembler();

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

                    if (!fragmentReassembler.TryReassemble(requestDatagram, out var assembledDatagram))
                    {
                        continue;
                    }

                    try
                    {
                        var destinationEndPoint = await ResolveUdpRemoteEndPointAsync(assembledDatagram, relayToken);
                        await udpRelay.SendAsync(assembledDatagram.Payload, destinationEndPoint, relayToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                    }

                    continue;
                }

                if (activeClientEndPoint is null)
                {
                    continue;
                }

                try
                {
                    var responseDatagram = BuildUdpRequestDatagram(
                        ToProtocolAddressType(received.RemoteEndPoint.Address),
                        received.RemoteEndPoint.Address.ToString(),
                        (ushort)received.RemoteEndPoint.Port,
                        received.Buffer);
                    await udpRelay.SendAsync(responseDatagram, activeClientEndPoint, relayToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
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

    private async Task HandleUdpAssociateRoutedAsync(
        NetworkStream clientStream,
        Socks5ConnectRequest request,
        IReadOnlyList<TunnelClientMultiplexer> multiplexers,
        CancellationToken cancellationToken)
    {
        using var udpRelay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)udpRelay.Client.LocalEndPoint!;
        await SendReplyAsync(clientStream, replyCode: 0x00, relayEndPoint, cancellationToken);

        var requestedClientEndPoint = TryGetRequestedUdpClientEndPoint(request);
        IPEndPoint? activeClientEndPoint = requestedClientEndPoint;
        var fragmentReassembler = new Socks5UdpFragmentReassembler();

        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;

        uint? tunnelSessionId = null;
        ChannelReader<UdpDatagram>? tunnelReader = null;
        TunnelClientMultiplexer? activeTunnelMultiplexer = null;

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

                    if (!fragmentReassembler.TryReassemble(requestDatagram, out var assembledDatagram))
                    {
                        continue;
                    }

                    var routeRequest = ToUdpRouteRequest(assembledDatagram);
                    var matchedRule = _routingPolicy?.Match(routeRequest);
                    if (matchedRule is null)
                    {
                        StructuredLog.Warn(
                            "socks5-server",
                            "SOCKS5/UDP",
                            $"udp datagram dropped: no routing rule matched target={assembledDatagram.Address}:{assembledDatagram.Port}");
                        continue;
                    }

                    if (matchedRule.Decision == TrafficRouteDecision.Direct)
                    {
                        try
                        {
                            var destinationEndPoint = await ResolveUdpRemoteEndPointAsync(assembledDatagram, relayToken);
                            await udpRelay.SendAsync(assembledDatagram.Payload, destinationEndPoint, relayToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                        }

                        continue;
                    }

                    if (matchedRule.Decision == TrafficRouteDecision.Drop)
                    {
                        StructuredLog.Warn(
                            "socks5-server",
                            "SOCKS5/UDP",
                            $"udp datagram dropped by routing rule target={assembledDatagram.Address}:{assembledDatagram.Port}");
                        continue;
                    }

                    if (multiplexers.Count == 0)
                    {
                        StructuredLog.Warn(
                            "socks5-server",
                            "SOCKS5/UDP",
                            $"udp datagram dropped: routed to tunnel but no tunnel backend is configured target={assembledDatagram.Address}:{assembledDatagram.Port}");
                        continue;
                    }

                    try
                    {
                        if (tunnelSessionId is null || tunnelReader is null || activeTunnelMultiplexer is null)
                        {
                            (tunnelSessionId, tunnelReader, activeTunnelMultiplexer) = await OpenUdpTunnelSessionAsync(multiplexers, relayToken);
                        }

                        await activeTunnelMultiplexer.SendUdpDataAsync(tunnelSessionId.Value, assembledDatagram, relayToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        StructuredLog.Warn(
                            "socks5-server",
                            "TUNNEL/UDP",
                            $"udp datagram forwarding failed target={assembledDatagram.Address}:{assembledDatagram.Port}",
                            tunnelSessionId);
                        StructuredLog.Error("socks5-server", "TUNNEL/UDP", "tunnel udp session failed", ex, tunnelSessionId);
                        tunnelSessionId = null;
                        tunnelReader = null;
                        activeTunnelMultiplexer = null;
                    }

                    continue;
                }

                if (activeClientEndPoint is null)
                {
                    continue;
                }

                try
                {
                    var responseDatagram = BuildUdpRequestDatagram(
                        ToProtocolAddressType(received.RemoteEndPoint.Address),
                        received.RemoteEndPoint.Address.ToString(),
                        (ushort)received.RemoteEndPoint.Port,
                        received.Buffer);
                    await udpRelay.SendAsync(responseDatagram, activeClientEndPoint, relayToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
        }, relayToken);

        var tunnelToUdpClientTask = Task.Run(async () =>
        {
            while (!relayToken.IsCancellationRequested)
            {
                if (tunnelReader is null)
                {
                    await Task.Delay(20, relayToken);
                    continue;
                }

                await foreach (var datagram in tunnelReader.ReadAllAsync(relayToken))
                {
                    if (activeClientEndPoint is null)
                    {
                        continue;
                    }

                    try
                    {
                        var socksDatagram = BuildUdpRequestDatagram(
                            datagram.AddressType,
                            datagram.Address,
                            datagram.Port,
                            datagram.Payload.Span);
                        await udpRelay.SendAsync(socksDatagram, activeClientEndPoint, relayToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                    }
                }

                tunnelSessionId = null;
                tunnelReader = null;
                activeTunnelMultiplexer = null;
            }
        }, relayToken);

        await Task.WhenAny(tcpMonitorTask, udpRelayTask, tunnelToUdpClientTask);
        relayCts.Cancel();

        try
        {
            await Task.WhenAll(tcpMonitorTask, udpRelayTask, tunnelToUdpClientTask);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (tunnelSessionId is not null && activeTunnelMultiplexer is not null)
            {
                try
                {
                    await activeTunnelMultiplexer.CloseSessionAsync(tunnelSessionId.Value, 0x00, CancellationToken.None);
                    StructuredLog.Info("socks5-server", "TUNNEL/UDP", "tunnel udp session closed", tunnelSessionId.Value);
                }
                catch
                {
                }
            }
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
            StructuredLog.Error("socks5-server", "TUNNEL/UDP", "tunnel udp session open failed", ex);
            await SendReplyAsync(clientStream, replyCode: 0x01, null, cancellationToken);
            return;
        }

        if (replyCode != 0x00)
        {
            StructuredLog.Warn("socks5-server", "TUNNEL/UDP", $"tunnel udp associate rejected reply={replyCode}", sessionId);
            await SendReplyAsync(clientStream, replyCode, null, cancellationToken);
            return;
        }

        StructuredLog.Info("socks5-server", "TUNNEL/UDP", "tunnel udp session opened", sessionId);

        using var udpRelay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)udpRelay.Client.LocalEndPoint!;
        await SendReplyAsync(clientStream, replyCode: 0x00, relayEndPoint, cancellationToken);

        var requestedClientEndPoint = TryGetRequestedUdpClientEndPoint(request);
        IPEndPoint? activeClientEndPoint = requestedClientEndPoint;
        var fragmentReassembler = new Socks5UdpFragmentReassembler();

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

                if (!fragmentReassembler.TryReassemble(requestDatagram, out var assembledDatagram))
                {
                    continue;
                }

                try
                {
                    await multiplexer.SendUdpDataAsync(sessionId, assembledDatagram, relayToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
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

                try
                {
                    var socksDatagram = BuildUdpRequestDatagram(
                        datagram.AddressType,
                        datagram.Address,
                        datagram.Port,
                        datagram.Payload.Span);
                    await udpRelay.SendAsync(socksDatagram, activeClientEndPoint, relayToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
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
                StructuredLog.Info("socks5-server", "TUNNEL/UDP", "tunnel udp session closed", sessionId);
            }
            catch
            {
            }
        }
    }

    private static Socks5ConnectRequest ToUdpRouteRequest(UdpDatagram datagram)
    {
        var addressType = datagram.AddressType switch
        {
            AddressType.IPv4 => AddressTypeIPv4,
            AddressType.Domain => AddressTypeDomain,
            AddressType.IPv6 => AddressTypeIPv6,
            _ => throw new InvalidDataException($"Unsupported UDP address type: {datagram.AddressType}")
        };

        return new Socks5ConnectRequest(CommandUdpAssociate, datagram.Address, datagram.Port, addressType);
    }

    private static async Task<(uint SessionId, ChannelReader<UdpDatagram> Reader, TunnelClientMultiplexer Multiplexer)> OpenUdpTunnelSessionAsync(
        IReadOnlyList<TunnelClientMultiplexer> multiplexers,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var multiplexer in multiplexers)
        {
            try
            {
                var (sessionId, replyCode, reader) = await multiplexer.OpenUdpSessionAsync(cancellationToken);
                if (replyCode == 0x00)
                {
                    StructuredLog.Info("socks5-server", "TUNNEL/UDP", "tunnel udp session opened", sessionId);
                    return (sessionId, reader, multiplexer);
                }

                lastError = new IOException($"Tunnel UDP associate rejected with reply={replyCode}.");
                StructuredLog.Warn("socks5-server", "TUNNEL/UDP", $"tunnel udp associate rejected reply={replyCode}", sessionId);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new IOException("Failed to open tunnel UDP session.", lastError);
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
        var selected = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
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
        var buffer = new byte[RelayBufferSize];
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
