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

    private static async Task ConnectDirectAsync(TcpClient client, Socks5ConnectRequest request, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(request.Host, out var ipAddress))
        {
            await client.ConnectAsync(ipAddress, request.Port, cancellationToken);
            return;
        }

        await client.ConnectAsync(request.Host, request.Port, cancellationToken);
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
