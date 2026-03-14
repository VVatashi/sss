using NSec.Cryptography;

namespace SimpleShadowsocks.Protocol.Crypto;

internal sealed class NsecAeadCipherImpl : IAeadCipherImpl
{
    private readonly AeadAlgorithm _algorithm;
    private readonly Key _key;

    public NsecAeadCipherImpl(TunnelCipherAlgorithm algorithm, ReadOnlySpan<byte> key)
    {
        _algorithm = ResolveAlgorithm(algorithm)
            ?? throw new InvalidDataException($"Unsupported NSec AEAD algorithm: {algorithm}.");

        var keyBytes = new byte[_algorithm.KeySize];
        key.Slice(0, keyBytes.Length).CopyTo(keyBytes);
        var parameters = default(KeyCreationParameters);
        _key = Key.Import(_algorithm, keyBytes, KeyBlobFormat.RawSymmetricKey, in parameters);
    }

    public int NonceSize => _algorithm.NonceSize;
    public int TagSize => _algorithm.TagSize;

    public static bool IsSupported(TunnelCipherAlgorithm algorithm)
    {
        var aead = ResolveAlgorithm(algorithm);
        if (aead is null)
        {
            return false;
        }

        try
        {
            var keyBytes = new byte[aead.KeySize];
            var nonce = new byte[aead.NonceSize];
            var plain = new byte[1];
            var cipherAndTag = new byte[plain.Length + aead.TagSize];
            var decoded = new byte[plain.Length];

            var parameters = default(KeyCreationParameters);
            using var key = Key.Import(aead, keyBytes, KeyBlobFormat.RawSymmetricKey, in parameters);
            aead.Encrypt(key, nonce, ReadOnlySpan<byte>.Empty, plain, cipherAndTag);
            return aead.Decrypt(key, nonce, ReadOnlySpan<byte>.Empty, cipherAndTag, decoded);
        }
        catch
        {
            return false;
        }
    }

    public static int GetNonceSize(TunnelCipherAlgorithm algorithm)
    {
        return ResolveAlgorithm(algorithm)?.NonceSize
               ?? throw new InvalidDataException($"Unsupported NSec AEAD algorithm: {algorithm}.");
    }

    public static int GetTagSize(TunnelCipherAlgorithm algorithm)
    {
        return ResolveAlgorithm(algorithm)?.TagSize
               ?? throw new InvalidDataException($"Unsupported NSec AEAD algorithm: {algorithm}.");
    }

    public int Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce, Span<byte> destination)
    {
        var cipherLength = plaintext.Length + _algorithm.TagSize;
        _algorithm.Encrypt(_key, nonce, ReadOnlySpan<byte>.Empty, plaintext, destination.Slice(0, cipherLength));
        return cipherLength;
    }

    public int Decrypt(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> nonce, Span<byte> destination)
    {
        var plainLength = ciphertextAndTag.Length - _algorithm.TagSize;
        if (plainLength < 0)
        {
            throw new InvalidDataException("Invalid encrypted record length.");
        }

        var ok = _algorithm.Decrypt(_key, nonce, ReadOnlySpan<byte>.Empty, ciphertextAndTag, destination.Slice(0, plainLength));
        if (!ok)
        {
            throw new InvalidDataException("Encrypted record authentication failed.");
        }

        return plainLength;
    }

    public void Dispose()
    {
        _key.Dispose();
    }

    private static AeadAlgorithm? ResolveAlgorithm(TunnelCipherAlgorithm algorithm)
    {
        return algorithm switch
        {
            TunnelCipherAlgorithm.Aegis128L => AeadAlgorithm.Aegis128L,
            TunnelCipherAlgorithm.Aegis256 => AeadAlgorithm.Aegis256,
            _ => null
        };
    }
}
