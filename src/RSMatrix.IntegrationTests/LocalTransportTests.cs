using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RSMatrix.IntegrationTests;

// Server-free regression tests for the integration fixture itself. In particular,
// StringContent cannot reproduce a network response's single-consumption behavior.
public class LocalTransportTests
{
    private static readonly Uri BaseUri = new("http://127.0.0.1:8008");

    [Test]
    public async Task LoginTokenCapture_PreservesResponseForHttpClientAndCaller()
    {
        const string json = """{"access_token":"public-test-token","user_id":"@alice:localhost"}""";
        using var content = new SingleUseContent(json);
        var tokens = new ConcurrentBag<string>();
        using var client = CreateClient(content, HttpStatusCode.OK, tokens);

        // Default ResponseContentRead buffers AFTER the delegating handler returns.
        using var response = await client.PostAsync(new Uri(BaseUri, "/_matrix/client/v3/login"),
            JsonContent.Create(new { }));
        var login = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(login.GetProperty("access_token").GetString()).IsEqualTo("public-test-token");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(json);
        await Assert.That(tokens.Single()).IsEqualTo("public-test-token");
        await Assert.That(content.ConsumptionCount).IsEqualTo(1);
    }

    [Test]
    public async Task FailedLogin_LeavesErrorBodyReadable_WithoutCapturingToken()
    {
        const string json = """{"errcode":"M_FORBIDDEN","error":"Invalid password"}""";
        using var content = new SingleUseContent(json);
        var tokens = new ConcurrentBag<string>();
        using var client = CreateClient(content, HttpStatusCode.Forbidden, tokens);

        using var response = await client.PostAsync(new Uri(BaseUri, "/_matrix/client/v3/login"),
            JsonContent.Create(new { }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(json);
        await Assert.That(tokens.IsEmpty).IsTrue();
        await Assert.That(content.ConsumptionCount).IsEqualTo(1);
    }

    [Test]
    public async Task MalformedLogin_DisposesResponse_WithoutCapturingToken()
    {
        using var content = new SingleUseContent("not JSON");
        var tokens = new ConcurrentBag<string>();
        using var client = CreateClient(content, HttpStatusCode.OK, tokens);

        await Assert.ThrowsAsync<JsonException>(() =>
            client.PostAsync(new Uri(BaseUri, "/_matrix/client/v3/login"), JsonContent.Create(new { })));
        await Assert.That(content.IsDisposed).IsTrue();
        await Assert.That(tokens.IsEmpty).IsTrue();
    }

    private static HttpClient CreateClient(HttpContent content, HttpStatusCode status,
        ConcurrentBag<string> tokens)
        => new(new LocalHomeserver.LocalTransport(BaseUri, tokens, new ResponseHandler(content, status)));

    private sealed class ResponseHandler(HttpContent content, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    // Model HttpConnectionResponseContent: either reading its stream OR copying
    // it into HttpClient's buffer acquires the underlying stream exactly once.
    private sealed class SingleUseContent(string json) : HttpContent
    {
        private readonly MemoryStream _stream = new(Encoding.UTF8.GetBytes(json));
        internal int ConsumptionCount { get; private set; }
        internal bool IsDisposed { get; private set; }

        private Stream ConsumeStream()
        {
            if (++ConsumptionCount > 1)
                throw new InvalidOperationException("The stream was already consumed. It cannot be read again.");
            return _stream;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(ConsumeStream());

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult(ConsumeStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => ConsumeStream().CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                _stream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
