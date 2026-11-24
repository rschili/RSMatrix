using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace RSMatrix.Desktop.Matrix;

/// <summary>
/// Gives RSMatrix an HttpClient that survives its 60 second sync long poll and can point it at an explicit homeserver.
/// </summary>
internal sealed class DesktopHttpClientFactory(Uri? homeserver) : IHttpClientFactory, IDisposable
{
    private readonly HttpMessageHandler _handler = homeserver == null
        ? CreateSocketsHandler()
        : new HomeserverOverrideHandler(homeserver, CreateSocketsHandler());

    public HttpClient CreateClient(string name)
        => new(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose() => _handler.Dispose();

    private static SocketsHttpHandler CreateSocketsHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    };
}

/// <summary>
/// RSMatrix finds the homeserver through https://&lt;user domain&gt;/.well-known/matrix/client and has no way to pass
/// a homeserver URL. This handler answers that one request with the given URL, so local servers like
/// http://127.0.0.1:8008 and servers without well-known setup work. All other requests pass through.
/// </summary>
internal sealed class HomeserverOverrideHandler(Uri homeserver, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && request.RequestUri is { AbsolutePath: "/.well-known/matrix/client" })
        {
            var json = new JsonObject
            {
                ["m.homeserver"] = new JsonObject { ["base_url"] = homeserver.AbsoluteUri.TrimEnd('/') },
            }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
        return base.SendAsync(request, cancellationToken);
    }
}
