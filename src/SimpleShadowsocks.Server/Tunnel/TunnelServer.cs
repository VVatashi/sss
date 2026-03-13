using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed class TunnelServer
{
    private readonly TcpListener _listener;
    private readonly byte[] _sharedKey;

    public TunnelServer(IPAddress listenAddress, int port)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes("dev-shared-key");
    }

    public TunnelServer(IPAddress listenAddress, int port, string sharedKey)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tunnelClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleTunnelSafelyAsync(tunnelClient, _sharedKey, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    private static async Task HandleTunnelSafelyAsync(TcpClient tunnelClient, byte[] sharedKey, CancellationToken cancellationToken)
    {
        using (tunnelClient)
        {
            try
            {
                await HandleTunnelAsync(tunnelClient, sharedKey, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[tunnel] client failed: {ex.Message}");
            }
        }
    }

    private static async Task HandleTunnelAsync(TcpClient tunnelClient, byte[] sharedKey, CancellationToken cancellationToken)
    {
        using var tunnelStream = tunnelClient.GetStream();
        await using var secureStream = await TunnelCryptoHandshake.AsServerAsync(tunnelStream, sharedKey, cancellationToken);

        var firstFrame = await ProtocolFrameCodec.ReadAsync(secureStream, cancellationToken);
        if (firstFrame is null || firstFrame.Value.Type != FrameType.Connect)
        {
            return;
        }

        var sessionId = firstFrame.Value.SessionId;
        ConnectRequest request;

        try
        {
            request = ProtocolPayloadSerializer.DeserializeConnectRequest(firstFrame.Value.Payload.Span);
        }
        catch (Exception)
        {
            await SendConnectReplyAsync(secureStream, sessionId, replyCode: 0x08, cancellationToken);
            return;
        }

        using var upstream = new TcpClient();

        try
        {
            await ConnectUpstreamAsync(upstream, request, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[tunnel] connect failed {request.Address}:{request.Port} ({ex.Message})");
            await SendConnectReplyAsync(secureStream, sessionId, replyCode: 0x05, cancellationToken);
            return;
        }

        await SendConnectReplyAsync(secureStream, sessionId, replyCode: 0x00, cancellationToken);
        Console.WriteLine($"[tunnel] proxy {request.Address}:{request.Port}");

        using var upstreamStream = upstream.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;

        var tunnelToUpstream = Task.Run(async () =>
        {
            while (!relayToken.IsCancellationRequested)
            {
                var frame = await ProtocolFrameCodec.ReadAsync(secureStream, relayToken);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.SessionId != sessionId)
                {
                    continue;
                }

                switch (frame.Value.Type)
                {
                    case FrameType.Data:
                        if (!frame.Value.Payload.IsEmpty)
                        {
                            await upstreamStream.WriteAsync(frame.Value.Payload, relayToken);
                            await upstreamStream.FlushAsync(relayToken);
                        }
                        break;
                    case FrameType.Close:
                        return;
                    case FrameType.Ping:
                        await SendFrameLockedAsync(
                    secureStream,
                    new ProtocolFrame(FrameType.Pong, sessionId, frame.Value.Payload),
                    writeLock,
                    relayToken);
                        break;
                }
            }
        }, relayToken);

        var upstreamToTunnel = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            while (!relayToken.IsCancellationRequested)
            {
                var read = await upstreamStream.ReadAsync(buffer, relayToken);
                if (read == 0)
                {
                    break;
                }

                var payload = new byte[read];
                Buffer.BlockCopy(buffer, 0, payload, 0, read);
                await SendFrameLockedAsync(
                    secureStream,
                    new ProtocolFrame(FrameType.Data, sessionId, payload),
                    writeLock,
                    relayToken);
            }
        }, relayToken);

        await Task.WhenAny(tunnelToUpstream, upstreamToTunnel);

        try
        {
            await SendFrameLockedAsync(
                secureStream,
                new ProtocolFrame(FrameType.Close, sessionId, ProtocolPayloadSerializer.SerializeClose(0x00)),
                writeLock,
                relayToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        relayCts.Cancel();

        try
        {
            await Task.WhenAll(tunnelToUpstream, upstreamToTunnel);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ConnectUpstreamAsync(TcpClient upstream, ConnectRequest request, CancellationToken cancellationToken)
    {
        if (request.AddressType is AddressType.IPv4 or AddressType.IPv6)
        {
            var ipAddress = IPAddress.Parse(request.Address);
            await upstream.ConnectAsync(ipAddress, request.Port, cancellationToken);
            return;
        }

        await upstream.ConnectAsync(request.Address, request.Port, cancellationToken);
    }

    private static Task SendConnectReplyAsync(
        Stream stream,
        uint sessionId,
        byte replyCode,
        CancellationToken cancellationToken)
    {
        return ProtocolFrameCodec.WriteAsync(
            stream,
            new ProtocolFrame(FrameType.Connect, sessionId, new[] { replyCode }),
            cancellationToken).AsTask();
    }

    private static async Task SendFrameLockedAsync(
        Stream stream,
        ProtocolFrame frame,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await ProtocolFrameCodec.WriteAsync(stream, frame, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
