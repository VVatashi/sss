using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Server.Tunnel;

namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class TunnelPendingFrameLifecycleTests
{
    [Fact]
    public void ClientSessionState_RebasesPendingFrames_AndPreservesPayload()
    {
        var sessionStateType = typeof(TunnelClientMultiplexer).GetNestedType("SessionState", BindingFlags.NonPublic);
        Assert.NotNull(sessionStateType);

        var ctor = sessionStateType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            [typeof(ConnectRequest), typeof(int)],
            modifiers: null);
        Assert.NotNull(ctor);

        using var sessionState = (IDisposable)ctor!.Invoke([new ConnectRequest(AddressType.Domain, "example.com", 443), 4]);
        Invoke(sessionState, "BeginConnectAttempt", 17u, false);

        var firstPayload = "first"u8.ToArray();
        var secondOwner = new byte[] { 0, (byte)'s', (byte)'e', (byte)'c', (byte)'o', (byte)'n', (byte)'d', 0 };
        Invoke(sessionState, "CreateTrackedOutboundFrame", 17u, FrameType.Data, new ReadOnlyMemory<byte>(firstPayload));
        Invoke(sessionState, "CreateTrackedOutboundFrame", 17u, FrameType.Data, new ReadOnlyMemory<byte>(secondOwner, 1, 6));
        Invoke(sessionState, "AcknowledgeOutgoingThrough", 1UL);

        var beforeRebase = SnapshotFrames(sessionState, sessionId: 17u, methodName: "SnapshotPendingOutgoingFrames");
        var pendingBefore = Assert.Single(beforeRebase);
        Assert.Equal((ulong)2, pendingBefore.Sequence);
        Assert.Equal("second", System.Text.Encoding.ASCII.GetString(pendingBefore.Payload.Span));

        Invoke(sessionState, "BeginConnectAttempt", 17u, true);

        var afterRebase = SnapshotFrames(sessionState, sessionId: 17u, methodName: "SnapshotPendingOutgoingFrames");
        var pendingAfter = Assert.Single(afterRebase);
        Assert.Equal((ulong)1, pendingAfter.Sequence);
        Assert.Equal("second", System.Text.Encoding.ASCII.GetString(pendingAfter.Payload.Span));

        Invoke(sessionState, "AcknowledgeOutgoingThrough", 1UL);
        Assert.Empty(SnapshotFrames(sessionState, sessionId: 17u, methodName: "SnapshotPendingOutgoingFrames"));
    }

    [Fact]
    public void ClientSessionState_CopiesTrackedPayload_FromReusableSourceBuffer()
    {
        var sessionStateType = typeof(TunnelClientMultiplexer).GetNestedType("SessionState", BindingFlags.NonPublic);
        Assert.NotNull(sessionStateType);

        var ctor = sessionStateType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            [typeof(ConnectRequest), typeof(int)],
            modifiers: null);
        Assert.NotNull(ctor);

        using var sessionState = (IDisposable)ctor!.Invoke([new ConnectRequest(AddressType.Domain, "example.com", 443), 4]);
        Invoke(sessionState, "BeginConnectAttempt", 17u, false);

        var reusableBuffer = "original-payload"u8.ToArray();
        Invoke(sessionState, "CreateTrackedOutboundFrame", 17u, FrameType.Data, new ReadOnlyMemory<byte>(reusableBuffer));
        Array.Fill(reusableBuffer, (byte)'x');

        var frames = SnapshotFrames(sessionState, sessionId: 17u, methodName: "SnapshotPendingOutgoingFrames");
        var pending = Assert.Single(frames);
        Assert.Equal("original-payload", System.Text.Encoding.ASCII.GetString(pending.Payload.Span));
    }

    [Fact]
    public void ServerSessionContext_AcknowledgesAndReplaysTrackedFrames_WithoutPayloadCopies()
    {
        var sessionContextType = typeof(TunnelServer).GetNestedType("UdpSessionContext", BindingFlags.NonPublic);
        Assert.NotNull(sessionContextType);

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sessionCts = new CancellationTokenSource();
        var sessionCtor = sessionContextType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(uint), typeof(UdpClient), typeof(CancellationTokenSource)],
            modifiers: null);
        Assert.NotNull(sessionCtor);

        using var sessionContext = (IDisposable)sessionCtor!.Invoke([23u, udpClient, sessionCts]);

        Invoke(sessionContext, "MarkConnectReceived");
        var payloadOwner = new byte[] { 1, 2, 3, 4, 5, 6 };
        Invoke(sessionContext, "CreateTrackedOutboundFrame", FrameType.Data, new ReadOnlyMemory<byte>(payloadOwner, 1, 4));
        Invoke(sessionContext, "CreateTrackedOutboundFrame", FrameType.Data, new ReadOnlyMemory<byte>(new byte[] { 7, 8, 9 }));
        Invoke(sessionContext, "AcknowledgeOutgoingThrough", 1UL);

        var frames = SnapshotFrames(sessionContext, sessionId: 23u, methodName: "SnapshotPendingOutgoingFramesFrom", 1UL);
        var pending = Assert.Single(frames);
        Assert.Equal((ulong)2, pending.Sequence);
        Assert.True(pending.Payload.Span.SequenceEqual(new byte[] { 7, 8, 9 }));
    }

    [Fact]
    public void ServerSessionContext_CopiesTrackedPayload_FromReusableSourceBuffer()
    {
        var sessionContextType = typeof(TunnelServer).GetNestedType("UdpSessionContext", BindingFlags.NonPublic);
        Assert.NotNull(sessionContextType);

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sessionCts = new CancellationTokenSource();
        var sessionCtor = sessionContextType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(uint), typeof(UdpClient), typeof(CancellationTokenSource)],
            modifiers: null);
        Assert.NotNull(sessionCtor);

        using var sessionContext = (IDisposable)sessionCtor!.Invoke([23u, udpClient, sessionCts]);

        Invoke(sessionContext, "MarkConnectReceived");
        var reusableBuffer = "server-buffer"u8.ToArray();
        Invoke(sessionContext, "CreateTrackedOutboundFrame", FrameType.Data, new ReadOnlyMemory<byte>(reusableBuffer));
        Array.Fill(reusableBuffer, (byte)'y');

        var frames = SnapshotFrames(sessionContext, sessionId: 23u, methodName: "SnapshotPendingOutgoingFramesFrom", 1UL);
        var pending = Assert.Single(frames);
        Assert.Equal("server-buffer", System.Text.Encoding.ASCII.GetString(pending.Payload.Span));
    }

    private static ProtocolFrame[] SnapshotFrames(object instance, uint sessionId, string methodName, params object[] extraArgs)
    {
        var result = Invoke(instance, methodName, extraArgs);
        var pendingFrames = ((IEnumerable)result!).Cast<object>().ToArray();
        return pendingFrames
            .Select(frame => frame is ProtocolFrame protocolFrame
                ? protocolFrame
                : (ProtocolFrame)Invoke(frame, "ToProtocolFrame", sessionId)!)
            .ToArray();
    }

    private static object? Invoke(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(instance, args);
    }
}
