namespace SimpleShadowsocks.Protocol.Crypto;

internal static class AeadCipherFactory
{
    public static bool IsSupported(TunnelCipherAlgorithm algorithm)
    {
        return algorithm switch
        {
            TunnelCipherAlgorithm.ChaCha20Poly1305 => ChaChaAeadCipherImpl.IsSupported(),
            TunnelCipherAlgorithm.Aes256Gcm => AesGcmAeadCipherImpl.IsSupported(),
            TunnelCipherAlgorithm.Aegis128L => NsecAeadCipherImpl.IsSupported(TunnelCipherAlgorithm.Aegis128L),
            TunnelCipherAlgorithm.Aegis256 => NsecAeadCipherImpl.IsSupported(TunnelCipherAlgorithm.Aegis256),
            _ => false
        };
    }

    public static int GetNonceSize(TunnelCipherAlgorithm algorithm)
    {
        return algorithm switch
        {
            TunnelCipherAlgorithm.ChaCha20Poly1305 => ChaChaAeadCipherImpl.NonceSizeConst,
            TunnelCipherAlgorithm.Aes256Gcm => AesGcmAeadCipherImpl.NonceSizeConst,
            TunnelCipherAlgorithm.Aegis128L or TunnelCipherAlgorithm.Aegis256 => NsecAeadCipherImpl.GetNonceSize(algorithm),
            _ => throw new InvalidDataException($"Unsupported transport cipher algorithm: {(byte)algorithm}.")
        };
    }

    public static int GetTagSize(TunnelCipherAlgorithm algorithm)
    {
        return algorithm switch
        {
            TunnelCipherAlgorithm.ChaCha20Poly1305 => ChaChaAeadCipherImpl.TagSizeConst,
            TunnelCipherAlgorithm.Aes256Gcm => AesGcmAeadCipherImpl.TagSizeConst,
            TunnelCipherAlgorithm.Aegis128L or TunnelCipherAlgorithm.Aegis256 => NsecAeadCipherImpl.GetTagSize(algorithm),
            _ => throw new InvalidDataException($"Unsupported transport cipher algorithm: {(byte)algorithm}.")
        };
    }

    public static IAeadCipherImpl Create(TunnelCipherAlgorithm algorithm, ReadOnlySpan<byte> key)
    {
        return algorithm switch
        {
            TunnelCipherAlgorithm.ChaCha20Poly1305 => new ChaChaAeadCipherImpl(key),
            TunnelCipherAlgorithm.Aes256Gcm => new AesGcmAeadCipherImpl(key),
            TunnelCipherAlgorithm.Aegis128L or TunnelCipherAlgorithm.Aegis256 => new NsecAeadCipherImpl(algorithm, key),
            _ => throw new InvalidDataException($"Unsupported transport cipher algorithm: {(byte)algorithm}.")
        };
    }
}

