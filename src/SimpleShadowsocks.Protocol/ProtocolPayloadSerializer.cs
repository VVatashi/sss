using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleShadowsocks.Protocol;

public static class ProtocolPayloadSerializer
{
    public static byte[] SerializeConnectRequest(ConnectRequest request)
    {
        return request.AddressType switch
        {
            AddressType.IPv4 => SerializeIpConnectRequest(request, AddressFamily.InterNetwork, 4),
            AddressType.IPv6 => SerializeIpConnectRequest(request, AddressFamily.InterNetworkV6, 16),
            AddressType.Domain => SerializeDomainConnectRequest(request.Address, request.Port),
            _ => throw new InvalidDataException($"Unsupported address type: {request.AddressType}.")
        };
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

    public static byte[] SerializeUdpDatagram(UdpDatagram datagram)
    {
        var endpointPayload = SerializeConnectRequest(new ConnectRequest(datagram.AddressType, datagram.Address, datagram.Port));
        var payload = new byte[endpointPayload.Length + datagram.Payload.Length];
        Buffer.BlockCopy(endpointPayload, 0, payload, 0, endpointPayload.Length);
        datagram.Payload.CopyTo(payload.AsMemory(endpointPayload.Length));
        return payload;
    }

    public static UdpDatagram DeserializeUdpDatagram(ReadOnlySpan<byte> payload)
    {
        var request = DeserializeConnectRequest(payload);
        var endpointLength = request.AddressType switch
        {
            AddressType.IPv4 => 1 + 4 + 2,
            AddressType.IPv6 => 1 + 16 + 2,
            AddressType.Domain => 1 + 1 + Encoding.ASCII.GetByteCount(request.Address) + 2,
            _ => throw new InvalidDataException($"Unsupported UDP address type: {request.AddressType}.")
        };

        if (payload.Length < endpointLength)
        {
            throw new InvalidDataException("UDP payload is too short.");
        }

        return new UdpDatagram(
            request.AddressType,
            request.Address,
            request.Port,
            payload.Slice(endpointLength).ToArray());
    }

    private static byte[] SerializeIpConnectRequest(ConnectRequest request, AddressFamily expectedFamily, int ipLength)
    {
        if (!IPAddress.TryParse(request.Address, out var ipAddress))
        {
            throw new InvalidDataException($"Invalid IP address: {request.Address}.");
        }

        if (ipAddress.AddressFamily != expectedFamily)
        {
            throw new InvalidDataException($"IP address family mismatch for {request.Address}.");
        }

        var payload = new byte[1 + ipLength + 2];
        payload[0] = (byte)request.AddressType;
        if (!ipAddress.TryWriteBytes(payload.AsSpan(1, ipLength), out var bytesWritten) || bytesWritten != ipLength)
        {
            throw new InvalidDataException($"Failed to serialize IP address: {request.Address}.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1 + ipLength, 2), request.Port);
        return payload;
    }

    private static byte[] SerializeDomainConnectRequest(string domain, ushort port)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new InvalidDataException("Domain must not be empty.");
        }

        var byteCount = Encoding.ASCII.GetByteCount(domain);
        if (byteCount > 255)
        {
            throw new InvalidDataException("Domain is too long. Max length is 255 bytes.");
        }

        var payload = new byte[1 + 1 + byteCount + 2];
        payload[0] = (byte)AddressType.Domain;

        var written = Encoding.ASCII.GetBytes(domain.AsSpan(), payload.AsSpan(2, byteCount));
        if (written != byteCount)
        {
            throw new InvalidDataException("Failed to serialize domain.");
        }

        payload[1] = (byte)byteCount;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2 + byteCount, 2), port);
        return payload;
    }

    private static (string Address, int Consumed) DeserializeIpAddress(ReadOnlySpan<byte> body, int expectedLength)
    {
        if (body.Length < expectedLength + 2)
        {
            throw new InvalidDataException("CONNECT payload is too short for IP address.");
        }

        return (new IPAddress(body.Slice(0, expectedLength)).ToString(), expectedLength);
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
