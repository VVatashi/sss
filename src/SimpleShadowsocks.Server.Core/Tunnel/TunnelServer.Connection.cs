using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed partial class TunnelServer
{
    private static async Task HandleTunnelAsync(
        TcpClient tunnelClient,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        using var tunnelStream = tunnelClient.GetStream();
        await using var secureStream = await TunnelCryptoHandshake.AsServerAsync(
            tunnelStream,
            sharedKey,
            cryptoPolicy,
            cancellationToken);

        using var writeLock = new SemaphoreSlim(1, 1);
        var sessions = new ConcurrentDictionary<uint, SessionContext>();
        var pendingConnectSessions = new ConcurrentDictionary<uint, byte>();
        byte? connectionVersion = null;
        var connectionCompressionEnabled = false;
        var connectionCompressionAlgorithm = PayloadCompressionAlgorithm.Deflate;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameResult = await ProtocolFrameCodec.ReadDetailedAsync(secureStream, cancellationToken);
                if (frameResult is null)
                {
                    break;
                }

                var frame = frameResult.Value.Frame;

                if (connectionVersion is null)
                {
                    connectionVersion = frameResult.Value.Version;
                    connectionCompressionEnabled = (frameResult.Value.Flags & ProtocolFlags.CompressionEnabled) != 0;
                    if (connectionCompressionEnabled)
                    {
                        connectionCompressionAlgorithm = ProtocolFrameCodec.GetCompressionAlgorithm(frameResult.Value.Flags);
                    }
                }
                else if (connectionVersion.Value != frameResult.Value.Version)
                {
                    throw new InvalidDataException(
                        $"Protocol version changed in one tunnel: {connectionVersion.Value} -> {frameResult.Value.Version}.");
                }

                var writeOptions = new ProtocolWriteOptions
                {
                    Version = connectionVersion.Value,
                    EnableCompression = connectionVersion.Value == ProtocolConstants.Version && connectionCompressionEnabled,
                    CompressionAlgorithm = connectionCompressionAlgorithm
                };

                await HandleFrameAsync(
                    secureStream,
                    writeLock,
                    sessions,
                    pendingConnectSessions,
                    frame,
                    writeOptions,
                    serverPolicy,
                    cancellationToken);
            }
        }
        finally
        {
            var closeWriteOptions = new ProtocolWriteOptions
            {
                Version = connectionVersion ?? ProtocolConstants.LegacyVersion,
                EnableCompression = connectionVersion == ProtocolConstants.Version && connectionCompressionEnabled,
                CompressionAlgorithm = connectionCompressionAlgorithm
            };

            foreach (var sessionId in sessions.Keys)
            {
                await CloseSessionAsync(
                    sessions,
                    sessionId,
                    sendCloseToClient: false,
                    secureStream,
                    writeLock,
                    closeWriteOptions,
                    CancellationToken.None);
            }
        }
    }

    private static Task HandleFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        return frame.Type switch
        {
            FrameType.Connect => HandleConnectFrameDispatchAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            FrameType.UdpAssociate => HandleUdpAssociateFrameDispatchAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            FrameType.Data => HandleDataFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.UdpData => HandleUdpDataFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.Close => HandleCloseFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            FrameType.Ping => HandlePingFrameAsync(
                secureStream,
                writeLock,
                sessions,
                frame,
                writeOptions,
                cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private static Task HandleConnectFrameDispatchAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () => HandleConnectFrameSafelyAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            cancellationToken);

        return Task.CompletedTask;
    }

    private static Task HandleUdpAssociateFrameDispatchAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () => HandleUdpAssociateFrameSafelyAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken),
            cancellationToken);

        return Task.CompletedTask;
    }

    private static async Task HandleDataFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(frame.SessionId, out var session))
        {
            return;
        }

        if (session is not TcpSessionContext tcpSession)
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        if (!session.TryAcceptIncomingSequence(frame.Sequence))
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        if (!frame.Payload.IsEmpty)
        {
            await tcpSession.UpstreamStream.WriteAsync(frame.Payload, cancellationToken);
            await tcpSession.UpstreamStream.FlushAsync(cancellationToken);
        }
    }

    private static async Task HandleUdpDataFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(frame.SessionId, out var session))
        {
            return;
        }

        if (session is not UdpSessionContext udpSession)
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        if (!session.TryAcceptIncomingSequence(frame.Sequence))
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        if (frame.Payload.IsEmpty)
        {
            return;
        }

        var datagram = ProtocolPayloadSerializer.DeserializeUdpDatagram(frame.Payload.Span);
        var remoteEndPoint = await ResolveRemoteEndPointAsync(datagram, cancellationToken);
        await udpSession.UpstreamClient.SendAsync(datagram.Payload.ToArray(), remoteEndPoint, cancellationToken);
    }

    private static async Task HandleCloseFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (sessions.TryGetValue(frame.SessionId, out var closingSession)
            && !closingSession.TryAcceptIncomingSequence(frame.Sequence))
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        await CloseSessionAsync(
            sessions,
            frame.SessionId,
            sendCloseToClient: false,
            secureStream,
            writeLock,
            writeOptions,
            cancellationToken);
    }

    private static async Task HandlePingFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        if (sessions.TryGetValue(frame.SessionId, out var pingSession)
            && !pingSession.TryAcceptIncomingSequence(frame.Sequence))
        {
            await CloseSessionAsync(
                sessions,
                frame.SessionId,
                sendCloseToClient: true,
                secureStream,
                writeLock,
                writeOptions,
                CancellationToken.None);
            return;
        }

        var pongSequence = sessions.TryGetValue(frame.SessionId, out pingSession)
            ? pingSession.TakeNextSendSequence()
            : 0UL;
        await SendFrameLockedAsync(
            secureStream,
            new ProtocolFrame(FrameType.Pong, frame.SessionId, pongSequence, frame.Payload),
            writeLock,
            writeOptions,
            cancellationToken);
    }

    private static async Task<IPEndPoint> ResolveRemoteEndPointAsync(UdpDatagram datagram, CancellationToken cancellationToken)
    {
        if (datagram.AddressType is AddressType.IPv4 or AddressType.IPv6)
        {
            return new IPEndPoint(IPAddress.Parse(datagram.Address), datagram.Port);
        }

        var addresses = await Dns.GetHostAddressesAsync(datagram.Address, cancellationToken);
        var selected = addresses.FirstOrDefault(ip => ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
        if (selected is null)
        {
            throw new InvalidDataException($"Unable to resolve UDP destination '{datagram.Address}'.");
        }

        return new IPEndPoint(selected, datagram.Port);
    }
}
