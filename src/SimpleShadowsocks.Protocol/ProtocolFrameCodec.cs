using System.Buffers.Binary;
using System.Buffers;

namespace SimpleShadowsocks.Protocol;

public static class ProtocolFrameCodec
{
    public static void WriteTo(IBufferWriter<byte> writer, ProtocolFrame frame)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        if (frame.Payload.Length > ProtocolConstants.MaxPayloadLength)
        {
            throw new InvalidOperationException($"Payload is too large. Max: {ProtocolConstants.MaxPayloadLength} bytes.");
        }

        var header = writer.GetSpan(ProtocolConstants.HeaderSize);
        header[0] = ProtocolConstants.Version;
        header[1] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(2, 4), frame.SessionId);
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(6, 8), frame.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(14, 4), (uint)frame.Payload.Length);
        writer.Advance(ProtocolConstants.HeaderSize);

        if (!frame.Payload.IsEmpty)
        {
            var payload = writer.GetMemory(frame.Payload.Length);
            frame.Payload.CopyTo(payload);
            writer.Advance(frame.Payload.Length);
        }
    }

    public static async ValueTask WriteAsync(Stream stream, ProtocolFrame frame, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (frame.Payload.Length > ProtocolConstants.MaxPayloadLength)
        {
            throw new InvalidOperationException($"Payload is too large. Max: {ProtocolConstants.MaxPayloadLength} bytes.");
        }

        var header = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize);
        try
        {
            var headerSpan = header.AsSpan(0, ProtocolConstants.HeaderSize);
            headerSpan[0] = ProtocolConstants.Version;
            headerSpan[1] = (byte)frame.Type;
            BinaryPrimitives.WriteUInt32BigEndian(headerSpan.Slice(2, 4), frame.SessionId);
            BinaryPrimitives.WriteUInt64BigEndian(headerSpan.Slice(6, 8), frame.Sequence);
            BinaryPrimitives.WriteUInt32BigEndian(headerSpan.Slice(14, 4), (uint)frame.Payload.Length);

            await stream.WriteAsync(header.AsMemory(0, ProtocolConstants.HeaderSize), cancellationToken);
            if (!frame.Payload.IsEmpty)
            {
                await stream.WriteAsync(frame.Payload, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    public static async ValueTask<ProtocolFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var header = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize);
        try
        {
            var headerRead = await ReadExactlyOrToEndAsync(stream, header, ProtocolConstants.HeaderSize, cancellationToken);

            if (headerRead == 0)
            {
                return null;
            }

            if (headerRead != ProtocolConstants.HeaderSize)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading frame header.");
            }

            var headerSpan = header.AsSpan(0, ProtocolConstants.HeaderSize);
            var version = headerSpan[0];
            if (version != ProtocolConstants.Version)
            {
                throw new InvalidDataException($"Unsupported frame version: {version}.");
            }

            var frameType = (FrameType)headerSpan[1];
            if (!Enum.IsDefined(frameType))
            {
                throw new InvalidDataException($"Unsupported frame type: {(byte)frameType}.");
            }

            var sessionId = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(2, 4));
            var sequence = BinaryPrimitives.ReadUInt64BigEndian(headerSpan.Slice(6, 8));
            var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(14, 4));

            if (payloadLength > ProtocolConstants.MaxPayloadLength)
            {
                throw new InvalidDataException($"Frame payload too large: {payloadLength}.");
            }

            var payload = payloadLength == 0
                ? Array.Empty<byte>()
                : new byte[payloadLength];

            if (payloadLength > 0)
            {
                await ReadExactlyAsync(stream, payload, cancellationToken);
            }

            return new ProtocolFrame(frameType, sessionId, sequence, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    private static async ValueTask<int> ReadExactlyOrToEndAsync(
        Stream stream,
        byte[] buffer,
        int length,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                return offset;
            }

            offset += read;
        }

        return offset;
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading frame payload.");
            }

            offset += read;
        }
    }
}
