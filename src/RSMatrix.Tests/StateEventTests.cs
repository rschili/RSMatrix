using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Tests;

public class StateEventTests
{
    [Test]
    public async Task CustomState_IsRetainedWithoutWarnings_AndTimelineReplacesState()
    {
        var (client, log) = CreateClient();
        var parent = Event("m.space.parent", "!parent:example.org", """{"via":["example.org"],"canonical":true}""");
        var feed = Event("uk.half-shot.matrix-hookshot.feed", "feed-one", """{"url":"https://example.org/feed"}""");
        await client.HandleSyncResponseForTestingAsync(Sync([parent, feed]));
        var room = RoomFor(client);
        var snapshot = room.StateEvents;

        await Assert.That(snapshot.Count).IsEqualTo(2);
        await Assert.That(snapshot[(parent.Type, parent.StateKey!)].Content.GetProperty("canonical").GetBoolean()).IsTrue();
        await Assert.That(snapshot[(feed.Type, feed.StateKey!)].Content.GetProperty("url").GetString()).IsEqualTo("https://example.org/feed");

        var removed = Event("m.space.parent", "!parent:example.org", "{}");
        await client.HandleSyncResponseForTestingAsync(Sync(timeline: [removed]));
        await Assert.That(room.StateEvents[(parent.Type, parent.StateKey!)].Content.EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(snapshot[(parent.Type, parent.StateKey!)].Content.GetProperty("canonical").GetBoolean()).IsTrue();
        await Assert.That(log.Levels.Any(l => l >= LogLevel.Warning)).IsFalse();
    }

    [Test]
    public async Task TimelineState_WinsOverStateBlock_InTheSameSync()
    {
        var (client, log) = CreateClient();
        await client.HandleSyncResponseForTestingAsync(Sync([
            Event("uk.half-shot.matrix-hookshot.feed", "feed", """{"version":1}""")
        ], [
            Event("uk.half-shot.matrix-hookshot.feed", "feed", """{"version":2}"""),
            Event("com.example.future.message", null, "{}")
        ]));
        var content = RoomFor(client).StateEvents[("uk.half-shot.matrix-hookshot.feed", "feed")].Content;
        await Assert.That(content.GetProperty("version").GetInt32()).IsEqualTo(2);
        await Assert.That(log.Levels.Any(l => l >= LogLevel.Warning)).IsFalse();
    }

    [Test]
    public async Task StateKeys_AreCaseSensitive_AndIncludeTheEventType()
    {
        var (client, _) = CreateClient();
        await client.HandleSyncResponseForTestingAsync(Sync([
            Event("com.example.custom", "Feed", "{}"),
            Event("com.example.custom", "feed", "{}"),
            Event("com.example.other", "feed", "{}"),
            Event("com.example.custom", "", "{}")
        ]));
        await Assert.That(RoomFor(client).StateEvents.Count).IsEqualTo(4);
    }

    [Test]
    public async Task StateContent_IsIndependentOfSourceDocumentAndEnvelope()
    {
        var (client, _) = CreateClient();
        using (var document = JsonDocument.Parse("""{"nested":{"value":42}}"""))
        {
            var ev = Event("com.example.custom", "", "{}");
            ev.Content = document.RootElement;
            await client.HandleSyncResponseForTestingAsync(Sync([ev]));
            ev.Content = null;
            ev.Type = "changed";
        }
        var retained = RoomFor(client).StateEvents[("com.example.custom", "")];
        await Assert.That(retained.Type).IsEqualTo("com.example.custom");
        await Assert.That(retained.Content.GetProperty("nested").GetProperty("value").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task StateKeyRatherThanType_DeterminesTimelineDispatch()
    {
        var (client, _) = CreateClient();
        await client.HandleSyncResponseForTestingAsync(Sync(timeline: [
            Event("m.room.message", "", """{"msgtype":"m.text","body":"not a message"}"""),
            Event("m.space.child", "!child:example.org", """{"via":["example.org"]}""")
        ]));
        await Assert.That(client.Messages.TryRead(out _)).IsFalse();
        await Assert.That(RoomFor(client).StateEvents.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Encryption_IsDetectedInStateBlock()
    {
        var (client, _) = CreateClient();
        await client.HandleSyncResponseForTestingAsync(Sync([
            Event("m.room.encryption", "", """{"algorithm":"m.megolm.v1.aes-sha2"}""")
        ]));
        await Assert.That(RoomFor(client).IsEncrypted).IsTrue();
    }

    [Test]
    public async Task MalformedStateAndRedactedMessages_DoNotDiscardLaterMessages()
    {
        var (client, log) = CreateClient();
        await client.HandleSyncResponseForTestingAsync(Sync([
            Event("m.room.name", "", """{"name":42}"""),
            Event("com.example.custom", "", "[]"),
            Event("com.example.custom", null, "{}"),
            Event("m.room.name", "", """{"name":"Valid name"}""")
        ], [
            Event("m.room.message", null, "{}"),
            Event("m.room.message", null, """{"msgtype":"m.text","body":"hello"}""")
        ]));
        await Assert.That(RoomFor(client).DisplayName).IsEqualTo("Valid name");
        await Assert.That(client.Messages.TryRead(out var message)).IsTrue();
        await Assert.That(message!.Body).IsEqualTo("hello");
        await Assert.That(log.Levels.Contains(LogLevel.Error)).IsFalse();
    }

    private static ClientEventWithoutRoomID Event(string type, string? key, string content) => new()
    {
        Type = type, StateKey = key, Content = JsonSerializer.Deserialize<JsonElement>(content),
        Sender = "@sender:example.org", EventId = "$event", OriginServerTs = 1700000000000
    };

    private static SyncResponse Sync(List<ClientEventWithoutRoomID>? state = null, List<ClientEventWithoutRoomID>? timeline = null) => new()
    {
        NextBatch = "next",
        Rooms = new RoomEvents
        {
            Joined = new Dictionary<string, JoinedRoomEvents>
            {
                ["!room:example.org"] = new()
                {
                    State = new() { Events = state }, Timeline = new() { Events = timeline }
                }
            }
        }
    };

    private static Room RoomFor(MatrixTextClient client)
    {
        RoomId.TryParse("!room:example.org", out var id);
        return client.GetOrAddRoom(id!);
    }

    private static (MatrixTextClient Client, RecordingLogger Log) CreateClient()
    {
        var logger = new RecordingLogger();
        UserId.TryParse("@bot:example.org", out var user);
        var parameters = new HttpClientParameters(new StubFactory(), "https://example.org", "token", logger, CancellationToken.None);
        return (MatrixTextClient.CreateForTesting(parameters, user!), logger);
    }

    // Receipts must never reach a real server during these tests.
    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
