using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Tests;

internal sealed class MatrixTestContext : IDisposable
{
    public CancellationTokenSource Lifetime { get; } = new();
    public MatrixTextClient Client { get; }
    public List<(string Path, string Body)> Requests { get; } = [];
    public List<(LogLevel Level, string Message)> Logs { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Send { get; set; }
    private readonly Handler _handler;

    public MatrixTestContext(ILogger? logger = null)
    {
        _handler = new Handler(async (request, token) =>
        {
            Requests.Add((request.RequestUri!.PathAndQuery,
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(token)));
            return Send != null ? await Send(request, token) : Json("""{"event_id":"$sent","room_id":"!invite:example.org"}""");
        });
        UserId.TryParse("@bot:example.org", out var user);
        Client = MatrixTextClient.CreateForTesting(new HttpClientParameters(
            new Factory(_handler), "https://example.org", "token", logger ?? new RecordingLogger(Logs), Lifetime.Token), user!);
    }

    public Room Room(string id = "!room:example.org")
    {
        if (!RoomId.TryParse(id, out var parsed)) throw new ArgumentException("Invalid test room ID", nameof(id));
        return Client.GetOrAddRoom(parsed!);
    }

    public Task Process(string json) => Client.HandleSyncResponseForTestingAsync(JsonSerializer.Deserialize<SyncResponse>(json)!);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public void Dispose()
    {
        Lifetime.Dispose();
        _handler.Dispose();
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class RecordingLogger(List<(LogLevel, string)> entries) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add((logLevel, formatter(state, exception)));
    }
}
