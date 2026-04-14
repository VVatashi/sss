using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleShadowsocks.Protocol;

public static class ProtocolPayloadSerializer
{
    private const int MaxStringBytes = ushort.MaxValue;
    private static readonly byte[][] SingleBytePayloadCache = CreateSingleBytePayloadCache();

    public static byte[] SerializeConnectRequest(ConnectRequest request)
    {
        var payload = new byte[GetConnectRequestLength(request)];
        WriteConnectRequest(payload, request);
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

    public static byte[] SerializeClose(byte reasonCode) => SingleBytePayloadCache[reasonCode];

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
        var payload = new byte[GetUdpDatagramLength(datagram)];
        WriteUdpDatagram(payload, datagram);
        return payload;
    }

    public static UdpDatagram DeserializeUdpDatagram(ReadOnlySpan<byte> payload)
    {
        var request = DeserializeConnectRequest(payload);
        var endpointLength = GetUdpEndpointLength(request);
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

    public static UdpDatagram DeserializeUdpDatagram(ReadOnlyMemory<byte> payload)
    {
        return ParseUdpDatagramCore(payload);
    }

    public static byte[] SerializeHttpRequestStart(HttpRequestStart request)
    {
        var payload = new byte[GetHttpRequestStartLength(request)];
        WriteHttpRequestStart(payload, request);
        return payload;
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
        var payload = new byte[GetHttpResponseStartLength(response)];
        WriteHttpResponseStart(payload, response);
        return payload;
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

    private static void WriteConnectRequest(Span<byte> destination, ConnectRequest request)
    {
        if (destination.Length != GetConnectRequestLength(request))
        {
            throw new InvalidDataException("CONNECT payload destination length mismatch.");
        }

        destination[0] = (byte)request.AddressType;
        switch (request.AddressType)
        {
            case AddressType.IPv4:
                WriteIpConnectRequest(destination, request, AddressFamily.InterNetwork, 4);
                break;
            case AddressType.IPv6:
                WriteIpConnectRequest(destination, request, AddressFamily.InterNetworkV6, 16);
                break;
            case AddressType.Domain:
                WriteDomainConnectRequest(destination, request.Address, request.Port);
                break;
            default:
                throw new InvalidDataException($"Unsupported address type: {request.AddressType}.");
        }
    }

    private static int GetConnectRequestLength(ConnectRequest request)
    {
        return request.AddressType switch
        {
            AddressType.IPv4 => 1 + 4 + 2,
            AddressType.IPv6 => 1 + 16 + 2,
            AddressType.Domain => 1 + 1 + GetValidatedDomainByteCount(request.Address) + 2,
            _ => throw new InvalidDataException($"Unsupported address type: {request.AddressType}.")
        };
    }

    private static int GetUdpDatagramLength(UdpDatagram datagram)
    {
        return GetConnectRequestLength(new ConnectRequest(datagram.AddressType, datagram.Address, datagram.Port))
            + datagram.Payload.Length;
    }

    private static void WriteUdpDatagram(Span<byte> destination, UdpDatagram datagram)
    {
        var endpointLength = GetConnectRequestLength(new ConnectRequest(datagram.AddressType, datagram.Address, datagram.Port));
        if (destination.Length != endpointLength + datagram.Payload.Length)
        {
            throw new InvalidDataException("UDP payload destination length mismatch.");
        }

        WriteConnectRequest(destination.Slice(0, endpointLength), new ConnectRequest(datagram.AddressType, datagram.Address, datagram.Port));
        datagram.Payload.Span.CopyTo(destination.Slice(endpointLength));
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

    private static int GetHttpRequestStartLength(HttpRequestStart request)
    {
        return GetStringEncodedLength(request.Method)
            + GetStringEncodedLength(request.Scheme)
            + GetStringEncodedLength(request.Authority)
            + GetStringEncodedLength(request.PathAndQuery)
            + 1
            + 1
            + GetHeadersLength(request.Headers);
    }

    private static void WriteHttpRequestStart(Span<byte> destination, HttpRequestStart request)
    {
        var offset = 0;
        WriteString(destination, ref offset, request.Method);
        WriteString(destination, ref offset, request.Scheme);
        WriteString(destination, ref offset, request.Authority);
        WriteString(destination, ref offset, request.PathAndQuery);
        destination[offset++] = request.VersionMajor;
        destination[offset++] = request.VersionMinor;
        WriteHeaders(destination, ref offset, request.Headers);
    }

    private static int GetHttpResponseStartLength(HttpResponseStart response)
    {
        return 2
            + GetStringEncodedLength(response.ReasonPhrase)
            + 1
            + 1
            + GetHeadersLength(response.Headers);
    }

    private static void WriteHttpResponseStart(Span<byte> destination, HttpResponseStart response)
    {
        var offset = 0;
        WriteUInt16(destination, ref offset, response.StatusCode);
        WriteString(destination, ref offset, response.ReasonPhrase);
        destination[offset++] = response.VersionMajor;
        destination[offset++] = response.VersionMinor;
        WriteHeaders(destination, ref offset, response.Headers);
    }

    private static int GetHeadersLength(IReadOnlyList<HttpHeader> headers)
    {
        if (headers.Count > ushort.MaxValue)
        {
            throw new InvalidDataException("Too many HTTP headers.");
        }

        var total = 2;
        foreach (var header in headers)
        {
            total += GetStringEncodedLength(header.Name);
            total += GetStringEncodedLength(header.Value);
        }

        return total;
    }

    private static void WriteHeaders(Span<byte> destination, ref int offset, IReadOnlyList<HttpHeader> headers)
    {
        WriteUInt16(destination, ref offset, checked((ushort)headers.Count));
        foreach (var header in headers)
        {
            WriteString(destination, ref offset, header.Name);
            WriteString(destination, ref offset, header.Value);
        }
    }

    private static int GetStringEncodedLength(string? value)
    {
        value ??= string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxStringBytes)
        {
            throw new InvalidDataException("String payload is too long.");
        }

        return 2 + byteCount;
    }

    private static void WriteString(Span<byte> destination, ref int offset, string? value)
    {
        value ??= string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxStringBytes)
        {
            throw new InvalidDataException("String payload is too long.");
        }

        WriteUInt16(destination, ref offset, (ushort)byteCount);
        if (byteCount == 0)
        {
            return;
        }

        var written = Encoding.UTF8.GetBytes(value.AsSpan(), destination.Slice(offset, byteCount));
        if (written != byteCount)
        {
            throw new InvalidDataException("Failed to serialize string payload.");
        }

        offset += written;
    }

    private static void WriteUInt16(Span<byte> destination, ref int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset, 2), value);
        offset += 2;
    }

    private static void WriteIpConnectRequest(Span<byte> destination, ConnectRequest request, AddressFamily expectedFamily, int ipLength)
    {
        if (!IPAddress.TryParse(request.Address, out var ipAddress))
        {
            throw new InvalidDataException($"Invalid IP address: {request.Address}.");
        }

        if (ipAddress.AddressFamily != expectedFamily)
        {
            throw new InvalidDataException($"IP address family mismatch for {request.Address}.");
        }

        if (!ipAddress.TryWriteBytes(destination.Slice(1, ipLength), out var bytesWritten) || bytesWritten != ipLength)
        {
            throw new InvalidDataException($"Failed to serialize IP address: {request.Address}.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(1 + ipLength, 2), request.Port);
    }

    private static void WriteDomainConnectRequest(Span<byte> destination, string domain, ushort port)
    {
        var byteCount = GetValidatedDomainByteCount(domain);
        destination[1] = (byte)byteCount;

        var written = Encoding.ASCII.GetBytes(domain.AsSpan(), destination.Slice(2, byteCount));
        if (written != byteCount)
        {
            throw new InvalidDataException("Failed to serialize domain.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2 + byteCount, 2), port);
    }

    private static int GetValidatedDomainByteCount(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new InvalidDataException("Domain must not be empty.");
        }

        var byteCount = Encoding.ASCII.GetByteCount(domain);
        if (byteCount > byte.MaxValue)
        {
            throw new InvalidDataException("Domain is too long. Max length is 255 bytes.");
        }

        return byteCount;
    }

    private static UdpDatagram ParseUdpDatagramCore(ReadOnlyMemory<byte> payload)
    {
        var request = DeserializeConnectRequest(payload.Span);
        var endpointLength = GetUdpEndpointLength(request);

        if (payload.Length < endpointLength)
        {
            throw new InvalidDataException("UDP payload is too short.");
        }

        return new UdpDatagram(
            request.AddressType,
            request.Address,
            request.Port,
            payload.Slice(endpointLength));
    }

    private static int GetUdpEndpointLength(ConnectRequest request)
    {
        return request.AddressType switch
        {
            AddressType.IPv4 => 1 + 4 + 2,
            AddressType.IPv6 => 1 + 16 + 2,
            AddressType.Domain => 1 + 1 + GetValidatedDomainByteCount(request.Address) + 2,
            _ => throw new InvalidDataException($"Unsupported UDP address type: {request.AddressType}.")
        };
    }

    private static byte[][] CreateSingleBytePayloadCache()
    {
        var cache = new byte[byte.MaxValue + 1][];
        for (var i = 0; i < cache.Length; i++)
        {
            cache[i] = [(byte)i];
        }

        return cache;
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
