namespace SimpleShadowsocks.Protocol.Compression;

internal sealed class PooledBufferWriteStream : Stream
{
    private readonly byte[] _buffer;
    private readonly int _maxLength;
    private int _position;

    public PooledBufferWriteStream(byte[] buffer, int maxLength)
    {
        _buffer = buffer;
        _maxLength = maxLength;
    }

    public int WrittenCount => _position;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _position;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_position + buffer.Length > _maxLength)
        {
            throw new InvalidDataException("Compressed payload exceeds allowed maximum length.");
        }

        buffer.CopyTo(_buffer.AsSpan(_position));
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
}
