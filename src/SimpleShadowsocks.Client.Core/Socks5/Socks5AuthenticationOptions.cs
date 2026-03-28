using System.Text;

namespace SimpleShadowsocks.Client.Socks5;

public sealed class Socks5AuthenticationOptions
{
    public static Socks5AuthenticationOptions Disabled { get; } = new();

    public Socks5AuthenticationOptions()
    {
        Enabled = false;
        Username = string.Empty;
        Password = string.Empty;
    }

    public Socks5AuthenticationOptions(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);

        var usernameBytes = Encoding.UTF8.GetByteCount(username);
        var passwordBytes = Encoding.UTF8.GetByteCount(password);
        if (usernameBytes is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(username), "SOCKS5 username must be 1..255 bytes in UTF-8.");
        }

        if (passwordBytes is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(password), "SOCKS5 password must be 1..255 bytes in UTF-8.");
        }

        Enabled = true;
        Username = username;
        Password = password;
    }

    public bool Enabled { get; }

    public string Username { get; }

    public string Password { get; }
}
