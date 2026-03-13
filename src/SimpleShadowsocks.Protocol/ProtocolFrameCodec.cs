using System.Buffers.Binary;

namespace SimpleShadowsocks.Protocol;

public static class ProtocolFrameCodec
{
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

        var header = new byte[ProtocolConstants.HeaderSize];
        header[0] = ProtocolConstants.Version;
        header[1] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2, 4), frame.SessionId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), (uint)frame.Payload.Length);

        await stream.WriteAsync(header, cancellationToken);
        if (!frame.Payload.IsEmpty)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken);
        }
    }

    public static async ValueTask<ProtocolFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var header = new byte[ProtocolConstants.HeaderSize];
        var headerRead = await ReadExactlyOrToEndAsync(stream, header, cancellationToken);

        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead != ProtocolConstants.HeaderSize)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading frame header.");
        }

        var version = header[0];
        if (version != ProtocolConstants.Version)
        {
            throw new InvalidDataException($"Unsupported frame version: {version}.");
        }

        var frameType = (FrameType)header[1];
        var sessionId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6, 4));

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

        return new ProtocolFrame(frameType, sessionId, payload);
    }

    private static async ValueTask<int> ReadExactlyOrToEndAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
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
