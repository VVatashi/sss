using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Socks5;

public sealed partial class Socks5Server
{
    private async Task<bool> HandleGreetingAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        if (!await TryReadExactAsync(stream, header, cancellationToken))
        {
            return false;
        }

        if (header[0] != SocksVersion)
        {
            return false;
        }

        var methodCount = header[1];
        if (methodCount == 0)
        {
            return false;
        }

        var methods = new byte[methodCount];
        if (!await TryReadExactAsync(stream, methods, cancellationToken))
        {
            return false;
        }

        var selectedMethod = SelectAuthenticationMethod(methods);
        var response = new[] { SocksVersion, selectedMethod };
        await stream.WriteAsync(response, cancellationToken);

        if (selectedMethod == AuthNoAcceptableMethods)
        {
            return false;
        }

        if (selectedMethod == AuthUsernamePassword)
        {
            return await HandleUsernamePasswordAuthenticationAsync(stream, cancellationToken);
        }

        return true;
    }

    private byte SelectAuthenticationMethod(byte[] methods)
    {
        if (_authenticationOptions.Enabled)
        {
            return methods.Contains(AuthUsernamePassword)
                ? AuthUsernamePassword
                : AuthNoAcceptableMethods;
        }

        return methods.Contains(AuthNone)
            ? AuthNone
            : AuthNoAcceptableMethods;
    }

    private async Task<bool> HandleUsernamePasswordAuthenticationAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var versionBuffer = new byte[1];
        if (!await TryReadExactAsync(stream, versionBuffer, cancellationToken))
        {
            return false;
        }

        if (versionBuffer[0] != UsernamePasswordAuthVersion)
        {
            await stream.WriteAsync(new byte[] { UsernamePasswordAuthVersion, 0x01 }, cancellationToken);
            return false;
        }

        var username = await ReadLengthPrefixedStringAsync(stream, cancellationToken);
        var password = await ReadLengthPrefixedStringAsync(stream, cancellationToken);
        if (username is null || password is null)
        {
            await stream.WriteAsync(new byte[] { UsernamePasswordAuthVersion, 0x01 }, cancellationToken);
            return false;
        }

        var authenticated = AreEqual(username, _authenticationOptions.Username)
            && AreEqual(password, _authenticationOptions.Password);

        await stream.WriteAsync(
            new byte[] { UsernamePasswordAuthVersion, authenticated ? (byte)0x00 : (byte)0x01 },
            cancellationToken);

        if (!authenticated)
        {
            StructuredLog.Warn("socks5-server", "SOCKS5/AUTH", "client authentication failed");
        }

        return authenticated;
    }

    private static async Task<string?> ReadLengthPrefixedStringAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[1];
        if (!await TryReadExactAsync(stream, lengthBuffer, cancellationToken))
        {
            return null;
        }

        var length = lengthBuffer[0];
        if (length == 0)
        {
            return null;
        }

        var valueBytes = new byte[length];
        if (!await TryReadExactAsync(stream, valueBytes, cancellationToken))
        {
            return null;
        }

        return Encoding.UTF8.GetString(valueBytes);
    }

    private static bool AreEqual(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task<Socks5ConnectRequest?> ReadConnectRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await TryReadExactAsync(stream, header, cancellationToken))
        {
            return null;
        }

        var version = header[0];
        var command = header[1];
        var addressType = header[3];

        if (version != SocksVersion)
        {
            return null;
        }

        var host = addressType switch
        {
            AddressTypeIPv4 => await ReadIPv4AddressAsync(stream, cancellationToken),
            AddressTypeIPv6 => await ReadIPv6AddressAsync(stream, cancellationToken),
            AddressTypeDomain => await ReadDomainAddressAsync(stream, cancellationToken),
            _ => null
        };

        if (host is null)
        {
            await SendReplyAsync(stream, replyCode: 0x08, null, cancellationToken);
            return null;
        }

        var portBytes = new byte[2];
        if (!await TryReadExactAsync(stream, portBytes, cancellationToken))
        {
            return null;
        }

        var port = (ushort)((portBytes[0] << 8) | portBytes[1]);

        if (command is not CommandConnect and not CommandUdpAssociate)
        {
            await SendReplyAsync(stream, replyCode: 0x07, null, cancellationToken);
            return null;
        }

        return new Socks5ConnectRequest(command, host, port, addressType);
    }

    private static async Task<string?> ReadIPv4AddressAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        if (!await TryReadExactAsync(stream, bytes, cancellationToken))
        {
            return null;
        }

        return new IPAddress(bytes).ToString();
    }

    private static async Task<string?> ReadIPv6AddressAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[16];
        if (!await TryReadExactAsync(stream, bytes, cancellationToken))
        {
            return null;
        }

        return new IPAddress(bytes).ToString();
    }

    private static async Task<string?> ReadDomainAddressAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[1];
        if (!await TryReadExactAsync(stream, lengthBuffer, cancellationToken))
        {
            return null;
        }

        var length = lengthBuffer[0];
        if (length == 0)
        {
            return null;
        }

        var domainBytes = new byte[length];
        if (!await TryReadExactAsync(stream, domainBytes, cancellationToken))
        {
            return null;
        }

        return Encoding.ASCII.GetString(domainBytes);
    }

    private static AddressType ToProtocolAddressType(byte socksAddressType)
    {
        return socksAddressType switch
        {
            AddressTypeIPv4 => AddressType.IPv4,
            AddressTypeIPv6 => AddressType.IPv6,
            AddressTypeDomain => AddressType.Domain,
            _ => throw new InvalidDataException($"Unsupported SOCKS address type: {socksAddressType}")
        };
    }

    private static AddressType ToProtocolAddressType(IPAddress ipAddress)
    {
        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => AddressType.IPv4,
            AddressFamily.InterNetworkV6 => AddressType.IPv6,
            _ => throw new InvalidDataException($"Unsupported IP address family: {ipAddress.AddressFamily}")
        };
    }

    private static async Task SendReplyAsync(
        NetworkStream stream,
        byte replyCode,
        IPEndPoint? boundEndPoint,
        CancellationToken cancellationToken)
    {
        if (boundEndPoint is null)
        {
            var failedReply = new byte[]
            {
                SocksVersion, replyCode, 0x00, AddressTypeIPv4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            await stream.WriteAsync(failedReply, cancellationToken);
            return;
        }

        var addressBytes = boundEndPoint.Address.GetAddressBytes();
        var addressType = boundEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? AddressTypeIPv6
            : AddressTypeIPv4;

        var response = new byte[4 + addressBytes.Length + 2];
        response[0] = SocksVersion;
        response[1] = replyCode;
        response[2] = 0x00;
        response[3] = addressType;
        Buffer.BlockCopy(addressBytes, 0, response, 4, addressBytes.Length);
        response[^2] = (byte)(boundEndPoint.Port >> 8);
        response[^1] = (byte)(boundEndPoint.Port & 0xFF);

        await stream.WriteAsync(response, cancellationToken);
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static byte[] BuildUdpRequestDatagram(AddressType addressType, string address, ushort port, ReadOnlySpan<byte> payload)
    {
        var endpointPayload = ProtocolPayloadSerializer.SerializeConnectRequest(new ConnectRequest(addressType, address, port));
        var datagram = new byte[3 + endpointPayload.Length + payload.Length];
        datagram[0] = 0x00;
        datagram[1] = 0x00;
        datagram[2] = 0x00;
        Buffer.BlockCopy(endpointPayload, 0, datagram, 3, endpointPayload.Length);
        payload.CopyTo(datagram.AsSpan(3 + endpointPayload.Length));
        return datagram;
    }

    private static bool TryParseUdpRequestDatagram(ReadOnlySpan<byte> datagram, out Socks5UdpClientDatagram parsed)
    {
        parsed = default;
        if (datagram.Length < 3)
        {
            return false;
        }

        if (datagram[0] != 0x00 || datagram[1] != 0x00)
        {
            return false;
        }

        try
        {
            var fragment = datagram[2];
            parsed = new Socks5UdpClientDatagram(fragment, ProtocolPayloadSerializer.DeserializeUdpDatagram(datagram.Slice(3)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct Socks5UdpClientDatagram(byte Fragment, UdpDatagram Datagram);

    private sealed class Socks5UdpFragmentReassembler
    {
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);
        private readonly Dictionary<UdpFragmentKey, FragmentState> _states = new();

        public bool TryReassemble(Socks5UdpClientDatagram incoming, out UdpDatagram datagram)
        {
            datagram = default;
            CleanupExpired(DateTime.UtcNow);

            if (incoming.Fragment == 0x00)
            {
                return TryAssign(incoming.Datagram, out datagram);
            }

            var fragmentIndex = (byte)(incoming.Fragment & 0x7F);
            var isLastFragment = (incoming.Fragment & 0x80) != 0;
            if (fragmentIndex == 0)
            {
                return false;
            }

            var key = new UdpFragmentKey(
                incoming.Datagram.AddressType,
                incoming.Datagram.Address,
                incoming.Datagram.Port);
            if (!_states.TryGetValue(key, out var state))
            {
                state = new FragmentState();
                _states[key] = state;
            }

            if (!state.TryAddFragment(fragmentIndex, isLastFragment, incoming.Datagram.Payload.Span, DateTime.UtcNow))
            {
                _states.Remove(key);
                return false;
            }

            if (!state.TryBuildPayload(out var payload))
            {
                return false;
            }

            _states.Remove(key);
            var assembled = new UdpDatagram(
                incoming.Datagram.AddressType,
                incoming.Datagram.Address,
                incoming.Datagram.Port,
                payload);
            return TryAssign(assembled, out datagram);
        }

        private void CleanupExpired(DateTime utcNow)
        {
            if (_states.Count == 0)
            {
                return;
            }

            List<UdpFragmentKey>? expiredKeys = null;
            foreach (var (key, state) in _states)
            {
                if (!state.IsExpired(utcNow))
                {
                    continue;
                }

                expiredKeys ??= new List<UdpFragmentKey>();
                expiredKeys.Add(key);
            }

            if (expiredKeys is null)
            {
                return;
            }

            foreach (var key in expiredKeys)
            {
                _states.Remove(key);
            }
        }

        private static bool TryAssign(UdpDatagram source, out UdpDatagram destination)
        {
            if (source.Payload.Length > ProtocolConstants.MaxPayloadLength)
            {
                destination = default;
                return false;
            }

            destination = source;
            return true;
        }

        private sealed class FragmentState
        {
            private readonly Dictionary<byte, byte[]> _payloadByIndex = new();
            private byte? _lastFragmentIndex;
            private byte _highestFragmentIndex;
            private DateTime _expiresAtUtc = DateTime.UtcNow + DefaultTtl;

            public bool IsExpired(DateTime utcNow) => utcNow >= _expiresAtUtc;

            public bool TryAddFragment(byte fragmentIndex, bool isLast, ReadOnlySpan<byte> payload, DateTime utcNow)
            {
                if (fragmentIndex < _highestFragmentIndex)
                {
                    // RFC 1928: when fragment number decreases, queue should reinitialize.
                    _payloadByIndex.Clear();
                    _lastFragmentIndex = null;
                    _highestFragmentIndex = 0;
                }

                if (_payloadByIndex.ContainsKey(fragmentIndex))
                {
                    return false;
                }

                if (_lastFragmentIndex is not null && fragmentIndex > _lastFragmentIndex.Value)
                {
                    return false;
                }

                _payloadByIndex[fragmentIndex] = payload.ToArray();
                if (fragmentIndex > _highestFragmentIndex)
                {
                    _highestFragmentIndex = fragmentIndex;
                }

                if (isLast)
                {
                    _lastFragmentIndex = fragmentIndex;
                }

                _expiresAtUtc = utcNow + DefaultTtl;
                return true;
            }

            public bool TryBuildPayload(out byte[] payload)
            {
                payload = Array.Empty<byte>();
                if (_lastFragmentIndex is null)
                {
                    return false;
                }

                var lastIndex = _lastFragmentIndex.Value;
                var totalLength = 0;
                for (byte index = 1; index <= lastIndex; index++)
                {
                    if (!_payloadByIndex.TryGetValue(index, out var chunk))
                    {
                        return false;
                    }

                    totalLength += chunk.Length;
                    if (totalLength > ProtocolConstants.MaxPayloadLength)
                    {
                        return false;
                    }
                }

                var combined = new byte[totalLength];
                var offset = 0;
                for (byte index = 1; index <= lastIndex; index++)
                {
                    var chunk = _payloadByIndex[index];
                    Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
                    offset += chunk.Length;
                }

                payload = combined;
                return true;
            }
        }

        private readonly record struct UdpFragmentKey(AddressType AddressType, string Address, ushort Port);
    }
}
