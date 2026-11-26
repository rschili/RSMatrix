using RSMatrix.Models;
using static RSMatrix.Tests.MessageUpdateTests;

namespace RSMatrix.Tests;

public class HistoryTests
{
    private const string Room = "!room:example.org";

    private static string SyncWithPrevBatch(string prevBatch, params string[] timeline)
        => $$"""{"next_batch":"next","rooms":{"join":{"{{Room}}":{"timeline":{"prev_batch":"{{prevBatch}}","events":[""" + string.Join(",", timeline) + "]}}}}}";

    /// <summary>A sync whose timeline the server cut short, so events before it were left out.</summary>
    private static string LimitedSync(string prevBatch, params string[] timeline)
        => SyncWithPrevBatch(prevBatch, timeline).Replace("\"timeline\":{", "\"timeline\":{\"limited\":true,");

    private static string[] Texts(int from, int count) => Enumerable.Range(from, count).Select(i => Text($"$m{i}", $"m{i}")).ToArray();

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
    }

    /// <summary>A /messages response, newest event first. Without <paramref name="end"/>, it reaches the start of the room.</summary>
    private static string Page(string? end, params string[] events)
        => $$"""{"start":"s",{{(end == null ? "" : $"\"end\":\"{end}\",")}}"chunk":[""" + string.Join(",", events) + "]}";

    /// <summary>Answers each request with the response of the first key its path contains.</summary>
    private static void Serve(MatrixTestContext context, params (string PathPart, string Response)[] responses)
        => context.Send = (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            var response = responses.FirstOrDefault(r => path.Contains(r.PathPart)).Response
                ?? throw new InvalidOperationException($"Unexpected request {path}");
            return Task.FromResult(MatrixTestContext.Json(response));
        };

    private static string Ids(IEnumerable<Message> messages) => string.Join(",", messages.Select(m => m.Id));

    [Test]
    public async Task EmptyCache_LoadsTheNewestPage_WithEditsReactionsAndTombstones()
    {
        using var context = new MatrixTestContext();
        Serve(context, ("/messages?dir=b&limit=6", Page(null,
            Text("$3", "three", ts: 1700000003000),
            Reaction("$r", "$2", "👍"),
            Edit("$e", "$2", "two!", ts: 1700000002500),
            Event("$2", "m.room.message", """{"msgtype":"m.text","body":"two"}""", ts: 1700000002000,
                extra: $$$""","unsigned":{"m.relations":{"m.replace":{{{Edit("$e", "$2", "two!", ts: 1700000002500)}}}}}"""),
            Event("$gone", "m.room.message", "{}", ts: 1700000001500, extra: ""","unsigned":{"redacted_because":{"type":"m.room.redaction"}}"""),
            Text("$1", "one", ts: 1700000001000))));

        var history = await context.Room().GetMessagesAsync(limit: 6);

        await Assert.That(Ids(history)).IsEqualTo("$1,$gone,$2,$3");
        await Assert.That(history[1].IsDeleted).IsTrue();
        await Assert.That(history[2].Body).IsEqualTo("two!");
        await Assert.That(history[2].EditedAt).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1700000002500));
        await Assert.That(history[2].Reactions.Single().Key).IsEqualTo("👍");

        // The page joined the room's cache, so live changes to its messages come out.
        await context.Process(Sync(Reaction("$r2", "$3", "🎉")));
        var added = (ReactionAdded)context.ReadUpdates().Single().Value!;
        await Assert.That(added.Message.Id).IsEqualTo("$3");
        await Assert.That(history.All(m => m.HasAllReactions)).IsTrue();
    }

    [Test]
    public async Task Walk_UsesTheCacheFirst_ThenContinuesFromPrevBatch()
    {
        using var context = new MatrixTestContext();
        Serve(context,
            ("from=p0", Page("e1", Text("$3", "three"), Text("$2", "two"))),
            ("from=e1", Page(null, Text("$1", "one"))));
        await context.Process(SyncWithPrevBatch("p0", Text("$4", "four"), Text("$5", "five")));
        var room = context.Room();

        var newest = await room.GetMessagesAsync(limit: 2);
        await Assert.That(Ids(newest)).IsEqualTo("$4,$5");
        await Assert.That(context.Requests).IsEmpty();

        var older = await room.GetMessagesAsync(limit: 2, before: newest[0]);
        await Assert.That(Ids(older)).IsEqualTo("$2,$3");
        var oldest = await room.GetMessagesAsync(limit: 10, before: older[0]);
        await Assert.That(Ids(oldest)).IsEqualTo("$1");
        await Assert.That(context.Requests.Count).IsEqualTo(2);

        // The start was reached and everything is in the cache now.
        await Assert.That(await room.GetMessagesAsync(limit: 10, before: oldest[0])).IsEmpty();
        var all = await room.GetMessagesAsync(limit: 10);
        await Assert.That(Ids(all)).IsEqualTo("$1,$2,$3,$4,$5");
        await Assert.That(context.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ShortPages_KeepRequesting_UntilTheLimitIsReached()
    {
        using var context = new MatrixTestContext();
        Serve(context,
            ("from=e1", Page("e2", Text("$1", "one"))),
            ("/messages", Page("e1", Reaction("$r", "$2", "👍"), Text("$2", "two"))));

        var history = await context.Room().GetMessagesAsync(limit: 2);

        await Assert.That(Ids(history)).IsEqualTo("$1,$2");
        await Assert.That(context.Requests[0].Path).Contains("limit=2");
        await Assert.That(context.Requests[1].Path).Contains("from=e1&limit=1");
    }

    [Test]
    public async Task LiveReaction_ToAMessageNotLoaded_IsThereWhenItLoads_UnlessRedacted()
    {
        using var context = new MatrixTestContext();
        await context.Process(SyncWithPrevBatch("p0", 
            Reaction("$r1", "$old", "👍"),
            Reaction("$r2", "$old", "🎉"),
            Redaction("$x", "$r2", sender: "@bob:example.org")));
        await Assert.That(context.ReadUpdates()).IsEmpty();
        Serve(context, ("from=p0", Page(null, Text("$old", "old"))));

        var old = (await context.Room().GetMessagesAsync(limit: 1)).Single();

        await Assert.That(old.Reactions.Single().Key).IsEqualTo("👍");
    }

    [Test]
    public async Task UnknownId_FindsItsPositionOnce_AndResultsStayOutOfTheCache()
    {
        using var context = new MatrixTestContext();
        Serve(context,
            ("/context/%24far?limit=0", """{"start":"c0"}"""),
            ("from=c0", Page("e1", Text("$b", "b"), Text("$a", "a"))),
            ("from=e1", Page(null)));
        var room = context.Room();

        var history = await room.GetMessagesAsync(limit: 2, beforeId: "$far");
        await Assert.That(Ids(history)).IsEqualTo("$a,$b");
        await Assert.That(history.Any(m => m.HasAllReactions)).IsFalse();
        await Assert.That(room.Cache.MessageCount).IsEqualTo(0);

        await Assert.That(await room.GetMessagesAsync(limit: 2, before: history[0])).IsEmpty();
        await Assert.That(context.Requests.Count(r => r.Path.Contains("/context/"))).IsEqualTo(1);
    }

    [Test]
    public async Task GetMessage_UsesTheCache_ThenTheServer()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(Text("$reply", "re", relatesTo: ""","m.relates_to":{"m.in_reply_to":{"event_id":"$target"}}""")));
        var reply = context.ReadMessages().Single();
        var room = context.Room();
        await Assert.That((await room.GetMessageAsync("$reply"))!.HasAllReactions).IsTrue();
        await Assert.That(context.Requests).IsEmpty();

        Serve(context,
            ("/event/%24target", Text("$target", "target")),
            ("/event/%24reaction", Reaction("$reaction", "$target", "👍")));
        var target = await reply.GetReplyToAsync();
        await Assert.That(target!.Body).IsEqualTo("target");
        await Assert.That(target.HasAllReactions).IsFalse();
        await Assert.That(room.Cache.Find("$target")).IsNull();
        await Assert.That(await room.GetMessageAsync("$reaction")).IsNull();

        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json("""{"errcode":"M_NOT_FOUND","error":"Event not found."}""",
            System.Net.HttpStatusCode.NotFound));
        await Assert.That(await room.GetMessageAsync("$missing")).IsNull();
    }

    /// <summary>A /relations response, newest event first. Without <paramref name="nextBatch"/>, it is the last page.</summary>
    private static string Relations(string? nextBatch, params string[] events)
        => $$"""{{{(nextBatch == null ? "" : $"\"next_batch\":\"{nextBatch}\",")}}"chunk":[""" + string.Join(",", events) + "]}";

    private static string ThreadReply(string id, string body, long ts)
        => Text(id, body, ts: ts, relatesTo: ""","m.relates_to":{"rel_type":"m.thread","event_id":"$root","is_falling_back":true}""");

    [Test]
    public async Task ThreadMessages_ComeFromTheCache_WhenItHoldsTheRoot()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(Text("$root", "root"), ThreadReply("$t1", "one", 1), Text("$other", "other"), ThreadReply("$t2", "two", 2)));
        var room = context.Room();

        var newest = await room.GetThreadMessagesAsync("$root", limit: 1);
        await Assert.That(Ids(newest)).IsEqualTo("$t2");
        var older = await room.GetThreadMessagesAsync("$root", limit: 5, before: newest[0]);
        await Assert.That(Ids(older)).IsEqualTo("$t1");
        await Assert.That(context.Requests).IsEmpty();
    }

    [Test]
    public async Task ThreadMessages_PageThroughRelations_WhenTheRootIsNotLoaded()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(ThreadReply("$t3", "three", 3)));
        var inCache = context.ReadMessages().Single();
        Serve(context,
            ("from=n1", Relations(null, ThreadReply("$t1", "one", 1))),
            ("from=n0", Relations("n1", ThreadReply("$t2", "two", 2))),
            ("m.thread?dir=b&limit=1", Relations("n0", ThreadReply("$t3", "three", 3))));
        var room = context.Room();

        // Starting at a reply without a token skips the newer replies first.
        var page = await room.GetThreadMessagesAsync("$root", limit: 1, before: inCache);
        await Assert.That(Ids(page)).IsEqualTo("$t2");
        var rest = await room.GetThreadMessagesAsync("$root", limit: 5, before: page[0]);
        await Assert.That(Ids(rest)).IsEqualTo("$t1");
        await Assert.That(context.Requests.Count).IsEqualTo(3);
        await Assert.That(context.Requests[0].Path).Contains("/_matrix/client/v1/rooms/%21room%3Aexample.org/relations/%24root/m.thread?dir=b&limit=1");
    }

    [Test]
    public async Task ThreadSummary_FollowsRepliesInTheCache()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(Text("$root", "root"), ThreadReply("$t1", "one", 1)));
        var root = (await context.Room().GetMessageAsync("$root"))!;
        await Assert.That(root.Thread!.ReplyCount).IsEqualTo(1);
        await Assert.That(root.Thread.CurrentUserParticipated).IsFalse();

        await context.Process(Sync(Text("$t2", "mine", sender: "@bot:example.org",
            relatesTo: ""","m.relates_to":{"rel_type":"m.thread","event_id":"$root","is_falling_back":true}""")));
        root = (await context.Room().GetMessageAsync("$root"))!;
        await Assert.That(root.Thread!.ReplyCount).IsEqualTo(2);
        await Assert.That(root.Thread.LatestReply!.Id).IsEqualTo("$t2");
        await Assert.That(root.Thread.CurrentUserParticipated).IsTrue();
    }

    [Test]
    public async Task ThreadSummary_ComesBundled_ForMessagesFromTheServer()
    {
        using var context = new MatrixTestContext();
        var latest = ThreadReply("$t9", "latest", 9);
        Serve(context, ("/event/%24root", Event("$root", "m.room.message", """{"msgtype":"m.text","body":"root"}""",
            extra: $$$$""","unsigned":{"m.relations":{"m.thread":{"latest_event":{{{{latest}}}},"count":9,"current_user_participated":true}}}""")));

        var root = await context.Room().GetMessageAsync("$root");

        await Assert.That(root!.Thread!.ReplyCount).IsEqualTo(9);
        await Assert.That(root.Thread.LatestReply!.Body).IsEqualTo("latest");
        await Assert.That(root.Thread.CurrentUserParticipated).IsTrue();
    }

    [Test]
    public async Task GetSource_ReturnsTheEventAsTheServerSentIt_Indented()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(Text("$m", "grüezi")));
        var message = context.ReadMessages().Single();
        Serve(context, ("/event/%24m", Text("$m", "grüezi")));

        var source = await message.GetSourceAsync();

        await Assert.That(source).Contains("\"body\": \"grüezi\"");
        await Assert.That(source).Contains("\n");
    }
    [Test]
    public async Task LimitedSync_StartsTheCacheOver_AndTheWalkCrossesTheGap()
    {
        using var context = new MatrixTestContext();
        await context.Process(LimitedSync("p0", Text("$1", "one"), Text("$2", "two"), Reaction("$r", "$1", "👍")));
        await context.Process(LimitedSync("p1", Text("$5", "five")));
        var updates = context.ReadUpdates().Select(u => u.Value).ToList();
        await Assert.That(updates.OfType<ClientIssue>()).IsEmpty();
        var room = context.Room();
        await Assert.That(room.Cache.MessageCount).IsEqualTo(1);

        Serve(context, ("from=p1", Page(null, Reaction("$r", "$1", "👍"), Text("$4", "four"), Text("$3", "three"), Text("$2", "two"), Text("$1", "one"))));
        var history = await room.GetMessagesAsync(limit: 10);

        await Assert.That(Ids(history)).IsEqualTo("$1,$2,$3,$4,$5");
        await Assert.That(history[0].Reactions.Single().Key).IsEqualTo("👍");
        await Assert.That(context.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cut_KeepsNewerReactions_ForMessagesLoadedAgain()
    {
        using var context = new MatrixTestContext();
        var timeline = Texts(0, TimelineCache.MaxMessages + 1).ToList();
        // The reaction is newer than the messages the walk loads again, so the walk doesn't see it.
        timeline.Insert(150, Reaction("$r", "$m100", "👍"));
        await context.Process(SyncWithPrevBatch("p0", [.. timeline]));
        var room = context.Room();
        await Assert.That(room.Cache.MessageCount).IsEqualTo(TimelineCache.MessagesAfterCut);
        await Assert.That(room.Cache.Find("$m100")).IsNull();

        Serve(context,
            ("/context/%24m101?limit=0", """{"start":"c0"}"""),
            ("from=c0", Page("e1", Text("$m100", "m100"))));
        var history = await room.GetMessagesAsync(limit: TimelineCache.MessagesAfterCut + 1);

        await Assert.That(history[0].Id).IsEqualTo("$m100");
        await Assert.That(history[0].Reactions.Single().Key).IsEqualTo("👍");
        await Assert.That(history[0].HasAllReactions).IsTrue();
    }

    [Test]
    public async Task Cache_IsNotCut_WhileTheRoomIsBeingRead()
    {
        using var context = new MatrixTestContext();
        var time = new ManualTime();
        var room = context.Room();
        room.Cache.Time = time;
        await context.Process(SyncWithPrevBatch("p0", Texts(0, 150)));
        await room.GetMessagesAsync(limit: 10);

        await context.Process(Sync(Texts(150, 100)));
        await Assert.That(room.Cache.MessageCount).IsEqualTo(250);

        // The ceiling applies even while the room is being read.
        await context.Process(Sync(Texts(250, TimelineCache.CeilingMessages - 249)));
        await Assert.That(room.Cache.MessageCount).IsEqualTo(TimelineCache.MessagesAfterCeilingCut);

        time.Now += TimelineCache.ReadTimeout;
        await context.Process(Sync(Texts(TimelineCache.CeilingMessages + 1, 1)));
        await Assert.That(room.Cache.MessageCount).IsEqualTo(TimelineCache.MessagesAfterCut);
    }

    [Test]
    public async Task GetReactions_AsksTheServer_ForMessagesWithoutAllReactions()
    {
        using var context = new MatrixTestContext();
        Serve(context,
            ("/event/%24m", Text("$m", "old")),
            ("/relations/%24m/m.annotation", Relations(null,
                Reaction("$r3", "$m", "🎉", sender: "@carol:example.org"),
                Event("$r2", "m.reaction", "{}", extra: ""","unsigned":{"redacted_because":{"type":"m.room.redaction"}}"""),
                Reaction("$r1", "$m", "👍"))));
        var message = (await context.Room().GetMessageAsync("$m"))!;
        await Assert.That(message.HasAllReactions).IsFalse();

        var reactions = await message.GetReactionsAsync();

        await Assert.That(string.Join(",", reactions.Select(r => r.Key))).IsEqualTo("👍,🎉");
        await Assert.That(context.Requests.Count).IsEqualTo(2);
    }
    [Test]
    public async Task Walk_DoesNotJoinTheCache_WhenTheSyncSkippedEventsMeanwhile()
    {
        using var context = new MatrixTestContext();
        var room = context.Room();
        context.Send = async (_, _) =>
        {
            // The sync skips events while the page is on its way. Its timeline has no message, so the cache stays empty.
            await context.Process(LimitedSync("p9"));
            return MatrixTestContext.Json(Page(null, Text("$2", "two"), Text("$1", "one")));
        };

        var history = await room.GetMessagesAsync(limit: 3);

        await Assert.That(Ids(history)).IsEqualTo("$1,$2");
        await Assert.That(history[0].HasAllReactions).IsFalse();
        await Assert.That(room.Cache.IsEmpty).IsTrue();
        await Assert.That(room.Cache.OlderToken).IsEqualTo("p9");
    }
}
