using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Server.Tunnel;

public sealed class TunnelServer
{
    private readonly TcpListener _listener;
    private readonly byte[] _sharedKey;
    private readonly TunnelCryptoPolicy _cryptoPolicy;
    private int _acceptedTunnelConnections;

    public int AcceptedTunnelConnections => Volatile.Read(ref _acceptedTunnelConnections);

    public TunnelServer(IPAddress listenAddress, int port)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes("dev-shared-key");
        _cryptoPolicy = TunnelCryptoPolicy.Default;
    }

    public TunnelServer(IPAddress listenAddress, int port, string sharedKey, TunnelCryptoPolicy? cryptoPolicy = null)
    {
        _listener = new TcpListener(listenAddress, port);
        _sharedKey = PreSharedKey.Derive32Bytes(sharedKey);
        _cryptoPolicy = cryptoPolicy ?? TunnelCryptoPolicy.Default;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tunnelClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                Interlocked.Increment(ref _acceptedTunnelConnections);
                _ = Task.Run(
                    () => HandleTunnelSafelyAsync(tunnelClient, _sharedKey, _cryptoPolicy, cancellationToken),
                    cancellationToken);
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

    private static async Task HandleTunnelSafelyAsync(
        TcpClient tunnelClient,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
        CancellationToken cancellationToken)
    {
        using (tunnelClient)
        {
            try
            {
                await HandleTunnelAsync(tunnelClient, sharedKey, cryptoPolicy, cancellationToken);
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

    private static async Task HandleTunnelAsync(
        TcpClient tunnelClient,
        byte[] sharedKey,
        TunnelCryptoPolicy cryptoPolicy,
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

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ProtocolFrameCodec.ReadAsync(secureStream, cancellationToken);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Value.Type)
                {
                    case FrameType.Connect:
                        await HandleConnectFrameAsync(secureStream, writeLock, sessions, frame.Value, cancellationToken);
                        break;

                    case FrameType.Data:
                        if (sessions.TryGetValue(frame.Value.SessionId, out var session) && !frame.Value.Payload.IsEmpty)
                        {
                            await session.UpstreamStream.WriteAsync(frame.Value.Payload, cancellationToken);
                            await session.UpstreamStream.FlushAsync(cancellationToken);
                        }
                        break;

                    case FrameType.Close:
                        await CloseSessionAsync(sessions, frame.Value.SessionId, sendCloseToClient: false, secureStream, writeLock, cancellationToken);
                        break;

                    case FrameType.Ping:
                        await SendFrameLockedAsync(
                            secureStream,
                            new ProtocolFrame(FrameType.Pong, frame.Value.SessionId, frame.Value.Payload),
                            writeLock,
                            cancellationToken);
                        break;
                }
            }
        }
        finally
        {
            foreach (var sessionId in sessions.Keys)
            {
                await CloseSessionAsync(sessions, sessionId, sendCloseToClient: false, secureStream, writeLock, CancellationToken.None);
            }
        }
    }

    private static async Task HandleConnectFrameAsync(
        Stream secureStream,
        SemaphoreSlim writeLock,
        ConcurrentDictionary<uint, SessionContext> sessions,
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        if (sessions.ContainsKey(frame.SessionId))
        {
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, cancellationToken);
            return;
        }

        ConnectRequest request;
        try
        {
            request = ProtocolPayloadSerializer.DeserializeConnectRequest(frame.Payload.Span);
        }
        catch
        {
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x08, writeLock, cancellationToken);
            return;
        }

        var upstreamClient = new TcpClient();
        try
        {
            await ConnectUpstreamAsync(upstreamClient, request, cancellationToken);
        }
        catch (Exception ex)
        {
            upstreamClient.Dispose();
            Console.WriteLine($"[tunnel] connect failed {request.Address}:{request.Port} ({ex.Message})");
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x05, writeLock, cancellationToken);
            return;
        }

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new SessionContext(frame.SessionId, upstreamClient, upstreamClient.GetStream(), sessionCts);
        if (!sessions.TryAdd(frame.SessionId, context))
        {
            context.Dispose();
            await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x01, writeLock, cancellationToken);
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

                    var payload = new byte[read];
                    Buffer.BlockCopy(buffer, 0, payload, 0, read);
                    await SendFrameLockedAsync(
                        secureStream,
                        new ProtocolFrame(FrameType.Data, context.SessionId, payload),
                        writeLock,
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
                    CancellationToken.None);
            }
        }, context.Cancellation.Token);

        Console.WriteLine($"[tunnel] proxy {request.Address}:{request.Port} session={frame.SessionId}");
        await SendConnectReplyAsync(secureStream, frame.SessionId, replyCode: 0x00, writeLock, cancellationToken);
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
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        return SendFrameLockedAsync(
            stream,
            new ProtocolFrame(FrameType.Connect, sessionId, new[] { replyCode }),
            writeLock,
            cancellationToken);
    }

    private static async Task CloseSessionAsync(
        ConcurrentDictionary<uint, SessionContext> sessions,
        uint sessionId,
        bool sendCloseToClient,
        Stream secureStream,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryRemove(sessionId, out var context))
        {
            return;
        }

        context.Cancellation.Cancel();
        context.Dispose();

        if (sendCloseToClient)
        {
            try
            {
                await SendFrameLockedAsync(
                    secureStream,
                    new ProtocolFrame(FrameType.Close, sessionId, ProtocolPayloadSerializer.SerializeClose(0x00)),
                    writeLock,
                    cancellationToken);
            }
            catch
            {
            }
        }
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

    private sealed class SessionContext : IDisposable
    {
        public SessionContext(uint sessionId, TcpClient upstreamClient, NetworkStream upstreamStream, CancellationTokenSource cancellation)
        {
            SessionId = sessionId;
            UpstreamClient = upstreamClient;
            UpstreamStream = upstreamStream;
            Cancellation = cancellation;
        }

        public uint SessionId { get; }
        public TcpClient UpstreamClient { get; }
        public NetworkStream UpstreamStream { get; }
        public CancellationTokenSource Cancellation { get; }
        public Task? UpstreamToClientTask { get; set; }

        public void Dispose()
        {
            try { UpstreamStream.Dispose(); } catch { }
            try { UpstreamClient.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
        }
    }
}
