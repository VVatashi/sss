using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using SimpleShadowsocks.Client.Tunnel;

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
    private readonly string? _remoteServerHost;
    private readonly int _remoteServerPort;
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private TunnelClientMultiplexer? _multiplexer;

    public Socks5Server(IPAddress listenAddress, int port)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes("dev-shared-key");
        _cryptoPolicy = TunnelCryptoPolicy.Default;
    }

    public Socks5Server(
        IPAddress listenAddress,
        int port,
        string remoteServerHost,
        int remoteServerPort,
        string sharedKey,
        TunnelCryptoPolicy? cryptoPolicy = null)
    {
        _listener = new TcpListener(listenAddress, port);
        _remoteServerHost = remoteServerHost;
        _remoteServerPort = remoteServerPort;
        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
        _cryptoPolicy = cryptoPolicy ?? TunnelCryptoPolicy.Default;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _multiplexer = string.IsNullOrWhiteSpace(_remoteServerHost)
            ? null
            : new TunnelClientMultiplexer(_remoteServerHost, _remoteServerPort, _sharedKey, _cryptoPolicy);

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
            if (_multiplexer is not null)
            {
                await _multiplexer.DisposeAsync();
            }

            _listener.Stop();
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
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

        Console.WriteLine($"[socks5] proxy {request.Value.Host}:{request.Value.Port}");

        if (string.IsNullOrWhiteSpace(_remoteServerHost))
        {
            await HandleDirectAsync(clientStream, request.Value, cancellationToken);
            return;
        }

        await HandleViaTunnelAsync(clientStream, request.Value, cancellationToken);
    }

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
        CancellationToken cancellationToken)
    {
        if (_multiplexer is null)
        {
            await SendReplyAsync(clientStream, replyCode: 0x01, null, cancellationToken);
            return;
        }

        var connectRequest = new ConnectRequest(ToProtocolAddressType(request.AddressType), request.Host, (ushort)request.Port);
        uint sessionId;
        byte replyCode;
        ChannelReader<byte[]> reader;
        try
        {
            (sessionId, replyCode, reader) = await _multiplexer.OpenSessionAsync(connectRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[socks5] tunnel connect failed {_remoteServerHost}:{_remoteServerPort} ({ex.Message})");
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
        await RelayViaTunnelAsync(clientStream, _multiplexer, reader, sessionId, cancellationToken);
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

                var payload = new byte[read];
                Buffer.BlockCopy(buffer, 0, payload, 0, read);
                await multiplexer.SendDataAsync(sessionId, payload, relayToken);
            }
        }, relayToken);

        var tunnelToClient = Task.Run(async () =>
        {
            await foreach (var data in reader.ReadAllAsync(relayToken))
            {
                if (data.Length > 0)
                {
                    await clientStream.WriteAsync(data, relayToken);
                    await clientStream.FlushAsync(relayToken);
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

        return new Socks5ConnectRequest(host, port, addressType);
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

    private static async Task ConnectDirectAsync(TcpClient client, Socks5ConnectRequest request, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(request.Host, out var ipAddress))
        {
            await client.ConnectAsync(ipAddress, request.Port, cancellationToken);
            return;
        }

        await client.ConnectAsync(request.Host, request.Port, cancellationToken);
    }

    private static AddressType ToProtocolAddressType(byte socksAddressType)
    {
        return socksAddressType switch
        {
            AddressTypeIPv4 => AddressType.IPv4,
            AddressTypeIPv6 => AddressType.IPv6,
            AddressTypeDomain => AddressType.Domain,
            _ => throw new InvalidDataException($"Unsupported SOCKS address type: {socksAddressType}")
        };
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

    private readonly record struct Socks5ConnectRequest(string Host, int Port, byte AddressType);
}
