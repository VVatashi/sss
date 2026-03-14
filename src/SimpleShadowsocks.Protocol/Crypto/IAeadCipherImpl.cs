namespace SimpleShadowsocks.Protocol.Crypto;

internal interface IAeadCipherImpl : IDisposable
{
    int NonceSize { get; }
    int TagSize { get; }
    int Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce, Span<byte> destination);
    int Decrypt(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> nonce, Span<byte> destination);
}

