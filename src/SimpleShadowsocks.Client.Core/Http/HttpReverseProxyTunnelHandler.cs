using System.Net;
using System.Net.Http;
using SimpleShadowsocks.Client.Tunnel;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Http;

public sealed class HttpReverseProxyTunnelHandler : ITunnelReverseHttpHandler
{
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Expect",
        "Via",
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto"
    };

    private readonly IReadOnlyList<Route> _routes;

    public HttpReverseProxyTunnelHandler(IEnumerable<Route> routes)
    {
        _routes = (routes ?? throw new ArgumentNullException(nameof(routes))).ToArray();
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestStart requestStart,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var route = MatchRoute(requestStart);
        if (route is null)
        {
            return CreateSyntheticResponse(HttpStatusCode.NotFound, "Not Found");
        }

        var request = BuildHttpRequestMessage(route, requestStart, body);
        try
        {
            return await SharedHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private Route? MatchRoute(HttpRequestStart requestStart)
    {
        var incomingHost = ExtractHost(requestStart.Authority);
        foreach (var route in _routes)
        {
            if (route.Host is not null
                && !string.Equals(route.Host, incomingHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (route.PathPrefix is not null
                && !PathMatches(route.PathPrefix, requestStart.PathAndQuery))
            {
                continue;
            }

            return route;
        }

        return null;
    }

    private static HttpRequestMessage BuildHttpRequestMessage(Route route, HttpRequestStart requestStart, ReadOnlyMemory<byte> body)
    {
        var targetUri = BuildTargetUri(route, requestStart.PathAndQuery);
        var message = new HttpRequestMessage(new HttpMethod(requestStart.Method), targetUri)
        {
            Version = new Version(requestStart.VersionMajor, requestStart.VersionMinor),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        ByteArrayContent? content = null;
        if (!body.IsEmpty)
        {
            content = new ByteArrayContent(body.ToArray());
            message.Content = content;
        }

        foreach (var header in SanitizeHeaders(requestStart.Headers))
        {
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Host = header.Value;
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                message.Content ??= content = new ByteArrayContent(Array.Empty<byte>());
                message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return message;
    }

    private static Uri BuildTargetUri(Route route, string requestPathAndQuery)
    {
        SplitPathAndQuery(requestPathAndQuery, out var requestPath, out var query);
        var effectivePath = requestPath;
        if (route.PathPrefix is not null && route.StripPathPrefix)
        {
            effectivePath = requestPath[route.PathPrefix.Length..];
            if (effectivePath.Length == 0)
            {
                effectivePath = "/";
            }
            else if (effectivePath[0] != '/')
            {
                effectivePath = "/" + effectivePath;
            }
        }

        var basePath = route.TargetBaseUri.AbsolutePath == "/"
            ? string.Empty
            : route.TargetBaseUri.AbsolutePath.TrimEnd('/');
        var combinedPath = CombinePaths(basePath, effectivePath);

        var builder = new UriBuilder(route.TargetBaseUri)
        {
            Path = combinedPath,
            Query = query
        };

        return builder.Uri;
    }

    private static IReadOnlyList<HttpHeader> SanitizeHeaders(IReadOnlyList<HttpHeader> headers)
    {
        var connectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (!header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                connectionTokens.Add(token);
            }
        }

        var sanitized = new List<HttpHeader>(headers.Count);
        foreach (var header in headers)
        {
            if (HopByHopHeaders.Contains(header.Name) || connectionTokens.Contains(header.Name))
            {
                continue;
            }

            sanitized.Add(header);
        }

        return sanitized;
    }

    private static HttpResponseMessage CreateSyntheticResponse(HttpStatusCode statusCode, string reasonPhrase)
    {
        return new HttpResponseMessage(statusCode)
        {
            ReasonPhrase = reasonPhrase,
            Version = HttpVersion.Version11,
            Content = new ByteArrayContent([])
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = UpstreamConnectTimeout
        };

        return new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static string ExtractHost(string authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return string.Empty;
        }

        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            var index = authority.IndexOf(']');
            return index >= 0 ? authority[1..index] : authority;
        }

        var colonIndex = authority.LastIndexOf(':');
        return colonIndex > 0 ? authority[..colonIndex] : authority;
    }

    private static bool PathMatches(string prefix, string pathAndQuery)
    {
        SplitPathAndQuery(pathAndQuery, out var path, out _);
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (prefix.EndsWith("/", StringComparison.Ordinal)
                    || (path.Length > prefix.Length && path[prefix.Length] == '/')));
    }

    private static void SplitPathAndQuery(string pathAndQuery, out string path, out string query)
    {
        var normalized = string.IsNullOrWhiteSpace(pathAndQuery) ? "/" : pathAndQuery;
        var queryIndex = normalized.IndexOf('?');
        if (queryIndex < 0)
        {
            path = normalized;
            query = string.Empty;
            return;
        }

        path = normalized[..queryIndex];
        query = normalized[(queryIndex + 1)..];
    }

    private static string CombinePaths(string basePath, string requestPath)
    {
        var normalizedRequestPath = string.IsNullOrEmpty(requestPath) ? "/" : requestPath;
        if (!normalizedRequestPath.StartsWith("/", StringComparison.Ordinal))
        {
            normalizedRequestPath = "/" + normalizedRequestPath;
        }

        if (string.IsNullOrEmpty(basePath))
        {
            return normalizedRequestPath;
        }

        return basePath + normalizedRequestPath;
    }

    public sealed record Route(
        string? Host,
        string? PathPrefix,
        Uri TargetBaseUri,
        bool StripPathPrefix);
}
