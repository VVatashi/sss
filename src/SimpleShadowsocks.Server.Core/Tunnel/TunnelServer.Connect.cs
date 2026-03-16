using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed partial class TunnelServer
{
    private static async Task HandleConnectFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        if (!pendingConnectSessions.TryAdd(frame.SessionId, 0))
        {
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
            return;
        }

        try
        {
            if (sessions.Count >= serverPolicy.MaxSessionsPerTunnel)
            {
                await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                return;
            }

            if (sessions.ContainsKey(frame.SessionId))
            {
                await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                return;
            }

            ConnectRequest request;
            try
            {
                if (frame.Sequence != 0)
                {
                    await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                    return;
                }

                request = ProtocolPayloadSerializer.DeserializeConnectRequest(frame.Payload.Span);
            }
            catch
            {
                await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x08, writeLock, writeOptions, cancellationToken);
                return;
            }

            var upstreamClient = new TcpClient();
            try
            {
                await ConnectUpstreamAsync(upstreamClient, request, serverPolicy.ConnectTimeoutMs, cancellationToken);
            }
            catch (Exception ex)
            {
                upstreamClient.Dispose();
                var replyCode = MapUpstreamConnectFailureToReplyCode(ex, cancellationToken);
                StructuredLog.Error(
                    "tunnel-server",
                    "TUNNEL/TCP",
                    $"upstream connect failed target={request.Address}:{request.Port} reply={replyCode}",
                    ex,
                    frame.SessionId);
                await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode, writeLock, writeOptions, cancellationToken);
                return;
            }

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var context = new TcpSessionContext(frame.SessionId, upstreamClient, upstreamClient.GetStream(), sessionCts);
            context.MarkConnectReceived();
            if (!sessions.TryAdd(frame.SessionId, context))
            {
                context.Dispose();
                await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                return;
            }

            context.UpstreamToClientTask = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                try
                {
                    while (!context.Cancellation.Token.IsCancellationRequested)
                    {
                        var read = await context.UpstreamStream.ReadAsync(buffer, context.Cancellation.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        var sequence = context.TakeNextSendSequence();
                        await SendFrameLockedAsync(
                            secureStream,
                            new ProtocolFrame(FrameType.Data, context.SessionId, sequence, buffer.AsMemory(0, read)),
                            writeLock,
                            writeOptions,
                            context.Cancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    await CloseSessionAsync(
                        sessions,
                        context.SessionId,
                        sendCloseToClient: true,
                        secureStream,
                        writeLock,
                        writeOptions,
                        CancellationToken.None);
                }
            }, context.Cancellation.Token);

            StructuredLog.Info(
                "tunnel-server",
                "TUNNEL/TCP",
                $"session opened target={request.Address}:{request.Port}",
                frame.SessionId);
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x00, writeLock, writeOptions, cancellationToken);
        }
        finally
        {
            pendingConnectSessions.TryRemove(frame.SessionId, out _);
        }
    }

    private static async Task HandleConnectFrameSafelyAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleConnectFrameAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            try
            {
                await SendConnectReplyAsync(
                    secureStream,
                    frame.SessionId,
                    replyCode: 0x05,
                    writeLock,
                    writeOptions,
                    CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static async Task HandleUdpAssociateFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        if (!pendingConnectSessions.TryAdd(frame.SessionId, 0))
        {
            await SendUdpAssociateReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
            return;
        }

        try
        {
            if (frame.Sequence != 0)
            {
                await SendUdpAssociateReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                return;
            }

            if (sessions.Count >= serverPolicy.MaxSessionsPerTunnel || sessions.ContainsKey(frame.SessionId))
            {
                await SendUdpAssociateReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                return;
            }

            var upstreamClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var context = new UdpSessionContext(frame.SessionId, upstreamClient, sessionCts);
            context.MarkConnectReceived();
            if (!sessions.TryAdd(frame.SessionId, context))
            {
                context.Dispose();
                await SendUdpAssociateReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, writeOptions, cancellationToken);
                return;
            }

            context.UpstreamToClientTask = Task.Run(async () =>
            {
                try
                {
                    while (!context.Cancellation.Token.IsCancellationRequested)
                    {
                        var result = await context.UpstreamClient.ReceiveAsync(context.Cancellation.Token);
                        var addressType = ToProtocolAddressType(result.RemoteEndPoint.Address);
                        var datagram = new UdpDatagram(
                            addressType,
                            result.RemoteEndPoint.Address.ToString(),
                            (ushort)result.RemoteEndPoint.Port,
                            result.Buffer);
                        var payload = ProtocolPayloadSerializer.SerializeUdpDatagram(datagram);
                        var sequence = context.TakeNextSendSequence();
                        await SendFrameLockedAsync(
                            secureStream,
                            new ProtocolFrame(FrameType.UdpData, context.SessionId, sequence, payload),
                            writeLock,
                            writeOptions,
                            context.Cancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    StructuredLog.Info("tunnel-server", "TUNNEL/UDP", "session closed", context.SessionId);
                    await CloseSessionAsync(
                        sessions,
                        context.SessionId,
                        sendCloseToClient: true,
                        secureStream,
                        writeLock,
                        writeOptions,
                        CancellationToken.None);
                }
            }, context.Cancellation.Token);

            StructuredLog.Info("tunnel-server", "TUNNEL/UDP", "session opened", frame.SessionId);
            await SendUdpAssociateReplyAsync(secureStream, frame.SessionId, replyCode: 0x00, writeLock, writeOptions, cancellationToken);
        }
        finally
        {
            pendingConnectSessions.TryRemove(frame.SessionId, out _);
        }
    }

    private static async Task HandleUdpAssociateFrameSafelyAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ConcurrentDictionary<uint, byte> pendingConnectSessions,
        ProtocolFrame frame,
        ProtocolWriteOptions writeOptions,
        TunnelServerPolicy serverPolicy,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleUdpAssociateFrameAsync(
                secureStream,
                writeLock,
                sessions,
                pendingConnectSessions,
                frame,
                writeOptions,
                serverPolicy,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            try
            {
                await SendUdpAssociateReplyAsync(
                    secureStream,
                    frame.SessionId,
                    replyCode: 0x05,
                    writeLock,
                    writeOptions,
                    CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static AddressType ToProtocolAddressType(IPAddress ipAddress)
    {
        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => AddressType.IPv4,
            AddressFamily.InterNetworkV6 => AddressType.IPv6,
            _ => throw new InvalidDataException($"Unsupported address family: {ipAddress.AddressFamily}.")
        };
    }

    private static async Task ConnectUpstreamAsync(
        TcpClient upstream,
        ConnectRequest request,
        int connectTimeoutMs,
        CancellationToken cancellationToken)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(connectTimeoutMs);

        try
        {
            if (request.AddressType is AddressType.IPv4 or AddressType.IPv6)
            {
                var ipAddress = IPAddress.Parse(request.Address);
                await upstream.ConnectAsync(ipAddress, request.Port, connectCts.Token);
                return;
            }

            await upstream.ConnectAsync(request.Address, request.Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connect timeout after {connectTimeoutMs} ms.");
        }
    }

    private static byte MapUpstreamConnectFailureToReplyCode(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return 0x01;
        }

        if (exception is TimeoutException)
        {
            return 0x04; // Host unreachable.
        }

        if (exception is InvalidDataException or FormatException or ArgumentException or NotSupportedException)
        {
            return 0x08; // Address type not supported / invalid target format.
        }

        if (exception is SocketException socketException)
        {
            return socketException.SocketErrorCode switch
            {
                SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.NetworkReset => 0x03,
                SocketError.HostUnreachable or SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.TimedOut => 0x04,
                SocketError.ConnectionRefused => 0x05,
                SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable => 0x08,
                _ => 0x01
            };
        }

        return 0x01;
    }
}
