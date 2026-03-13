using System.Net;
using System.Net.Sockets;

namespace SimpleShadowsocks.Client.Socks5;

public sealed class Socks5Server
{
    private const byte SocksVersion = 0x05;
    private const byte AuthNone = 0x00;
    private const byte AuthNoAcceptableMethods = 0xFF;
    private const byte CommandConnect = 0x01;
    private const byte AddressTypeIPv4 = 0x01;
    private const byte AddressTypeDomain = 0x03;
    private const byte AddressTypeIPv6 = 0x04;

    private readonly TcpListener _listener;

    public Socks5Server(IPAddress listenAddress, int port)
    {
        _listener = new TcpListener(listenAddress, port);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientSafelyAsync(client, cancellationToken), cancellationToken);
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

    private static async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[socks5] client failed: {ex.Message}");
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientStream = client.GetStream();

        if (!await HandleGreetingAsync(clientStream, cancellationToken))
        {
            return;
        }

        var request = await ReadConnectRequestAsync(clientStream, cancellationToken);
        if (request is null)
        {
            return;
        }

        using var upstream = new TcpClient();

        try
        {
            await ConnectAsync(upstream, request.Value, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[socks5] connect failed {request.Value.Host}:{request.Value.Port} ({ex.Message})");
            await SendReplyAsync(clientStream, replyCode: 0x05, null, cancellationToken);
            return;
        }

        var boundEndPoint = upstream.Client.LocalEndPoint as IPEndPoint;
        await SendReplyAsync(clientStream, replyCode: 0x00, boundEndPoint, cancellationToken);

        Console.WriteLine($"[socks5] proxy {request.Value.Host}:{request.Value.Port}");
        using var upstreamStream = upstream.GetStream();
        await RelayAsync(clientStream, upstreamStream, cancellationToken);
    }

    private static async Task<bool> HandleGreetingAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        if (!await TryReadExactAsync(stream, header, cancellationToken))
        {
            return false;
        }

        if (header[0] != SocksVersion)
        {
            return false;
        }

        var methodCount = header[1];
        if (methodCount == 0)
        {
            return false;
        }

        var methods = new byte[methodCount];
        if (!await TryReadExactAsync(stream, methods, cancellationToken))
        {
            return false;
        }

        var supportsNoAuth = methods.Contains(AuthNone);
        var response = new[] { SocksVersion, supportsNoAuth ? AuthNone : AuthNoAcceptableMethods };
        await stream.WriteAsync(response, cancellationToken);

        return supportsNoAuth;
    }

    private static async Task<Socks5ConnectRequest?> ReadConnectRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await TryReadExactAsync(stream, header, cancellationToken))
        {
            return null;
        }

        var version = header[0];
        var command = header[1];
        var addressType = header[3];

        if (version != SocksVersion)
        {
            return null;
        }

        var host = addressType switch
        {
            AddressTypeIPv4 => await ReadIPv4AddressAsync(stream, cancellationToken),
            AddressTypeIPv6 => await ReadIPv6AddressAsync(stream, cancellationToken),
            AddressTypeDomain => await ReadDomainAddressAsync(stream, cancellationToken),
            _ => null
        };

        if (host is null)
        {
            await SendReplyAsync(stream, replyCode: 0x08, null, cancellationToken);
            return null;
        }

        var portBytes = new byte[2];
        if (!await TryReadExactAsync(stream, portBytes, cancellationToken))
        {
            return null;
        }

        var port = (ushort)((portBytes[0] << 8) | portBytes[1]);

        if (command != CommandConnect)
        {
            await SendReplyAsync(stream, replyCode: 0x07, null, cancellationToken);
            return null;
        }

        return new Socks5ConnectRequest(host, port);
    }

    private static async Task<string?> ReadIPv4AddressAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        if (!await TryReadExactAsync(stream, bytes, cancellationToken))
        {
            return null;
        }

        return new IPAddress(bytes).ToString();
    }

    private static async Task<string?> ReadIPv6AddressAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[16];
        if (!await TryReadExactAsync(stream, bytes, cancellationToken))
        {
            return null;
        }

        return new IPAddress(bytes).ToString();
    }

    private static async Task<string?> ReadDomainAddressAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[1];
        if (!await TryReadExactAsync(stream, lengthBuffer, cancellationToken))
        {
            return null;
        }

        var length = lengthBuffer[0];
        if (length == 0)
        {
            return null;
        }

        var domainBytes = new byte[length];
        if (!await TryReadExactAsync(stream, domainBytes, cancellationToken))
        {
            return null;
        }

        return System.Text.Encoding.ASCII.GetString(domainBytes);
    }

    private static async Task ConnectAsync(TcpClient client, Socks5ConnectRequest request, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(request.Host, out var ipAddress))
        {
            await client.ConnectAsync(ipAddress, request.Port, cancellationToken);
            return;
        }

        await client.ConnectAsync(request.Host, request.Port, cancellationToken);
    }

    private static async Task SendReplyAsync(
        NetworkStream stream,
        byte replyCode,
        IPEndPoint? boundEndPoint,
        CancellationToken cancellationToken)
    {
        if (boundEndPoint is null)
        {
            var failedReply = new byte[]
            {
                SocksVersion, replyCode, 0x00, AddressTypeIPv4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            await stream.WriteAsync(failedReply, cancellationToken);
            return;
        }

        var addressBytes = boundEndPoint.Address.GetAddressBytes();
        var addressType = boundEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? AddressTypeIPv6
            : AddressTypeIPv4;

        var response = new byte[4 + addressBytes.Length + 2];
        response[0] = SocksVersion;
        response[1] = replyCode;
        response[2] = 0x00;
        response[3] = addressType;
        Buffer.BlockCopy(addressBytes, 0, response, 4, addressBytes.Length);
        response[^2] = (byte)(boundEndPoint.Port >> 8);
        response[^1] = (byte)(boundEndPoint.Port & 0xFF);

        await stream.WriteAsync(response, cancellationToken);
    }

    private static async Task RelayAsync(Stream clientStream, Stream upstreamStream, CancellationToken cancellationToken)
    {
        var toUpstream = PumpAsync(clientStream, upstreamStream, cancellationToken);
        var toClient = PumpAsync(upstreamStream, clientStream, cancellationToken);

        await Task.WhenAny(toUpstream, toClient);
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
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
            await destination.FlushAsync(cancellationToken);
        }
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private readonly record struct Socks5ConnectRequest(string Host, int Port);
}
