using System.Threading.Channels;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer
{
    private enum SessionKind
    {
        Tcp = 0,
        Udp = 1,
        Http = 2
    }

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
        private TaskCompletionSource<HttpResponseStart> _pendingHttpResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _activeReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SessionKind Kind { get; }
        public bool IsUdp { get; }
        public bool IsHttp => Kind == SessionKind.Http;
        public bool IsResumable => !IsHttp;
        public ConnectRequest? ConnectRequest { get; }
        public HttpRequestStart? HttpRequestStart { get; }
        public Channel<byte[]>? ReaderWriter { get; }
        public Channel<UdpDatagram>? UdpReaderWriter { get; }

        public SessionState(ConnectRequest connectRequest, int receiveChannelCapacity)
        {
            Kind = SessionKind.Tcp;
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
            Kind = SessionKind.Udp;
            IsUdp = true;
            UdpReaderWriter = Channel.CreateBounded<UdpDatagram>(new BoundedChannelOptions(receiveChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public SessionState(HttpRequestStart httpRequestStart, int receiveChannelCapacity)
        {
            Kind = SessionKind.Http;
            IsUdp = false;
            HttpRequestStart = httpRequestStart;
            ReaderWriter = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(receiveChannelCapacity)
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
                _pendingHttpResponse = new TaskCompletionSource<HttpResponseStart>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        public Task<HttpResponseStart> WaitForHttpResponseAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<HttpResponseStart> pending;
            lock (_connectLock)
            {
                pending = _pendingHttpResponse;
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
            TaskCompletionSource<HttpResponseStart> httpResponse;
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                connectReply = _pendingConnectReply;
                httpResponse = _pendingHttpResponse;
                ready = _activeReady;
            }

            connectReply.TrySetResult(replyCode);
            httpResponse.TrySetException(new InvalidOperationException("Expected HTTP response, but received CONNECT reply."));
            if (replyCode == 0x00)
            {
                ready.TrySetResult(true);
            }
            else
            {
                ready.TrySetException(new IOException($"Remote CONNECT failed with code {replyCode}."));
            }
        }

        public void CompleteHttpResponse(HttpResponseStart response)
        {
            TaskCompletionSource<byte> connectReply;
            TaskCompletionSource<HttpResponseStart> httpResponse;
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                connectReply = _pendingConnectReply;
                httpResponse = _pendingHttpResponse;
                ready = _activeReady;
            }

            connectReply.TrySetException(new InvalidOperationException("Expected CONNECT reply, but received HTTP response."));
            httpResponse.TrySetResult(response);
            ready.TrySetResult(true);
        }

        public void FailConnect(Exception ex)
        {
            TaskCompletionSource<byte> connectReply;
            TaskCompletionSource<HttpResponseStart> httpResponse;
            TaskCompletionSource<bool> ready;
            lock (_connectLock)
            {
                connectReply = _pendingConnectReply;
                httpResponse = _pendingHttpResponse;
                ready = _activeReady;
            }

            connectReply.TrySetException(ex);
            httpResponse.TrySetException(ex);
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
                    var validConnectReply = Kind switch
                    {
                        SessionKind.Udp => FrameType.UdpAssociate,
                        SessionKind.Http => FrameType.HttpResponse,
                        _ => FrameType.Connect
                    };

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

    private sealed class IncomingReverseHttpSession : IDisposable
    {
        private readonly object _sequenceLock = new();
        private ulong _nextSendSequence = 1;
        private ulong _nextExpectedIncomingSequence = 1;
        private int _requestCompleted;
        private int _responseStarted;

        public IncomingReverseHttpSession(uint sessionId, HttpRequestStart requestStart)
        {
            SessionId = sessionId;
            RequestStart = requestStart;
            RequestBody = new MemoryStream();
        }

        public uint SessionId { get; }
        public HttpRequestStart RequestStart { get; }
        public MemoryStream RequestBody { get; }
        public bool HasCompletedRequest => Volatile.Read(ref _requestCompleted) == 1;

        public bool TryAcceptIncomingSequence(ulong sequence)
        {
            lock (_sequenceLock)
            {
                if (sequence != _nextExpectedIncomingSequence)
                {
                    return false;
                }

                _nextExpectedIncomingSequence++;
                return true;
            }
        }

        public ulong TakeNextSendSequence()
        {
            lock (_sequenceLock)
            {
                return _nextSendSequence++;
            }
        }

        public bool TryMarkRequestCompleted()
        {
            return Interlocked.CompareExchange(ref _requestCompleted, 1, 0) == 0;
        }

        public bool TryStartResponse()
        {
            return Interlocked.CompareExchange(ref _responseStarted, 1, 0) == 0;
        }

        public void Dispose()
        {
            try { RequestBody.Dispose(); } catch { }
        }
    }

    private readonly record struct OutboundFrame(ProtocolFrame Frame, TaskCompletionSource<bool> Completion);
}
