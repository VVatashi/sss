using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleShadowsocks.Protocol;

public static class ProtocolPayloadSerializer
{
    public static byte[] SerializeConnectRequest(ConnectRequest request)
    {
        var addressBytes = request.AddressType switch
        {
            AddressType.IPv4 => SerializeIpAddress(request.Address, AddressFamily.InterNetwork),
            AddressType.IPv6 => SerializeIpAddress(request.Address, AddressFamily.InterNetworkV6),
            AddressType.Domain => SerializeDomain(request.Address),
            _ => throw new InvalidDataException($"Unsupported address type: {request.AddressType}.")
        };

        var payload = new byte[1 + addressBytes.Length + 2];
        payload[0] = (byte)request.AddressType;
        Buffer.BlockCopy(addressBytes, 0, payload, 1, addressBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1 + addressBytes.Length, 2), request.Port);
        return payload;
    }

    public static ConnectRequest DeserializeConnectRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1 + 2)
        {
            throw new InvalidDataException("CONNECT payload is too short.");
        }

        var addressType = (AddressType)payload[0];
        var body = payload.Slice(1);

        var (address, consumed) = addressType switch
        {
            AddressType.IPv4 => DeserializeIpAddress(body, 4),
            AddressType.IPv6 => DeserializeIpAddress(body, 16),
            AddressType.Domain => DeserializeDomain(body),
            _ => throw new InvalidDataException($"Unsupported address type: {addressType}.")
        };

        if (body.Length < consumed + 2)
        {
            throw new InvalidDataException("CONNECT payload does not include port.");
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(consumed, 2));
        return new ConnectRequest(addressType, address, port);
    }

    public static byte[] SerializeClose(byte reasonCode) => new[] { reasonCode };

    public static byte DeserializeClose(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new InvalidDataException("CLOSE payload must contain exactly one byte.");
        }

        return payload[0];
    }

    public static byte[] SerializeHeartbeat(ulong nonce)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, nonce);
        return payload;
    }

    public static ulong DeserializeHeartbeat(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8)
        {
            throw new InvalidDataException("Heartbeat payload must contain exactly 8 bytes.");
        }

        return BinaryPrimitives.ReadUInt64BigEndian(payload);
    }

    private static byte[] SerializeIpAddress(string value, AddressFamily expectedFamily)
    {
        if (!IPAddress.TryParse(value, out var ipAddress))
        {
            throw new InvalidDataException($"Invalid IP address: {value}.");
        }

        if (ipAddress.AddressFamily != expectedFamily)
        {
            throw new InvalidDataException($"IP address family mismatch for {value}.");
        }

        return ipAddress.GetAddressBytes();
    }

    private static byte[] SerializeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new InvalidDataException("Domain must not be empty.");
        }

        var bytes = Encoding.ASCII.GetBytes(domain);
        if (bytes.Length > 255)
        {
            throw new InvalidDataException("Domain is too long. Max length is 255 bytes.");
        }

        var result = new byte[1 + bytes.Length];
        result[0] = (byte)bytes.Length;
        Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
        return result;
    }

    private static (string Address, int Consumed) DeserializeIpAddress(ReadOnlySpan<byte> body, int expectedLength)
    {
        if (body.Length < expectedLength + 2)
        {
            throw new InvalidDataException("CONNECT payload is too short for IP address.");
        }

        var bytes = body.Slice(0, expectedLength).ToArray();
        return (new IPAddress(bytes).ToString(), expectedLength);
    }

    private static (string Address, int Consumed) DeserializeDomain(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1 + 2)
        {
            throw new InvalidDataException("CONNECT payload is too short for domain.");
        }

        var length = body[0];
        if (length == 0)
        {
            throw new InvalidDataException("Domain length must be greater than zero.");
        }

        if (body.Length < 1 + length + 2)
        {
            throw new InvalidDataException("CONNECT payload domain length is invalid.");
        }

        var domain = Encoding.ASCII.GetString(body.Slice(1, length));
        return (domain, 1 + length);
    }
}
