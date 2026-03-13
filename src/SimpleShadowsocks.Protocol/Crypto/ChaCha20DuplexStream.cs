using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace SimpleShadowsocks.Protocol.Crypto;

public sealed class ChaCha20DuplexStream : Stream
{
    private readonly Stream _inner;
    private readonly ChaCha7539Engine _encryptor;
    private readonly ChaCha7539Engine _decryptor;
    private readonly bool _leaveOpen;

    public ChaCha20DuplexStream(
        Stream inner,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> writeNonce,
        ReadOnlySpan<byte> readNonce,
        bool leaveOpen = false)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("ChaCha20 key must be 32 bytes.", nameof(key));
        }

        if (writeNonce.Length != 12 || readNonce.Length != 12)
        {
            throw new ArgumentException("ChaCha20 nonce must be 12 bytes.");
        }

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leaveOpen = leaveOpen;

        _encryptor = new ChaCha7539Engine();
        _decryptor = new ChaCha7539Engine();

        _encryptor.Init(true, new ParametersWithIV(new KeyParameter(key.ToArray()), writeNonce.ToArray()));
        _decryptor.Init(false, new ParametersWithIV(new KeyParameter(key.ToArray()), readNonce.ToArray()));
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
        {
            TransformInPlace(_decryptor, buffer.Span[..read]);
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            TransformInPlace(_decryptor, buffer.AsSpan(offset, read));
        }

        return read;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return;
        }

        var encrypted = new byte[count];
        buffer.AsSpan(offset, count).CopyTo(encrypted);
        TransformInPlace(_encryptor, encrypted);
        await _inner.WriteAsync(encrypted, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var encrypted = buffer.ToArray();
        TransformInPlace(_encryptor, encrypted);
        await _inner.WriteAsync(encrypted, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return;
        }

        var encrypted = new byte[count];
        buffer.AsSpan(offset, count).CopyTo(encrypted);
        TransformInPlace(_encryptor, encrypted);
        _inner.Write(encrypted, 0, encrypted.Length);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static void TransformInPlace(ChaCha7539Engine engine, Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = engine.ReturnByte(data[i]);
        }
    }
}
