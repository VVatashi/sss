using System.Buffers.Binary;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

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
    private static int _systemChaCha20Poly1305Available = ProbeSystemChaCha20Poly1305() ? 1 : 0;

    private ulong _writeCounter;
    private ulong _readCounter;

    private byte[] _plainReadBuffer = Array.Empty<byte>();
    private int _plainReadOffset;
    private int _plainReadLength;
    private bool _plainReadBufferFromPool;

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

        var nonce = BuildNonce(_writeBaseNonce, _writeCounter++);
        await EncryptAndWriteAsync(buffer, nonce, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        ReturnPlainReadBuffer();

        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        ReturnPlainReadBuffer();

        if (!_leaveOpen)
        {
            await _inner.DisposeAsync();
        }

        await base.DisposeAsync();
    }

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
            if (cipherLength <= TagLength || cipherLength > MaxCipherRecordLength)
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

                var nonce = BuildNonce(_readBaseNonce, _readCounter++);
                ReturnPlainReadBuffer();
                (_plainReadBuffer, _plainReadLength) = DecryptPooled(cipherText.AsSpan(0, cipherLength), nonce, _key);
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

    private static byte[] BuildNonce(byte[] baseNonce, ulong counter)
    {
        var nonce = new byte[NonceLength];
        baseNonce.AsSpan().CopyTo(nonce);
        nonce[NonceLength - 8] ^= (byte)(counter >> 56);
        nonce[NonceLength - 7] ^= (byte)(counter >> 48);
        nonce[NonceLength - 6] ^= (byte)(counter >> 40);
        nonce[NonceLength - 5] ^= (byte)(counter >> 32);
        nonce[NonceLength - 4] ^= (byte)(counter >> 24);
        nonce[NonceLength - 3] ^= (byte)(counter >> 16);
        nonce[NonceLength - 2] ^= (byte)(counter >> 8);
        nonce[NonceLength - 1] ^= (byte)counter;

        return nonce;
    }

    private async ValueTask EncryptAndWriteAsync(ReadOnlyMemory<byte> plain, byte[] nonce, CancellationToken cancellationToken)
    {
        var cipherBuffer = ArrayPool<byte>.Shared.Rent(plain.Length + TagLength);
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

            var len = EncryptCombined(plainSpan, nonce, _key, cipherBuffer);

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

    private static (byte[] Buffer, int Length) DecryptPooled(ReadOnlySpan<byte> cipherText, byte[] nonce, byte[] key)
    {
        var output = ArrayPool<byte>.Shared.Rent(cipherText.Length - TagLength);
        try
        {
            var len = DecryptCombined(cipherText, nonce, key, output);
            return (output, len);
        }
        catch (CryptographicException ex)
        {
            ArrayPool<byte>.Shared.Return(output);
            throw new InvalidDataException("Encrypted record authentication failed.", ex);
        }
        catch (InvalidCipherTextException ex)
        {
            ArrayPool<byte>.Shared.Return(output);
            throw new InvalidDataException("Encrypted record authentication failed.", ex);
        }
    }

    private static async Task<int> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        return await ReadExactlyOrEofAsync(stream, buffer, buffer.Length, cancellationToken);
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

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        await ReadExactlyAsync(stream, buffer, buffer.Length, cancellationToken);
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

    private static int EncryptCombined(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> nonce, byte[] key, byte[] destination)
    {
        if (Volatile.Read(ref _systemChaCha20Poly1305Available) == 1)
        {
            try
            {
                using var cipher = new System.Security.Cryptography.ChaCha20Poly1305(key);
                var cipherSpan = destination.AsSpan(0, plain.Length);
                var tagSpan = destination.AsSpan(plain.Length, TagLength);
                cipher.Encrypt(nonce, plain, cipherSpan, tagSpan);
                return plain.Length + TagLength;
            }
            catch (PlatformNotSupportedException)
            {
                Interlocked.Exchange(ref _systemChaCha20Poly1305Available, 0);
            }
        }

        var bcCipher = new BcChaCha20Poly1305();
        var nonceArray = new byte[NonceLength];
        byte[]? rentedPlain = null;
        try
        {
            nonce.CopyTo(nonceArray);
            bcCipher.Init(true, new AeadParameters(new KeyParameter(key), TagBits, nonceArray));

            var plainArray = ArrayPool<byte>.Shared.Rent(plain.Length);
            rentedPlain = plainArray;
            plain.CopyTo(plainArray);

            var len = bcCipher.ProcessBytes(plainArray, 0, plain.Length, destination, 0);
            len += bcCipher.DoFinal(destination, len);
            return len;
        }
        finally
        {
            if (rentedPlain is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedPlain);
            }
        }
    }

    private static int DecryptCombined(ReadOnlySpan<byte> cipherTextAndTag, ReadOnlySpan<byte> nonce, byte[] key, byte[] destination)
    {
        if (Volatile.Read(ref _systemChaCha20Poly1305Available) == 1)
        {
            try
            {
                using var cipher = new System.Security.Cryptography.ChaCha20Poly1305(key);
                var cipherLen = cipherTextAndTag.Length - TagLength;
                var cipherSpan = cipherTextAndTag.Slice(0, cipherLen);
                var tagSpan = cipherTextAndTag.Slice(cipherLen, TagLength);
                cipher.Decrypt(nonce, cipherSpan, tagSpan, destination.AsSpan(0, cipherLen));
                return cipherLen;
            }
            catch (PlatformNotSupportedException)
            {
                Interlocked.Exchange(ref _systemChaCha20Poly1305Available, 0);
            }
        }

        var bcCipher = new BcChaCha20Poly1305();
        var nonceArray = new byte[NonceLength];
        nonce.CopyTo(nonceArray);
        bcCipher.Init(false, new AeadParameters(new KeyParameter(key), TagBits, nonceArray));

        var rentedInput = ArrayPool<byte>.Shared.Rent(cipherTextAndTag.Length);
        try
        {
            cipherTextAndTag.CopyTo(rentedInput);
            var len = bcCipher.ProcessBytes(rentedInput, 0, cipherTextAndTag.Length, destination, 0);
            len += bcCipher.DoFinal(destination, len);
            return len;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedInput);
        }
    }

    private static bool ProbeSystemChaCha20Poly1305()
    {
        try
        {
            var key = new byte[32];
            var nonce = new byte[NonceLength];
            var plain = new byte[1];
            var cipher = new byte[1];
            var tag = new byte[TagLength];

            using var aead = new System.Security.Cryptography.ChaCha20Poly1305(key);
            aead.Encrypt(nonce, plain, cipher, tag);
            aead.Decrypt(nonce, cipher, tag, plain);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
