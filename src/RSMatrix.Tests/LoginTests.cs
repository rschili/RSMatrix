using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Http;

namespace RSMatrix.Tests;

public class LoginTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Connect_InitializesAndRecoversWithoutAnotherLogin(bool failInitialization)
    {
        using var handler = new LoginHandler(failInitialization);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var delays = new List<TimeSpan>();
        var client = await MatrixTextClient.ConnectAsync("@bot:example.org", "test-password", "fixed-device",
            new ClientFactory(handler), lifetime.Token, NullLogger.Instance, (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays.Add(delay);
                return Task.CompletedTask;
            });

        // The fake server ends sync with a terminal error. It must reach the channel,
        // rather than silently triggering another login or leaving a background loop.
        var error = await Assert.ThrowsAsync<MatrixResponseException>(() =>
            client.Messages.Completion.WaitAsync(lifetime.Token));
        await Assert.That(error!.ErrorCode).IsEqualTo("M_UNKNOWN_TOKEN");
        await Assert.That(handler.LoginCount).IsEqualTo(1);
        await Assert.That(handler.CapabilitiesCount).IsEqualTo(failInitialization ? 2 : 1);
        await Assert.That(handler.PresenceCount).IsEqualTo(failInitialization ? 2 : 1);
        await Assert.That(delays.Count).IsEqualTo(failInitialization ? 3 : 0);
        await Assert.That(handler.SyncQueries.Count).IsEqualTo(failInitialization ? 3 : 2);
        await Assert.That(handler.SyncQueries[0].Contains("since=")).IsFalse();
        await Assert.That(handler.SyncQueries.Skip(1).All(q => q.Contains("since=cursor%26one"))).IsTrue();
        await Assert.That(handler.AuthenticatedTokens.All(t => t == "session-token")).IsTrue();
        await Assert.That(client.IsSyncing).IsFalse();
        await Assert.That(client.HttpClientParameters.BearerToken).IsEqualTo("session-token");
        await Assert.That(client.Filter!.FilterId).IsEqualTo("filter1");

        using var login = JsonDocument.Parse(handler.LoginBody!);
        await Assert.That(login.RootElement.GetProperty("type").GetString()).IsEqualTo("m.login.password");
        await Assert.That(login.RootElement.GetProperty("identifier").GetProperty("user").GetString()).IsEqualTo("@bot:example.org");
        await Assert.That(login.RootElement.GetProperty("device_id").GetString()).IsEqualTo("fixed-device");
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class LoginHandler(bool failInitialization) : HttpMessageHandler
    {
        public int LoginCount { get; private set; }
        public int CapabilitiesCount { get; private set; }
        public int PresenceCount { get; private set; }
        public string? LoginBody { get; private set; }
        public List<string?> AuthenticatedTokens { get; } = [];
        public List<string> SyncQueries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/matrix/client")
                return Json("""{"m.homeserver":{"base_url":"https://example.org"}}""");
            if (path == "/_matrix/client/versions")
                return Json("""{"versions":["v1.12"]}""");
            if (path == "/_matrix/client/v3/login" && request.Method == HttpMethod.Get)
                return Json("""{"flows":[{"type":"m.login.password"}]}""");
            if (path == "/_matrix/client/v3/login" && request.Method == HttpMethod.Post)
            {
                LoginCount++;
                LoginBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("""{"access_token":"session-token","device_id":"fixed-device","user_id":"@bot:example.org"}""");
            }

            AuthenticatedTokens.Add(request.Headers.Authorization?.Parameter);
            if (path == "/_matrix/client/v3/capabilities")
            {
                CapabilitiesCount++;
                if (failInitialization && CapabilitiesCount == 1)
                    return Json("""{"errcode":"M_UNKNOWN","error":"Temporarily unavailable"}""", HttpStatusCode.ServiceUnavailable);
                return Json("""{"capabilities":{}}""");
            }
            if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                PresenceCount++;
                if (failInitialization && PresenceCount == 1)
                    throw new HttpRequestException("Temporary network failure after login");
                return Json("{}");
            }
            if (path.EndsWith("/filter", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                return Json("""{"filter_id":"filter1"}""");
            if (path.EndsWith("/filter/filter1", StringComparison.Ordinal))
                return Json("{}");
            if (path == "/_matrix/client/v3/sync")
            {
                SyncQueries.Add(request.RequestUri.Query);
                if (SyncQueries.Count == 1)
                    return Json("""{"next_batch":"cursor&one"}""");
                if (failInitialization && SyncQueries.Count == 2)
                    return Json("""{"errcode":"M_UNKNOWN"}""", HttpStatusCode.ServiceUnavailable);
                return Json("""{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}""", HttpStatusCode.Unauthorized);
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
