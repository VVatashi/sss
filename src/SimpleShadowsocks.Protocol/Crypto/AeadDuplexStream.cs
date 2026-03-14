using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SimpleShadowsocks.Protocol.Crypto;

public sealed class AeadDuplexStream : Stream
{
    private const int LengthPrefixSize = 4;
    private const int MaxCipherRecordLength = 2 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly IAeadCipherImpl _writeCipher;
    private readonly IAeadCipherImpl _readCipher;
    private readonly int _nonceLength;
    private readonly int _tagLength;
    private readonly byte[] _writeBaseNonce;
    private readonly byte[] _readBaseNonce;
    private readonly bool _leaveOpen;

    private ulong _writeCounter;
    private ulong _readCounter;

    private byte[] _plainReadBuffer = Array.Empty<byte>();
    private int _plainReadOffset;
    private int _plainReadLength;
    private bool _plainReadBufferFromPool;

    public AeadDuplexStream(
        Stream inner,
        TunnelCipherAlgorithm algorithm,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> writeBaseNonce,
        ReadOnlySpan<byte> readBaseNonce,
        bool leaveOpen = false)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AEAD key must be 32 bytes.", nameof(key));
        }

        _writeCipher = AeadCipherFactory.Create(algorithm, key);
        _readCipher = AeadCipherFactory.Create(algorithm, key);
        _nonceLength = _writeCipher.NonceSize;
        _tagLength = _writeCipher.TagSize;

        if (writeBaseNonce.Length != _nonceLength || readBaseNonce.Length != _nonceLength)
        {
            throw new ArgumentException($"AEAD nonce must be {_nonceLength} bytes.");
        }

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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

        var available = _plainReadLength - _plainReadOffset;
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

        var nonce = BuildNonce(_writeBaseNonce, _writeCounter++, _nonceLength);
        await EncryptAndWriteAsync(buffer, nonce, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        ReturnPlainReadBuffer();
        _writeCipher.Dispose();
        _readCipher.Dispose();

        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        ReturnPlainReadBuffer();
        _writeCipher.Dispose();
        _readCipher.Dispose();

        if (!_leaveOpen)
        {
            await _inner.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    public static bool IsSupported(TunnelCipherAlgorithm algorithm) => AeadCipherFactory.IsSupported(algorithm);
    public static int GetNonceSize(TunnelCipherAlgorithm algorithm) => AeadCipherFactory.GetNonceSize(algorithm);

    private bool EnsurePlainReadData()
    {
        return _plainReadOffset < _plainReadLength;
    }

    private async Task<bool> LoadNextPlainRecordAsync(CancellationToken cancellationToken)
    {
        var lengthBuffer = ArrayPool<byte>.Shared.Rent(LengthPrefixSize);
        try
        {
            var lengthBytesRead = await ReadExactlyOrEofAsync(_inner, lengthBuffer, LengthPrefixSize, cancellationToken);
            if (lengthBytesRead == 0)
            {
                return false;
            }

            if (lengthBytesRead != LengthPrefixSize)
            {
                throw new EndOfStreamException("Unexpected EOF while reading encrypted record length.");
            }

            var cipherLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer.AsSpan(0, LengthPrefixSize));
            if (cipherLength <= _tagLength || cipherLength > MaxCipherRecordLength)
            {
                throw new InvalidDataException($"Invalid encrypted record length: {cipherLength}.");
            }

            var cipherText = ArrayPool<byte>.Shared.Rent(cipherLength);
            try
            {
                await ReadExactlyAsync(_inner, cipherText, cipherLength, cancellationToken);

                if (_readCounter == ulong.MaxValue)
                {
                    throw new InvalidDataException("AEAD read counter exhausted.");
                }

                var nonce = BuildNonce(_readBaseNonce, _readCounter++, _nonceLength);
                ReturnPlainReadBuffer();
                (_plainReadBuffer, _plainReadLength) = DecryptPooled(cipherText.AsSpan(0, cipherLength), nonce);
                _plainReadBufferFromPool = true;
                _plainReadOffset = 0;
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(cipherText);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthBuffer);
        }
    }

    private static byte[] BuildNonce(byte[] baseNonce, ulong counter, int nonceLength)
    {
        var nonce = new byte[nonceLength];
        baseNonce.AsSpan().CopyTo(nonce);
        nonce[nonceLength - 8] ^= (byte)(counter >> 56);
        nonce[nonceLength - 7] ^= (byte)(counter >> 48);
        nonce[nonceLength - 6] ^= (byte)(counter >> 40);
        nonce[nonceLength - 5] ^= (byte)(counter >> 32);
        nonce[nonceLength - 4] ^= (byte)(counter >> 24);
        nonce[nonceLength - 3] ^= (byte)(counter >> 16);
        nonce[nonceLength - 2] ^= (byte)(counter >> 8);
        nonce[nonceLength - 1] ^= (byte)counter;
        return nonce;
    }

    private async ValueTask EncryptAndWriteAsync(ReadOnlyMemory<byte> plain, byte[] nonce, CancellationToken cancellationToken)
    {
        var cipherBuffer = ArrayPool<byte>.Shared.Rent(plain.Length + _tagLength);
        byte[]? rentedInput = null;
        try
        {
            ReadOnlySpan<byte> plainSpan;
            if (MemoryMarshal.TryGetArray(plain, out var segment) && segment.Array is not null)
            {
                plainSpan = segment.Array.AsSpan(segment.Offset, segment.Count);
            }
            else
            {
                rentedInput = ArrayPool<byte>.Shared.Rent(plain.Length);
                plain.Span.CopyTo(rentedInput.AsSpan(0, plain.Length));
                plainSpan = rentedInput.AsSpan(0, plain.Length);
            }

            var len = _writeCipher.Encrypt(plainSpan, nonce, cipherBuffer);

            var prefix = ArrayPool<byte>.Shared.Rent(LengthPrefixSize);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(0, LengthPrefixSize), len);
                await _inner.WriteAsync(prefix.AsMemory(0, LengthPrefixSize), cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(prefix);
            }

            await _inner.WriteAsync(cipherBuffer.AsMemory(0, len), cancellationToken);
        }
        finally
        {
            if (rentedInput is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedInput);
            }

            ArrayPool<byte>.Shared.Return(cipherBuffer);
        }
    }

    private (byte[] Buffer, int Length) DecryptPooled(ReadOnlySpan<byte> cipherText, ReadOnlySpan<byte> nonce)
    {
        var output = ArrayPool<byte>.Shared.Rent(cipherText.Length - _tagLength);
        try
        {
            var len = _readCipher.Decrypt(cipherText, nonce, output);
            return (output, len);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
    }

    private static async Task<int> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, int length, CancellationToken cancellationToken)
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

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading encrypted record.");
            }

            offset += read;
        }
    }

    private void ReturnPlainReadBuffer()
    {
        if (_plainReadBufferFromPool && _plainReadBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_plainReadBuffer);
        }

        _plainReadBuffer = Array.Empty<byte>();
        _plainReadOffset = 0;
        _plainReadLength = 0;
        _plainReadBufferFromPool = false;
    }
}
