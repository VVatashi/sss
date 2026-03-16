using System.Threading.Channels;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer
{
    private sealed class SessionState
    {
        private readonly object _sequenceLock = new();
        private readonly object _connectLock = new();
        private ulong _nextSendSequence;
        private ulong _nextExpectedIncomingSequence;
        private bool _awaitingConnectReply;
        private bool _closed;
        private TaskCompletionSource<byte> _pendingConnectReply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _activeReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsUdp { get; }
        public ConnectRequest? ConnectRequest { get; }
        public Channel<byte[]>? ReaderWriter { get; }
        public Channel<UdpDatagram>? UdpReaderWriter { get; }

        public SessionState(ConnectRequest connectRequest, int receiveChannelCapacity)
        {
            IsUdp = false;
            ConnectRequest = connectRequest;
            ReaderWriter = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(receiveChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public SessionState(int receiveChannelCapacity)
        {
            IsUdp = true;
            UdpReaderWriter = Channel.CreateBounded<UdpDatagram>(new BoundedChannelOptions(receiveChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public bool IsClosed
        {
            get
            {
                lock (_connectLock)
                {
                    return _closed;
                }
            }
        }

        public void BeginConnectAttempt()
        {
            lock (_connectLock)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("Session is closed.");
                }

                _pendingConnectReply = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            lock (_sequenceLock)
            {
                _nextSendSequence = 1;
                _nextExpectedIncomingSequence = 0;
                _awaitingConnectReply = true;
            }
        }

        public Task<byte> WaitForConnectReplyAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<byte> pending;
            lock (_connectLock)
            {
                pending = _pendingConnectReply;
            }

            return pending.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilActiveAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("Session is closed.");
                }

                ready = _activeReady;
            }

            return ready.Task.WaitAsync(cancellationToken);
        }

        public void CompleteConnectReply(byte replyCode)
        {
            TaskCompletionSource<byte> connectReply;
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                connectReply = _pendingConnectReply;
                ready = _activeReady;
            }

            connectReply.TrySetResult(replyCode);
            if (replyCode == 0x00)
            {
                ready.TrySetResult(true);
            }
            else
            {
                ready.TrySetException(new IOException($"Remote CONNECT failed with code {replyCode}."));
            }
        }

        public void FailConnect(Exception ex)
        {
            TaskCompletionSource<byte> connectReply;
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                connectReply = _pendingConnectReply;
                ready = _activeReady;
            }

            connectReply.TrySetException(ex);
            ready.TrySetException(ex);
        }

        public void NotifyConnectionFault(Exception ex)
        {
            lock (_sequenceLock)
            {
                if (!_awaitingConnectReply)
                {
                    return;
                }

                _awaitingConnectReply = false;
            }

            FailConnect(ex);
        }

        public void MarkClosed()
        {
            lock (_connectLock)
            {
                _closed = true;
            }
        }

        public ulong TakeNextSendSequence()
        {
            lock (_sequenceLock)
            {
                return _nextSendSequence++;
            }
        }

        public bool TryAcceptIncomingFrame(FrameType frameType, ulong sequence)
        {
            lock (_sequenceLock)
            {
                if (_awaitingConnectReply)
                {
                    var validConnectReply = IsUdp ? FrameType.UdpAssociate : FrameType.Connect;
                    if (frameType != validConnectReply || sequence != 0)
                    {
                        return false;
                    }

                    _awaitingConnectReply = false;
                    _nextExpectedIncomingSequence = 1;
                    return true;
                }

                if (sequence != _nextExpectedIncomingSequence)
                {
                    return false;
                }

                _nextExpectedIncomingSequence++;
                return true;
            }
        }
    }

    private readonly record struct OutboundFrame(ProtocolFrame Frame, TaskCompletionSource<bool> Completion);
}
