using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RSMatrix.Http;

namespace RSMatrix.Tests;

public class SyncResilienceTests
{
    private const string Message = """
        {"type":"m.room.message","sender":"@alice:example.org","event_id":"$message",
         "origin_server_ts":1700000000000,"content":{"msgtype":"m.text","body":"hello"}}
        """;

    [Test]
    public async Task MalformedEvents_DoNotDiscardOtherEventsOrRooms()
    {
        using var context = new MatrixTestContext();
        await context.Process("""
        {"next_batch":"next","account_data":{"events":[null]},"presence":{"events":[null,
          {"type":"m.presence","sender":"@alice:example.org","content":{"presence":"invalid"}},
          {"type":"m.presence","sender":"@alice:example.org","content":[]},
          {"type":"m.presence","sender":"@alice:example.org","content":{"presence":"online"}}]},
         "rooms":{"join":{"!bad:example.org":null,"!room:example.org":{
           "ephemeral":{"events":[null]},"state":{"events":[null]},"timeline":{"events":[null,{{Message}}]}},
           "!other:example.org":{"timeline":{"events":[{{Message}}]}}},
          "invite":{"!bad:example.org":null,"!broken:example.org":{"invite_state":{"events":[null,
             {"type":"m.room.member","state_key":"@bot:example.org","sender":"@alice:example.org","content":{"membership":"invite","is_direct":"wrong"}}]}},
           "!invite:example.org":{"invite_state":{"events":[
             {"type":"m.room.member","state_key":"@bot:example.org","sender":"@alice:example.org","content":{"membership":"invite","is_direct":true}}]}}}}}
        """.Replace("{{Message}}", Message));

        var updates = context.ReadUpdates();
        var messages = updates.Select(u => u.Value).OfType<Models.NewMessage>().Select(u => u.Message).ToList();
        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages[0].Sender.User.Presence).IsEqualTo(Models.Presence.Online);
        var invites = updates.Select(u => u.Value).OfType<Models.Request>().Select(r => (Models.RoomInvite)r.Value!).ToList();
        await Assert.That(string.Join(",", invites.Select(i => $"{i.RoomId.Full}:{i.IsDirect}"))).IsEqualTo("!broken:example.org:False,!invite:example.org:True");
        await Assert.That(context.Logs.Any(l => l.Level == LogLevel.Error)).IsFalse();
    }

    [Test]
    public async Task ClosedMessageChannel_IsNotSilentlyIgnored()
    {
        using var context = new MatrixTestContext();
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json(
            """{"errcode":"M_UNKNOWN_TOKEN"}""", HttpStatusCode.Unauthorized));
        await context.Client.SyncAsync(); // completes/faults the channel
        await Assert.ThrowsAsync<ChannelClosedException>(() => context.Process("""
        {"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[{{Message}}]}}}}}
        """.Replace("{{Message}}", Message)));
        await Assert.That(context.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MalformedPresence_IsSkipped_AndNextSyncUsesTheToken()
    {
        using var context = new MatrixTestContext();
        context.Send = (request, _) => Task.FromResult(!request.RequestUri!.AbsolutePath.EndsWith("/sync")
            ? MatrixTestContext.Json("{}")
            : context.Requests.Count(r => r.Path.Contains("/sync")) == 1
            ? MatrixTestContext.Json("""
              {"next_batch":"after_bad_presence","presence":{"events":[{"type":"m.presence","sender":"@alice:example.org","content":{"presence":"invalid"}}]},
               "rooms":{"join":{"!room:example.org":{"timeline":{"events":[{{Message}}]}}}}}
              """.Replace("{{Message}}", Message))
            : MatrixTestContext.Json("""{"errcode":"M_UNKNOWN_TOKEN"}""", HttpStatusCode.Unauthorized));
        await context.Client.SyncAsync();
        // The first sync has no sync position, so its message only goes to the room's history.
        await Assert.That(context.ReadMessages()).IsEmpty();
        await Assert.That((await context.Room().GetMessagesAsync(1)).Single().Id).IsEqualTo("$message");
        var syncs = context.Requests.Where(r => r.Path.Contains("/sync")).ToList();
        await Assert.That(syncs.Count).IsEqualTo(2);
        await Assert.That(syncs[1].Path.Contains("since=after_bad_presence")).IsTrue();
    }

    [Test]
    public async Task SyncWithoutPosition_KeepsMessagesInHistory_AndReportsOnlyLaterOnes()
    {
        using var context = new MatrixTestContext();
        const string encrypted = """
            {"type":"m.room.encrypted","sender":"@alice:example.org","event_id":"$encrypted","origin_server_ts":1700000000001,
             "content":{"algorithm":"m.megolm.v1.aes-sha2","ciphertext":"x","session_id":"s"}}
            """;
        const string reaction = """
            {"type":"m.reaction","sender":"@alice:example.org","event_id":"$reaction","origin_server_ts":1700000000002,
             "content":{"m.relates_to":{"rel_type":"m.annotation","event_id":"$message","key":"👍"}}}
            """;
        const string invite = """
            "invite":{"!invite:example.org":{"invite_state":{"events":[
              {"type":"m.room.member","state_key":"@bot:example.org","sender":"@alice:example.org","content":{"membership":"invite"}}]}}}
            """;
        var syncCount = 0;
        context.Send = (request, _) => Task.FromResult(!request.RequestUri!.AbsolutePath.EndsWith("/sync")
            ? MatrixTestContext.Json("{}")
            : ++syncCount switch
            {
                1 => MatrixTestContext.Json("""
                    {"next_batch":"first","rooms":{"join":{"!room:example.org":{"timeline":{"events":[{{Message}},{{Encrypted}},{{Reaction}}]}}},
                     {{Invite}}}}
                    """.Replace("{{Message}}", Message).Replace("{{Encrypted}}", encrypted).Replace("{{Reaction}}", reaction).Replace("{{Invite}}", invite)),
                2 => MatrixTestContext.Json("""
                    {"next_batch":"second","rooms":{"join":{"!room:example.org":{"timeline":{"events":[{{Message}}]}}}}}
                    """.Replace("{{Message}}", Message.Replace("$message", "$later"))),
                _ => MatrixTestContext.Json("""{"errcode":"M_UNKNOWN_TOKEN"}""", HttpStatusCode.Unauthorized),
            });
        await context.Client.SyncAsync();

        var updates = context.ReadUpdates().Select(u => u.Value).ToList();
        await Assert.That(updates.OfType<Models.NewMessage>().Select(m => m.Message.Id)).IsEquivalentTo(["$later"]);
        await Assert.That(updates.OfType<Models.MessageChanged>().Any()).IsFalse();
        await Assert.That(updates.OfType<Models.ClientIssue>().Any(i => i.Kind == Models.ClientIssueKind.DecryptionFailed)).IsFalse();
        await Assert.That(updates.OfType<Models.Request>().Count()).IsEqualTo(1);
        var history = await context.Room().GetMessagesAsync(2);
        await Assert.That(history.Select(m => m.Id)).IsEquivalentTo(["$message", "$later"]);
        await Assert.That(history[0].Reactions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LatestMessage_IsTheNewestMessageInTheCache()
    {
        using var context = new MatrixTestContext();
        await Assert.That(context.Room().LatestMessage).IsNull();
        const string reaction = """
            {"type":"m.reaction","sender":"@alice:example.org","event_id":"$reaction","origin_server_ts":1700000000002,
             "content":{"m.relates_to":{"rel_type":"m.annotation","event_id":"$later","key":"👍"}}}
            """;
        await context.Process("""
            {"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[{{Message}},{{Later}},{{Reaction}}]}}}}}
            """.Replace("{{Message}}", Message).Replace("{{Later}}", Message.Replace("$message", "$later")).Replace("{{Reaction}}", reaction));

        var latest = context.Room().LatestMessage;
        await Assert.That(latest!.Id).IsEqualTo("$later");
        await Assert.That(latest.Reactions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnexpectedProcessingFailure_FaultsSyncInsteadOfAdvancing()
    {
        using var context = new MatrixTestContext(new ThrowOnPresenceLogger());
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json("""
            {"next_batch":"must_not_advance","presence":{"events":[{"type":"m.presence","sender":"@alice:example.org"}]}}
            """));
        await context.Client.SyncAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await context.Client.Updates.Completion);
        await Assert.That(context.Requests.Count).IsEqualTo(1);
    }

    private sealed class ThrowOnPresenceLogger : ILogger
    {
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, error).StartsWith("Received presence event without object content"))
                throw new InvalidOperationException("Simulated unexpected processing failure");
        }
    }
}
