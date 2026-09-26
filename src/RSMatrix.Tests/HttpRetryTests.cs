using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Http;

namespace RSMatrix.Tests;

public class HttpRetryTests
{
    [Test]
    [Arguments("120", 30_000, 120)]
    [Arguments(null, 90_000, 90)]
    [Arguments("invalid", 90_000, 90)]
    public async Task RateLimit_WaitsAndReplaysTheSameRequest(string? header, long retryAfterMs, int expectedSeconds)
    {
        var requests = new List<(string Url, string Body, string? Token)>();
        var delays = new List<TimeSpan>();
        using var handler = new Handler(async (request, token) =>
        {
            requests.Add((request.RequestUri!.AbsoluteUri,
                await request.Content!.ReadAsStringAsync(token), request.Headers.Authorization?.Parameter));
            if (requests.Count != 1)
                return Json(HttpStatusCode.OK, "{}");
            var limited = Json(HttpStatusCode.TooManyRequests,
                $$"""{"errcode":"M_LIMIT_EXCEEDED","retry_after_ms":{{retryAfterMs}}}""");
            if (header != null)
                limited.Headers.TryAddWithoutValidation("Retry-After", header);
            return limited;
        });
        var parameters = Parameters(handler) with
        {
            RetryDelayAsync = (delay, _) => { delays.Add(delay); return Task.CompletedTask; }
        };

        await HttpClientHelper.SendAsync(parameters, "/_matrix/client/v3/rooms/room/send/m.room.message/txn-123",
            HttpMethod.Put, new StringContent("{\"body\":\"hello\"}", Encoding.UTF8, "application/json"));

        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That(requests[0]).IsEqualTo(requests[1]);
        await Assert.That(requests[0].Token).IsEqualTo("test-token");
        await Assert.That(delays.Count).IsEqualTo(1);
        await Assert.That(delays[0]).IsEqualTo(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Test]
    public async Task NonJsonError_PreservesStatusAndHttpDateRetryAfter()
    {
        var date = DateTimeOffset.UtcNow.AddMinutes(2);
        using var handler = new Handler((_, _) =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, "<html>Unavailable</html>");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
            return Task.FromResult(response);
        });
        var before = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<MatrixResponseException>(() =>
            HttpClientHelper.SendAsync(Parameters(handler), "/test"));
        var after = DateTimeOffset.UtcNow;

        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(error.IsTransient).IsTrue();
        // HTTP-date precision is one second.
        await Assert.That(error.RetryAfter >= date - after - TimeSpan.FromSeconds(1)
            && error.RetryAfter <= date - before + TimeSpan.FromSeconds(1)).IsTrue();
    }

    [Test]
    public async Task CancellationDuringRateLimitWait_PreventsAnotherRequest()
    {
        using var lifetime = new CancellationTokenSource();
        var requests = 0;
        using var handler = new Handler((_, _) =>
        {
            requests++;
            // A proxy's non-JSON 429 must still be recognized as rate limiting.
            return Task.FromResult(Json(HttpStatusCode.TooManyRequests, "Too many requests"));
        });
        var parameters = Parameters(handler) with
        {
            CancellationToken = lifetime.Token,
            RetryDelayAsync = (_, _) => { lifetime.Cancel(); return Task.CompletedTask; }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            HttpClientHelper.SendAsync(parameters, "/test"));
        await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    public async Task AuthErrorWithoutOptionalMessage_IsTerminal()
    {
        var requests = 0;
        using var handler = new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.Unauthorized,
                """{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}"""));
        });
        var error = await Assert.ThrowsAsync<MatrixResponseException>(() =>
            HttpClientHelper.SendAsync(Parameters(handler), "/test"));

        await Assert.That(error!.ErrorCode).IsEqualTo("M_UNKNOWN_TOKEN");
        await Assert.That(error.SoftLogout).IsTrue();
        await Assert.That(error.IsTransient).IsFalse();
        await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    public async Task BackoffDoesNotTruncateServerMinimum()
    {
        var delay = MatrixRetry.GetDelay(int.MaxValue);
        await Assert.That(delay >= TimeSpan.FromMinutes(5) && delay < TimeSpan.FromSeconds(301)).IsTrue();
        await Assert.That(MatrixRetry.GetDelay(0, TimeSpan.FromHours(1))).IsEqualTo(TimeSpan.FromHours(1));
    }

    private static HttpClientParameters Parameters(HttpMessageHandler handler)
        => new(new Factory(handler), "https://example.org", "test-token", NullLogger.Instance, CancellationToken.None);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
