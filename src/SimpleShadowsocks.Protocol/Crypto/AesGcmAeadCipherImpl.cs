using System.Buffers;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace SimpleShadowsocks.Protocol.Crypto;

internal sealed class AesGcmAeadCipherImpl : IAeadCipherImpl
{
    public const int NonceSizeConst = 12;
    public const int TagSizeConst = 16;
    private const int TagBits = 128;

    private readonly byte[] _key;
    private static int _systemSupported = ProbeSystem() ? 1 : 0;

    public AesGcmAeadCipherImpl(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256-GCM key must be 32 bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    public int NonceSize => NonceSizeConst;
    public int TagSize => TagSizeConst;

    public static bool IsSupported() => true;

    public int Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce, Span<byte> destination)
    {
        if (Volatile.Read(ref _systemSupported) == 1)
        {
            try
            {
                using var cipher = new AesGcm(_key, TagSizeConst);
                cipher.Encrypt(
                    nonce,
                    plaintext,
                    destination.Slice(0, plaintext.Length),
                    destination.Slice(plaintext.Length, TagSizeConst));
                return plaintext.Length + TagSizeConst;
            }
            catch (PlatformNotSupportedException)
            {
                Interlocked.Exchange(ref _systemSupported, 0);
            }
        }

        var bcCipher = new GcmBlockCipher(new AesEngine());
        var nonceArray = new byte[NonceSizeConst];
        nonce.CopyTo(nonceArray);
        bcCipher.Init(true, new AeadParameters(new KeyParameter(_key), TagBits, nonceArray));

        byte[]? rentedPlain = null;
        byte[]? rentedOutput = null;
        try
        {
            rentedPlain = ArrayPool<byte>.Shared.Rent(plaintext.Length);
            plaintext.CopyTo(rentedPlain);
            rentedOutput = ArrayPool<byte>.Shared.Rent(plaintext.Length + TagSizeConst);
            var len = bcCipher.ProcessBytes(rentedPlain, 0, plaintext.Length, rentedOutput, 0);
            len += bcCipher.DoFinal(rentedOutput, len);
            rentedOutput.AsSpan(0, len).CopyTo(destination);
            return len;
        }
        finally
        {
            if (rentedPlain is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedPlain);
            }

            if (rentedOutput is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedOutput);
            }
        }
    }

    public int Decrypt(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> nonce, Span<byte> destination)
    {
        if (Volatile.Read(ref _systemSupported) == 1)
        {
            try
            {
                using var cipher = new AesGcm(_key, TagSizeConst);
                var cipherLen = ciphertextAndTag.Length - TagSizeConst;
                cipher.Decrypt(
                    nonce,
                    ciphertextAndTag.Slice(0, cipherLen),
                    ciphertextAndTag.Slice(cipherLen, TagSizeConst),
                    destination.Slice(0, cipherLen));
                return cipherLen;
            }
            catch (PlatformNotSupportedException)
            {
                Interlocked.Exchange(ref _systemSupported, 0);
            }
        }

        var bcCipher = new GcmBlockCipher(new AesEngine());
        var nonceArray = new byte[NonceSizeConst];
        nonce.CopyTo(nonceArray);
        bcCipher.Init(false, new AeadParameters(new KeyParameter(_key), TagBits, nonceArray));

        byte[]? rentedInput = null;
        byte[]? rentedOutput = null;
        try
        {
            rentedInput = ArrayPool<byte>.Shared.Rent(ciphertextAndTag.Length);
            ciphertextAndTag.CopyTo(rentedInput);
            rentedOutput = ArrayPool<byte>.Shared.Rent(destination.Length);
            var len = bcCipher.ProcessBytes(rentedInput, 0, ciphertextAndTag.Length, rentedOutput, 0);
            len += bcCipher.DoFinal(rentedOutput, len);
            rentedOutput.AsSpan(0, len).CopyTo(destination);
            return len;
        }
        finally
        {
            if (rentedInput is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedInput);
            }

            if (rentedOutput is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedOutput);
            }
        }
    }

    public void Dispose()
    {
    }

    private static bool ProbeSystem()
    {
        try
        {
            var key = new byte[32];
            var nonce = new byte[NonceSizeConst];
            var plain = new byte[1];
            var cipher = new byte[1];
            var tag = new byte[TagSizeConst];
            using var aead = new AesGcm(key, TagSizeConst);
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

