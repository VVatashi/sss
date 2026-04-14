using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Tests;

public sealed partial class TunnelIntegrationTests
{
    [Fact]
    public async Task TunnelClientMultiplexer_RequestsRecovery_ForOutOfOrderServerFrames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sharedKey = PreSharedKey.Derive32Bytes("dev-shared-key");

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(cts.Token);
            await using var secure = await TunnelCryptoHandshake.AsServerAsync(
                accepted.GetStream(),
                sharedKey,
                TunnelCryptoPolicy.Default,
                cts.Token);

            var connectFrame = await ReadTunnelFrameAsync(secure, cts.Token);
            Assert.NotNull(connectFrame);
            Assert.Equal(FrameType.Connect, connectFrame!.Value.Type);

            await ProtocolFrameCodec.WriteAsync(
                secure,
                new ProtocolFrame(FrameType.Connect, connectFrame.Value.SessionId, 0, new byte[] { 0x00 }),
                cts.Token,
                CreateWriteOptions());

            await ProtocolFrameCodec.WriteAsync(
                secure,
                new ProtocolFrame(FrameType.Data, connectFrame.Value.SessionId, 2, Encoding.ASCII.GetBytes("world")),
                cts.Token,
                CreateWriteOptions());

            var recoverFrame = await ReadTunnelFrameAsync(secure, cts.Token, FrameType.Recover);
            Assert.NotNull(recoverFrame);
            Assert.Equal((ulong)1, recoverFrame!.Value.Sequence);

            await ProtocolFrameCodec.WriteAsync(
                secure,
                new ProtocolFrame(FrameType.Data, connectFrame.Value.SessionId, 1, Encoding.ASCII.GetBytes("hello")),
                cts.Token,
                CreateWriteOptions());
            await ProtocolFrameCodec.WriteAsync(
                secure,
                new ProtocolFrame(FrameType.Data, connectFrame.Value.SessionId, 2, Encoding.ASCII.GetBytes("world")),
                cts.Token,
                CreateWriteOptions());
            await Task.Delay(250);
        }, cts.Token);

        await using var multiplexer = new TunnelClientMultiplexer(
            "127.0.0.1",
            port,
            sharedKey,
            TunnelCryptoPolicy.Default,
            new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 5,
                IdleTimeoutSeconds = 20,
                ReconnectBaseDelayMs = 50,
                ReconnectMaxDelayMs = 100,
                ReconnectMaxAttempts = 5
            });

        var (_, replyCode, reader) = await multiplexer.OpenSessionAsync(
            new ConnectRequest(AddressType.IPv4, IPAddress.Loopback.ToString(), 80),
            cts.Token);
        Assert.Equal((byte)0x00, replyCode);

        var first = await reader.ReadAsync(cts.Token);
        var second = await reader.ReadAsync(cts.Token);
        Assert.Equal("hello", Encoding.ASCII.GetString(first));
        Assert.Equal("world", Encoding.ASCII.GetString(second));

        await serverTask;
        listener.Stop();
    }

    [Fact]
    public async Task TunnelClientMultiplexer_ReplaysUnacknowledgedFrames_AfterReconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sharedKey = PreSharedKey.Derive32Bytes("dev-shared-key");
        var replayObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await HandleReconnectStageAsync(expectReplay: false, sendAck: false, sendResponse: false, cts.Token);
            await HandleReconnectStageAsync(expectReplay: true, sendAck: true, sendResponse: true, cts.Token);
        }, cts.Token);

        await using var multiplexer = new TunnelClientMultiplexer(
            "127.0.0.1",
            port,
            sharedKey,
            TunnelCryptoPolicy.Default,
            new TunnelConnectionPolicy
            {
                HeartbeatIntervalSeconds = 1,
                IdleTimeoutSeconds = 10,
                ReconnectBaseDelayMs = 50,
                ReconnectMaxDelayMs = 100,
                ReconnectMaxAttempts = 20
            });

        var persistentTask = multiplexer.RunPersistentAsync(cts.Token);
        var (sessionId, replyCode, reader) = await multiplexer.OpenSessionAsync(
            new ConnectRequest(AddressType.IPv4, IPAddress.Loopback.ToString(), 443),
            cts.Token);
        Assert.Equal((byte)0x00, replyCode);

        await multiplexer.SendDataAsync(sessionId, Encoding.ASCII.GetBytes("replay-me"), cts.Token);
        await replayObserved.Task.WaitAsync(cts.Token);

        var echoed = await reader.ReadAsync(cts.Token);
        Assert.Equal("replayed-ok", Encoding.ASCII.GetString(echoed));

        cts.Cancel();
        try
        {
            await persistentTask;
        }
        catch (OperationCanceledException)
        {
        }

        await serverTask;
        listener.Stop();

        async Task HandleReconnectStageAsync(bool expectReplay, bool sendAck, bool sendResponse, CancellationToken cancellationToken)
        {
            using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var secure = await TunnelCryptoHandshake.AsServerAsync(
                accepted.GetStream(),
                sharedKey,
                TunnelCryptoPolicy.Default,
                cancellationToken);

            var connectFrame = await ReadTunnelFrameAsync(secure, cancellationToken, FrameType.Connect);
            Assert.NotNull(connectFrame);

            await ProtocolFrameCodec.WriteAsync(
                secure,
                new ProtocolFrame(FrameType.Connect, connectFrame!.Value.SessionId, 0, new byte[] { 0x00 }),
                cancellationToken,
                CreateWriteOptions());

            var dataFrame = await ReadTunnelFrameAsync(secure, cancellationToken, FrameType.Data);
            Assert.NotNull(dataFrame);
            Assert.Equal("replay-me", Encoding.ASCII.GetString(dataFrame!.Value.Payload.Span));

            if (expectReplay)
            {
                replayObserved.TrySetResult(true);
            }

            if (sendAck)
            {
                await ProtocolFrameCodec.WriteAsync(
                    secure,
                    new ProtocolFrame(FrameType.Ack, connectFrame.Value.SessionId, dataFrame.Value.Sequence, Array.Empty<byte>()),
                    cancellationToken,
                    CreateWriteOptions());
            }

            if (sendResponse)
            {
                await ProtocolFrameCodec.WriteAsync(
                    secure,
                    new ProtocolFrame(FrameType.Data, connectFrame.Value.SessionId, 1, Encoding.ASCII.GetBytes("replayed-ok")),
                    cancellationToken,
                    CreateWriteOptions());
                await Task.Delay(250);
            }
        }
    }

    private static ProtocolWriteOptions CreateWriteOptions()
    {
        return new ProtocolWriteOptions
        {
            Version = ProtocolConstants.Version
        };
    }

    private static async Task<ProtocolFrame?> ReadTunnelFrameAsync(
        Stream secureStream,
        CancellationToken cancellationToken,
        params FrameType[] expectedFrameTypes)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frameResult = await ProtocolFrameCodec.ReadDetailedAsync(secureStream, cancellationToken);
            if (frameResult is null)
            {
                return null;
            }

            var frame = frameResult.Value.Frame;
            if (frame.SessionId == 0)
            {
                if (frame.Type == FrameType.Ping)
                {
                    await ProtocolFrameCodec.WriteAsync(
                        secureStream,
                        new ProtocolFrame(FrameType.Pong, 0, frame.Sequence, frame.Payload),
                        cancellationToken,
                        CreateWriteOptions());
                }

                continue;
            }

            if (expectedFrameTypes.Length == 0 || expectedFrameTypes.Contains(frame.Type))
            {
                return frame;
            }
        }

        return null;
    }
}
