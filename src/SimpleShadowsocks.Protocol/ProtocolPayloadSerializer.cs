using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleShadowsocks.Protocol;

public static class ProtocolPayloadSerializer
{
    private const int MaxStringBytes = ushort.MaxValue;

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

    public static byte[] SerializeHttpRequestStart(HttpRequestStart request)
    {
        using var stream = new MemoryStream();
        WriteString(stream, request.Method);
        WriteString(stream, request.Scheme);
        WriteString(stream, request.Authority);
        WriteString(stream, request.PathAndQuery);
        stream.WriteByte(request.VersionMajor);
        stream.WriteByte(request.VersionMinor);
        WriteHeaders(stream, request.Headers);
        return stream.ToArray();
    }

    public static HttpRequestStart DeserializeHttpRequestStart(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        var method = reader.ReadString();
        var scheme = reader.ReadString();
        var authority = reader.ReadString();
        var pathAndQuery = reader.ReadString();
        var versionMajor = reader.ReadByte();
        var versionMinor = reader.ReadByte();
        var headers = reader.ReadHeaders();
        reader.EnsureFullyConsumed("HTTP request");
        return new HttpRequestStart(method, scheme, authority, pathAndQuery, versionMajor, versionMinor, headers);
    }

    public static byte[] SerializeHttpResponseStart(HttpResponseStart response)
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, response.StatusCode);
        WriteString(stream, response.ReasonPhrase);
        stream.WriteByte(response.VersionMajor);
        stream.WriteByte(response.VersionMinor);
        WriteHeaders(stream, response.Headers);
        return stream.ToArray();
    }

    public static HttpResponseStart DeserializeHttpResponseStart(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        var statusCode = reader.ReadUInt16();
        var reasonPhrase = reader.ReadString();
        var versionMajor = reader.ReadByte();
        var versionMinor = reader.ReadByte();
        var headers = reader.ReadHeaders();
        reader.EnsureFullyConsumed("HTTP response");
        return new HttpResponseStart(statusCode, reasonPhrase, versionMajor, versionMinor, headers);
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

    private static void WriteHeaders(Stream stream, IReadOnlyList<HttpHeader> headers)
    {
        if (headers.Count > ushort.MaxValue)
        {
            throw new InvalidDataException("Too many HTTP headers.");
        }

        WriteUInt16(stream, (ushort)headers.Count);
        foreach (var header in headers)
        {
            WriteString(stream, header.Name);
            WriteString(stream, header.Value);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        value ??= string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxStringBytes)
        {
            throw new InvalidDataException("String payload is too long.");
        }

        WriteUInt16(stream, (ushort)byteCount);
        if (byteCount == 0)
        {
            return;
        }

        var buffer = new byte[byteCount];
        var written = Encoding.UTF8.GetBytes(value, buffer);
        if (written != byteCount)
        {
            throw new InvalidDataException("Failed to serialize string payload.");
        }

        stream.Write(buffer, 0, buffer.Length);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private ref struct SpanReader
    {
        private ReadOnlySpan<byte> _payload;
        private int _offset;

        public SpanReader(ReadOnlySpan<byte> payload)
        {
            _payload = payload;
            _offset = 0;
        }

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _payload[_offset++];
        }

        public ushort ReadUInt16()
        {
            EnsureAvailable(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(_payload.Slice(_offset, 2));
            _offset += 2;
            return value;
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            if (length == 0)
            {
                return string.Empty;
            }

            EnsureAvailable(length);
            var value = Encoding.UTF8.GetString(_payload.Slice(_offset, length));
            _offset += length;
            return value;
        }

        public HttpHeader[] ReadHeaders()
        {
            var count = ReadUInt16();
            var headers = new HttpHeader[count];
            for (var i = 0; i < count; i++)
            {
                headers[i] = new HttpHeader(ReadString(), ReadString());
            }

            return headers;
        }

        public void EnsureFullyConsumed(string payloadName)
        {
            if (_offset != _payload.Length)
            {
                throw new InvalidDataException($"{payloadName} payload has trailing bytes.");
            }
        }

        private void EnsureAvailable(int count)
        {
            if (_payload.Length - _offset < count)
            {
                throw new InvalidDataException("Payload is truncated.");
            }
        }
    }
}
