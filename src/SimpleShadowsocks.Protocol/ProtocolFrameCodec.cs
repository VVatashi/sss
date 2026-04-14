using System.Buffers.Binary;
using System.Buffers;
using SimpleShadowsocks.Protocol.Compression;

namespace SimpleShadowsocks.Protocol;

public static class ProtocolFrameCodec
{
    public static PayloadCompressionAlgorithm GetCompressionAlgorithm(byte flags)
    {
        return PayloadCompressionCodecFactory.FromFlags(flags);
    }

    public static void WriteTo(IBufferWriter<byte> writer, ProtocolFrame frame, ProtocolWriteOptions? options = null)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        options ??= ProtocolWriteOptions.V2NoCompression;

        if (frame.Payload.Length > ProtocolConstants.MaxPayloadLength)
        {
            throw new InvalidOperationException($"Payload is too large. Max: {ProtocolConstants.MaxPayloadLength} bytes.");
        }

        var effectiveVersion = options.Version;
        if (!ProtocolConstants.IsSupported(effectiveVersion))
        {
            throw new InvalidOperationException($"Unsupported write protocol version: {effectiveVersion}.");
        }

        var flags = ProtocolFlags.None;
        EncodedPayload encodedPayload;
        if (ProtocolConstants.SupportsCompression(effectiveVersion))
        {
            encodedPayload = EncodePayload(
                frame.Payload,
                options.EnableCompression,
                options.CompressionAlgorithm,
                options.CompressionMinBytes,
                options.CompressionMinSavingsBytes);
            flags = encodedPayload.Flags;
        }
        else
        {
            encodedPayload = EncodedPayload.FromSource(frame.Payload);
        }

        try
        {
            if (effectiveVersion == ProtocolConstants.LegacyVersion)
            {
                var headerV1 = writer.GetSpan(ProtocolConstants.HeaderSizeV1);
                headerV1[0] = ProtocolConstants.LegacyVersion;
                headerV1[1] = (byte)frame.Type;
                BinaryPrimitives.WriteUInt32BigEndian(headerV1.Slice(2, 4), frame.SessionId);
                BinaryPrimitives.WriteUInt64BigEndian(headerV1.Slice(6, 8), frame.Sequence);
                BinaryPrimitives.WriteUInt32BigEndian(headerV1.Slice(14, 4), (uint)encodedPayload.Length);
                writer.Advance(ProtocolConstants.HeaderSizeV1);
            }
            else
            {
                var headerV2 = writer.GetSpan(ProtocolConstants.HeaderSizeV2);
                headerV2[0] = effectiveVersion;
                headerV2[1] = (byte)frame.Type;
                headerV2[2] = flags;
                BinaryPrimitives.WriteUInt32BigEndian(headerV2.Slice(3, 4), frame.SessionId);
                BinaryPrimitives.WriteUInt64BigEndian(headerV2.Slice(7, 8), frame.Sequence);
                BinaryPrimitives.WriteUInt32BigEndian(headerV2.Slice(15, 4), (uint)encodedPayload.Length);
                writer.Advance(ProtocolConstants.HeaderSizeV2);
            }

            if (encodedPayload.Length > 0)
            {
                var payloadMemory = writer.GetMemory(encodedPayload.Length);
                encodedPayload.Payload.Span.CopyTo(payloadMemory.Span);
                writer.Advance(encodedPayload.Length);
            }
        }
        finally
        {
            encodedPayload.Dispose();
        }
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        ProtocolFrame frame,
        CancellationToken cancellationToken = default,
        ProtocolWriteOptions? options = null)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var header = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize);
        try
        {
            options ??= ProtocolWriteOptions.V2NoCompression;
            var effectiveVersion = options.Version;
            if (!ProtocolConstants.IsSupported(effectiveVersion))
            {
                throw new InvalidOperationException($"Unsupported write protocol version: {effectiveVersion}.");
            }

            EncodedPayload encodedPayload;
            if (ProtocolConstants.SupportsCompression(effectiveVersion))
            {
                encodedPayload = EncodePayload(
                    frame.Payload,
                    options.EnableCompression,
                    options.CompressionAlgorithm,
                    options.CompressionMinBytes,
                    options.CompressionMinSavingsBytes);
            }
            else
            {
                encodedPayload = EncodedPayload.FromSource(frame.Payload);
            }

            try
            {
                if (effectiveVersion == ProtocolConstants.LegacyVersion)
                {
                    var headerSpan = header.AsSpan(0, ProtocolConstants.HeaderSizeV1);
                    headerSpan[0] = ProtocolConstants.LegacyVersion;
                    headerSpan[1] = (byte)frame.Type;
                    BinaryPrimitives.WriteUInt32BigEndian(headerSpan.Slice(2, 4), frame.SessionId);
                    BinaryPrimitives.WriteUInt64BigEndian(headerSpan.Slice(6, 8), frame.Sequence);
                    BinaryPrimitives.WriteUInt32BigEndian(headerSpan.Slice(14, 4), (uint)encodedPayload.Length);
                    await stream.WriteAsync(header.AsMemory(0, ProtocolConstants.HeaderSizeV1), cancellationToken);
                }
                else
                {
                    var headerSpan = header.AsSpan(0, ProtocolConstants.HeaderSizeV2);
                    headerSpan[0] = effectiveVersion;
                    headerSpan[1] = (byte)frame.Type;
                    headerSpan[2] = encodedPayload.Flags;
                    BinaryPrimitives.WriteUInt32BigEndian(headerSpan.Slice(3, 4), frame.SessionId);
                    BinaryPrimitives.WriteUInt64BigEndian(headerSpan.Slice(7, 8), frame.Sequence);
                    BinaryPrimitives.WriteUInt32BigEndian(headerSpan.Slice(15, 4), (uint)encodedPayload.Length);
                    await stream.WriteAsync(header.AsMemory(0, ProtocolConstants.HeaderSizeV2), cancellationToken);
                }

                if (encodedPayload.Length > 0)
                {
                    await stream.WriteAsync(encodedPayload.Payload, cancellationToken);
                }
            }
            finally
            {
                encodedPayload.Dispose();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    public static async ValueTask<ProtocolFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var result = await ReadDetailedAsync(stream, cancellationToken);
        return result?.Frame;
    }

    public static async ValueTask<ProtocolReadResult?> ReadDetailedAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var header = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize);
        try
        {
            var versionRead = await ReadExactlyOrToEndAsync(stream, header, 1, cancellationToken);
            if (versionRead == 0)
            {
                return null;
            }

            var version = header[0];
            int expectedHeaderLength;
            if (version == ProtocolConstants.LegacyVersion)
            {
                expectedHeaderLength = ProtocolConstants.HeaderSizeV1;
            }
            else if (ProtocolConstants.UsesExtendedHeader(version))
            {
                expectedHeaderLength = ProtocolConstants.HeaderSizeV2;
            }
            else
            {
                throw new InvalidDataException($"Unsupported frame version: {version}.");
            }

            var headerRead = await ReadExactlyOrToEndAsync(
                stream,
                header,
                offset: 1,
                length: expectedHeaderLength - 1,
                cancellationToken);
            if (headerRead != expectedHeaderLength - 1)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading frame header.");
            }

            var headerSpan = header.AsSpan(0, expectedHeaderLength);
            var frameType = (FrameType)headerSpan[1];
            if (!Enum.IsDefined(frameType))
            {
                throw new InvalidDataException($"Unsupported frame type: {(byte)frameType}.");
            }

            byte flags;
            uint sessionId;
            ulong sequence;
            uint payloadLength;
            if (version == ProtocolConstants.LegacyVersion)
            {
                flags = ProtocolFlags.None;
                sessionId = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(2, 4));
                sequence = BinaryPrimitives.ReadUInt64BigEndian(headerSpan.Slice(6, 8));
                payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(14, 4));
            }
            else
            {
                flags = headerSpan[2];
                sessionId = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(3, 4));
                sequence = BinaryPrimitives.ReadUInt64BigEndian(headerSpan.Slice(7, 8));
                payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(15, 4));
            }

            if (payloadLength > ProtocolConstants.MaxPayloadLength)
            {
                throw new InvalidDataException($"Frame payload too large: {payloadLength}.");
            }

            byte[] payload;
            if (payloadLength == 0)
            {
                payload = Array.Empty<byte>();
            }
            else if ((flags & ProtocolFlags.PayloadCompressed) != 0)
            {
                var compressed = ArrayPool<byte>.Shared.Rent((int)payloadLength);
                try
                {
                    await ReadExactlyAsync(stream, compressed, (int)payloadLength, cancellationToken);
                    payload = DecompressPayload(compressed, (int)payloadLength, flags);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                }
            }
            else
            {
                payload = new byte[payloadLength];
                await ReadExactlyAsync(stream, payload, cancellationToken);
            }

            return new ProtocolReadResult(new ProtocolFrame(frameType, sessionId, sequence, payload), version, flags);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    internal static async ValueTask<ProtocolReadLease?> ReadDetailedLeasedAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var header = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HeaderSize);
        try
        {
            var versionRead = await ReadExactlyOrToEndAsync(stream, header, 1, cancellationToken);
            if (versionRead == 0)
            {
                return null;
            }

            var version = header[0];
            int expectedHeaderLength;
            if (version == ProtocolConstants.LegacyVersion)
            {
                expectedHeaderLength = ProtocolConstants.HeaderSizeV1;
            }
            else if (ProtocolConstants.UsesExtendedHeader(version))
            {
                expectedHeaderLength = ProtocolConstants.HeaderSizeV2;
            }
            else
            {
                throw new InvalidDataException($"Unsupported frame version: {version}.");
            }

            var headerRead = await ReadExactlyOrToEndAsync(
                stream,
                header,
                offset: 1,
                length: expectedHeaderLength - 1,
                cancellationToken);
            if (headerRead != expectedHeaderLength - 1)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading frame header.");
            }

            var headerSpan = header.AsSpan(0, expectedHeaderLength);
            var frameType = (FrameType)headerSpan[1];
            if (!Enum.IsDefined(frameType))
            {
                throw new InvalidDataException($"Unsupported frame type: {(byte)frameType}.");
            }

            byte flags;
            uint sessionId;
            ulong sequence;
            uint payloadLength;
            if (version == ProtocolConstants.LegacyVersion)
            {
                flags = ProtocolFlags.None;
                sessionId = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(2, 4));
                sequence = BinaryPrimitives.ReadUInt64BigEndian(headerSpan.Slice(6, 8));
                payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(14, 4));
            }
            else
            {
                flags = headerSpan[2];
                sessionId = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(3, 4));
                sequence = BinaryPrimitives.ReadUInt64BigEndian(headerSpan.Slice(7, 8));
                payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(15, 4));
            }

            if (payloadLength > ProtocolConstants.MaxPayloadLength)
            {
                throw new InvalidDataException($"Frame payload too large: {payloadLength}.");
            }

            byte[]? pooledPayload = null;
            ReadOnlyMemory<byte> payload;
            if (payloadLength == 0)
            {
                payload = ReadOnlyMemory<byte>.Empty;
            }
            else if ((flags & ProtocolFlags.PayloadCompressed) != 0)
            {
                var compressed = ArrayPool<byte>.Shared.Rent((int)payloadLength);
                try
                {
                    await ReadExactlyAsync(stream, compressed, (int)payloadLength, cancellationToken);
                    payload = DecompressPayload(compressed, (int)payloadLength, flags);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                }
            }
            else
            {
                pooledPayload = ArrayPool<byte>.Shared.Rent((int)payloadLength);
                try
                {
                    await ReadExactlyAsync(stream, pooledPayload, (int)payloadLength, cancellationToken);
                    payload = pooledPayload.AsMemory(0, (int)payloadLength);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(pooledPayload);
                    throw;
                }
            }

            return new ProtocolReadLease(
                new ProtocolFrame(frameType, sessionId, sequence, payload),
                version,
                flags,
                pooledPayload);
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
        return await ReadExactlyOrToEndAsync(stream, buffer, 0, length, cancellationToken);
    }

    private static async ValueTask<int> ReadExactlyOrToEndAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int length,
        CancellationToken cancellationToken)
    {
        var readOffset = offset;
        var readBytes = 0;
        while (readBytes < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(readOffset, length - readBytes), cancellationToken);
            if (read == 0)
            {
                return readBytes;
            }

            readOffset += read;
            readBytes += read;
        }

        return readBytes;
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        await ReadExactlyAsync(stream, buffer, buffer.Length, cancellationToken);
    }

    private static async ValueTask ReadExactlyAsync(
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
                throw new EndOfStreamException("Unexpected end of stream while reading frame payload.");
            }

            offset += read;
        }
    }

    private static EncodedPayload EncodePayload(
        ReadOnlyMemory<byte> sourcePayload,
        bool enableCompression,
        PayloadCompressionAlgorithm compressionAlgorithm,
        int minBytes,
        int minSavingsBytes)
    {
        var flags = ProtocolFlags.None;
        if (!enableCompression)
        {
            return EncodedPayload.FromSource(sourcePayload);
        }

        flags |= ProtocolFlags.CompressionEnabled;
        flags |= PayloadCompressionCodecFactory.ToFlags(compressionAlgorithm);
        if (sourcePayload.Length < minBytes)
        {
            return EncodedPayload.FromSource(sourcePayload, flags);
        }

        var rented = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaxPayloadLength);
        var codec = PayloadCompressionCodecFactory.Resolve(compressionAlgorithm);
        if (!codec.TryCompress(sourcePayload.Span, rented, out var bytesWritten))
        {
            ArrayPool<byte>.Shared.Return(rented);
            return EncodedPayload.FromSource(sourcePayload, flags);
        }

        if (bytesWritten + minSavingsBytes >= sourcePayload.Length)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return EncodedPayload.FromSource(sourcePayload, flags);
        }

        flags |= ProtocolFlags.PayloadCompressed;
        return EncodedPayload.FromPooled(rented, bytesWritten, flags);
    }

    private static byte[] DecompressPayload(byte[] compressed, int compressedLength, byte flags)
    {
        var algorithm = PayloadCompressionCodecFactory.FromFlags(flags);
        var codec = PayloadCompressionCodecFactory.Resolve(algorithm);
        return codec.Decompress(compressed, compressedLength, ProtocolConstants.MaxPayloadLength);
    }

    private readonly struct EncodedPayload : IDisposable
    {
        private readonly byte[]? _pooled;
        public ReadOnlyMemory<byte> Payload { get; }
        public int Length { get; }
        public byte Flags { get; }

        private EncodedPayload(ReadOnlyMemory<byte> payload, int length, byte flags, byte[]? pooled)
        {
            Payload = payload;
            Length = length;
            Flags = flags;
            _pooled = pooled;
        }

        public static EncodedPayload FromSource(ReadOnlyMemory<byte> payload, byte flags = ProtocolFlags.None)
            => new(payload, payload.Length, flags, null);

        public static EncodedPayload FromPooled(byte[] pooled, int length, byte flags)
            => new(pooled.AsMemory(0, length), length, flags, pooled);

        public void Dispose()
        {
            if (_pooled is not null)
            {
                ArrayPool<byte>.Shared.Return(_pooled);
            }
        }
    }
}
