using System.Net.Http;
using SimpleShadowsocks.Protocol;

namespace SimpleShadowsocks.Client.Tunnel;

public interface ITunnelReverseHttpHandler
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestStart requestStart,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);
}
