using System.Buffers.Binary;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace SimpleShadowsocks.Protocol.Crypto;

public sealed class ChaCha20Poly1305DuplexStream : Stream
{
    private const int NonceLength = 12;
    private const int TagBits = 128;
    private const int TagLength = TagBits / 8;
    private const int LengthPrefixSize = 4;
    private const int MaxCipherRecordLength = 2 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly byte[] _key;
    private readonly byte[] _writeBaseNonce;
    private readonly byte[] _readBaseNonce;
    private readonly bool _leaveOpen;

    private ulong _writeCounter;
    private ulong _readCounter;

    private byte[] _plainReadBuffer = Array.Empty<byte>();
    private int _plainReadOffset;

    public ChaCha20Poly1305DuplexStream(
        Stream inner,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> writeBaseNonce,
        ReadOnlySpan<byte> readBaseNonce,
        bool leaveOpen = false)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("ChaCha20-Poly1305 key must be 32 bytes.", nameof(key));
        }

        if (writeBaseNonce.Length != NonceLength || readBaseNonce.Length != NonceLength)
        {
            throw new ArgumentException("ChaCha20-Poly1305 nonce must be 12 bytes.");
        }

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _key = key.ToArray();
        _writeBaseNonce = writeBaseNonce.ToArray();
        _readBaseNonce = readBaseNonce.ToArray();
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        if (!EnsurePlainReadData() && !await LoadNextPlainRecordAsync(cancellationToken))
        {
            return 0;
        }

        var available = _plainReadBuffer.Length - _plainReadOffset;
        var toCopy = Math.Min(buffer.Length, available);
        _plainReadBuffer.AsSpan(_plainReadOffset, toCopy).CopyTo(buffer.Span);
        _plainReadOffset += toCopy;
        return toCopy;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        if (_writeCounter == ulong.MaxValue)
        {
            throw new InvalidOperationException("AEAD write counter exhausted; re-key is required.");
        }

        var nonce = BuildNonce(_writeBaseNonce, _writeCounter++);
        var cipherText = Encrypt(buffer.Span, nonce, _key);

        var prefix = new byte[LengthPrefixSize];
        BinaryPrimitives.WriteInt32BigEndian(prefix, cipherText.Length);

        await _inner.WriteAsync(prefix, cancellationToken);
        await _inner.WriteAsync(cipherText, cancellationToken);
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

    private bool EnsurePlainReadData()
    {
        return _plainReadOffset < _plainReadBuffer.Length;
    }

    private async Task<bool> LoadNextPlainRecordAsync(CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[LengthPrefixSize];
        var lengthBytesRead = await ReadExactlyOrEofAsync(_inner, lengthBuffer, cancellationToken);
        if (lengthBytesRead == 0)
        {
            return false;
        }

        if (lengthBytesRead != LengthPrefixSize)
        {
            throw new EndOfStreamException("Unexpected EOF while reading encrypted record length.");
        }

        var cipherLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (cipherLength <= TagLength || cipherLength > MaxCipherRecordLength)
        {
            throw new InvalidDataException($"Invalid encrypted record length: {cipherLength}.");
        }

        var cipherText = new byte[cipherLength];
        await ReadExactlyAsync(_inner, cipherText, cancellationToken);

        if (_readCounter == ulong.MaxValue)
        {
            throw new InvalidDataException("AEAD read counter exhausted.");
        }

        var nonce = BuildNonce(_readBaseNonce, _readCounter++);
        _plainReadBuffer = Decrypt(cipherText, nonce, _key);
        _plainReadOffset = 0;
        return true;
    }

    private static byte[] BuildNonce(byte[] baseNonce, ulong counter)
    {
        var nonce = baseNonce.ToArray();
        var counterBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);

        for (var i = 0; i < 8; i++)
        {
            nonce[NonceLength - 8 + i] ^= counterBytes[i];
        }

        return nonce;
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> plain, byte[] nonce, byte[] key)
    {
        var cipher = new ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), TagBits, nonce));

        var input = plain.ToArray();
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len += cipher.DoFinal(output, len);

        if (len == output.Length)
        {
            return output;
        }

        var resized = new byte[len];
        Buffer.BlockCopy(output, 0, resized, 0, len);
        return resized;
    }

    private static byte[] Decrypt(byte[] cipherText, byte[] nonce, byte[] key)
    {
        try
        {
            var cipher = new ChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagBits, nonce));

            var output = new byte[cipher.GetOutputSize(cipherText.Length)];
            var len = cipher.ProcessBytes(cipherText, 0, cipherText.Length, output, 0);
            len += cipher.DoFinal(output, len);

            if (len == output.Length)
            {
                return output;
            }

            var resized = new byte[len];
            Buffer.BlockCopy(output, 0, resized, 0, len);
            return resized;
        }
        catch (InvalidCipherTextException ex)
        {
            throw new InvalidDataException("Encrypted record authentication failed.", ex);
        }
    }

    private static async Task<int> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
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

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading encrypted record.");
            }

            offset += read;
        }
    }
}
