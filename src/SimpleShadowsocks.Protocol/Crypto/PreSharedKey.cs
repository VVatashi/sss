using System.Security.Cryptography;
using System.Text;

namespace SimpleShadowsocks.Protocol.Crypto;

public static class PreSharedKey
{
    public static byte[] Derive32Bytes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("SharedKey must not be empty.");
        }

        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Convert.FromBase64String(value["base64:".Length..]);
            if (decoded.Length != 32)
            {
                throw new InvalidDataException("base64 key must decode to exactly 32 bytes.");
            }

            return decoded;
        }

        if (value.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Convert.FromHexString(value["hex:".Length..]);
            if (decoded.Length != 32)
            {
                throw new InvalidDataException("hex key must decode to exactly 32 bytes.");
            }

            return decoded;
        }

        // Passphrase mode: derive fixed-size key with SHA-256.
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }
}
