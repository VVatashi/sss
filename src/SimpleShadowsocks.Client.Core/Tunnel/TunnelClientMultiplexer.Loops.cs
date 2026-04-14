using System.IO.Pipelines;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tunnel;

public sealed partial class TunnelClientMultiplexer
{
    private async Task ReadLoopAsync(int generation, CancellationToken cancellationToken)
    {
        var preserveSessionsOnFault = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_secureStream is null)
                {
                    return;
                }

                var frameResult = await ProtocolFrameCodec.ReadDetailedAsync(_secureStream, cancellationToken);
                if (frameResult is null)
                {
                    break;
                }
                var frame = frameResult.Value.Frame;

                if (frameResult.Value.Version != _protocolVersion)
                {
                    throw new InvalidDataException(
                        $"Protocol version mismatch: client expects v{_protocolVersion}, server replied with v{frameResult.Value.Version}.");
                }

                TouchIncoming();

                if (frame.SessionId == 0)
                {
                    if (frame.Type == FrameType.Ping)
                    {
                        var sequence = (ulong)Interlocked.Increment(ref _controlSendSequence);
                        await SendFrameAsync(new ProtocolFrame(FrameType.Pong, 0, sequence, frame.Payload), cancellationToken);
                    }

                    continue;
                }

                if (!_sessions.TryGetValue(frame.SessionId, out var state))
                {
                    if (await TryHandleIncomingReverseHttpFrameAsync(frame, cancellationToken))
                    {
                        continue;
                    }

                    continue;
                }

                if (!state.TryAcceptIncomingFrame(frame.Type, frame.Sequence))
                {
                    await CloseSessionAsync(frame.SessionId, 0x21, CancellationToken.None);
                    continue;
                }

                switch (frame.Type)
                {
                    case FrameType.Connect:
                    case FrameType.UdpAssociate:
                        state.CompleteConnectReply(frame.Payload.Length >= 1 ? frame.Payload.Span[0] : (byte)0x01);
                        break;

                    case FrameType.HttpResponse:
                        try
                        {
                            state.CompleteHttpResponse(ProtocolPayloadSerializer.DeserializeHttpResponseStart(frame.Payload.Span));
                        }
                        catch (Exception ex)
                        {
                            StructuredLog.Error(
                                "tunnel-client",
                                "TUNNEL/HTTP",
                                "invalid HTTP response metadata",
                                ex,
                                frame.SessionId);
                            await CloseSessionAsync(frame.SessionId, 0x08, CancellationToken.None);
                        }
                        break;

                    case FrameType.Data:
                        if (state.ReaderWriter is not null)
                        {
                            await state.ReaderWriter.Writer.WriteAsync(DetachPayload(frame.Payload), cancellationToken);
                        }
                        break;

                    case FrameType.UdpData:
                        if (state.UdpReaderWriter is not null)
                        {
                            try
                            {
                                var datagram = ProtocolPayloadSerializer.DeserializeUdpDatagram(frame.Payload.Span);
                                await state.UdpReaderWriter.Writer.WriteAsync(datagram, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                StructuredLog.Error(
                                    "tunnel-client",
                                    "TUNNEL/UDP",
                                    "invalid UDP frame payload",
                                    ex,
                                    frame.SessionId);
                                try
                                {
                                    await CloseSessionAsync(frame.SessionId, 0x08, CancellationToken.None);
                                }
                                catch
                                {
                                }
                            }
                        }
                        break;

                    case FrameType.Close:
                        state.MarkClosed();
                        state.FailConnect(new IOException("Session closed by remote."));
                        state.ReaderWriter?.Writer.TryComplete();
                        state.UdpReaderWriter?.Writer.TryComplete();
                        _sessions.TryRemove(frame.SessionId, out _);
                        StructuredLog.Info(
                            "tunnel-client",
                            state.IsUdp ? "TUNNEL/UDP" : state.IsHttp ? "TUNNEL/HTTP" : "TUNNEL/TCP",
                            "session closed by remote",
                            frame.SessionId);
                        break;

                    case FrameType.Ping:
                        await SendSessionFrameAsync(frame.SessionId, FrameType.Pong, frame.Payload, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException ex) when (_protocolVersion != ProtocolConstants.LegacyVersion)
        {
            _connectionError = new IOException(
                $"Tunnel closed unexpectedly while using protocol v{_protocolVersion}. Possible protocol version mismatch with server.",
                ex);
            throw _connectionError;
        }
        catch (Exception ex)
        {
            if (ex is InvalidDataException)
            {
                // AEAD auth failure / protocol corruption is fatal:
                // do not keep sessions waiting for reconnect, fail them fast.
                preserveSessionsOnFault = false;
            }

            _connectionError = ex;
            throw new IOException(
                $"Tunnel read loop failed: {ex.Message}",
                ex);
        }
        finally
        {
            await HandleConnectionFaultAsync(preserveSessions: preserveSessionsOnFault, expectedGeneration: generation);
        }
    }

    private async Task WriteLoopAsync(int generation, CancellationToken cancellationToken)
    {
        var outboundFrames = _outboundFrames;
        if (_secureStream is null || outboundFrames is null)
        {
            return;
        }

        var writer = PipeWriter.Create(_secureStream, new StreamPipeWriterOptions(leaveOpen: true));
        var batch = new List<OutboundFrame>(64);
        const int maxBatchFrames = 64;
        const int maxBatchBytes = 512 * 1024;

        Exception? terminalError = null;
        try
        {
            while (await outboundFrames.Reader.WaitToReadAsync(cancellationToken))
            {
                batch.Clear();
                var accumulatedBytes = 0;
                while (outboundFrames.Reader.TryRead(out var outbound))
                {
                    try
                    {
                        ProtocolFrameCodec.WriteTo(writer, outbound.Frame, _writeOptions);
                        batch.Add(outbound);
                        accumulatedBytes += ProtocolConstants.HeaderSize + outbound.Frame.Payload.Length;

                        if (batch.Count >= maxBatchFrames || accumulatedBytes >= maxBatchBytes)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        outbound.Completion.TrySetException(ex);
                        throw;
                    }
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                var flushResult = await writer.FlushAsync(cancellationToken);
                if (flushResult.IsCanceled)
                {
                    throw new OperationCanceledException("Outbound tunnel writer flush was canceled.");
                }

                foreach (var sent in batch)
                {
                    sent.Completion.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            terminalError = new OperationCanceledException("Outbound tunnel writer was canceled.");
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            try { await writer.CompleteAsync(); } catch { }

            while (outboundFrames.Reader.TryRead(out var outbound))
            {
                outbound.Completion.TrySetException(terminalError ?? new IOException("Tunnel writer stopped."));
            }

            await HandleConnectionFaultAsync(preserveSessions: true, expectedGeneration: generation);
        }
    }

    private async Task HeartbeatLoopAsync(int generation, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _connectionPolicy.HeartbeatIntervalSeconds));
        var idleTimeout = TimeSpan.FromSeconds(Math.Max(5, _connectionPolicy.IdleTimeoutSeconds));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);

                var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastIncomingUtcTicks), DateTimeKind.Utc);
                if (idle > idleTimeout)
                {
                    throw new TimeoutException($"Tunnel idle timeout exceeded ({idleTimeout}).");
                }

                var seq = (ulong)Interlocked.Increment(ref _controlSendSequence);
                await SendFrameAsync(new ProtocolFrame(FrameType.Ping, 0, seq, Array.Empty<byte>()), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await HandleConnectionFaultAsync(preserveSessions: true, expectedGeneration: generation);
        }
    }
}
