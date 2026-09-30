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
        context.Client.AutoJoinOnInvite = true;
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

        var messages = new List<Models.ReceivedTextMessage>();
        while (context.Client.Messages.TryRead(out var message)) messages.Add(message);
        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages[0].Sender.User.Presence).IsEqualTo(Models.Presence.Online);
        await Assert.That(context.Room("!invite:example.org").IsDirect).IsTrue();
        await Assert.That(context.Logs.Any(l => l.Level == LogLevel.Error)).IsFalse();
    }

    [Test]
    public async Task FailedOptionalRequests_DoNotDiscardMessages_OrStopLaterInvites()
    {
        using var context = new MatrixTestContext();
        context.Client.AutoJoinOnInvite = true;
        context.Send = (request, _) => Task.FromResult(
            Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Contains("!broken:") || request.RequestUri.AbsolutePath.EndsWith("/read_markers")
                ? MatrixTestContext.Json("""{"errcode":"M_FORBIDDEN"}""", HttpStatusCode.Forbidden)
                : MatrixTestContext.Json("""{"room_id":"!invite:example.org"}"""));
        await context.Process("""
        {"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[{{Message}}]}}},
        "invite":{"!broken:example.org":{},"!invite:example.org":{}}}}
        """.Replace("{{Message}}", Message));
        await Assert.That(context.Client.Messages.TryRead(out _)).IsTrue();
        await Assert.That(context.Requests.Count(r => r.Path.EndsWith("/join"))).IsEqualTo(2);
        await Assert.That(context.Requests.Any(r => r.Path.EndsWith("/read_markers"))).IsTrue();
        await Assert.That(context.Room().LastReceiptEventId).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CallerCancellationDuringSideEffects_Propagates(bool invite)
    {
        using var context = new MatrixTestContext();
        context.Client.AutoJoinOnInvite = invite;
        context.Send = (_, _) =>
        {
            context.Lifetime.Cancel();
            // Deliberately a plain OCE, not just TaskCanceledException.
            throw new OperationCanceledException(context.Lifetime.Token);
        };
        var json = invite
            ? """{"next_batch":"next","rooms":{"invite":{"!invite:example.org":{}}}}"""
            : """{"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[{{Message}}]}}}}} """.Replace("{{Message}}", Message);
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Process(json));
        await Assert.That(context.Requests.Count).IsEqualTo(1);
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
        await Assert.That(context.Client.Messages.TryRead(out _)).IsTrue();
        var syncs = context.Requests.Where(r => r.Path.Contains("/sync")).ToList();
        await Assert.That(syncs.Count).IsEqualTo(2);
        await Assert.That(syncs[1].Path.Contains("since=after_bad_presence")).IsTrue();
    }

    [Test]
    public async Task UnexpectedProcessingFailure_FaultsSyncInsteadOfAdvancing()
    {
        using var context = new MatrixTestContext(new ThrowOnAccountDataLogger());
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json("""
            {"next_batch":"must_not_advance","account_data":{"events":[{"type":"com.example.test","content":{}}]}}
            """));
        await context.Client.SyncAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await context.Client.Messages.Completion);
        await Assert.That(context.Requests.Count).IsEqualTo(1);
    }

    private sealed class ThrowOnAccountDataLogger : ILogger
    {
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, error).StartsWith("Received account data event:"))
                throw new InvalidOperationException("Simulated unexpected processing failure");
        }
    }
}
