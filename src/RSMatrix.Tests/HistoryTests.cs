using RSMatrix.Models;
using static RSMatrix.Tests.MessageUpdateTests;

namespace RSMatrix.Tests;

public class HistoryTests
{
    private const string Room = "!room:example.org";

    private static string SyncWithPrevBatch(string prevBatch, params string[] timeline)
        => $$"""{"next_batch":"next","rooms":{"join":{"{{Room}}":{"timeline":{"prev_batch":"{{prevBatch}}","events":[""" + string.Join(",", timeline) + "]}}}}}";

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
    public async Task EmptyWindow_LoadsTheNewestPage_WithEditsReactionsAndTombstones()
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
        await Assert.That(history[2].Reactions.Single().Key).IsEqualTo("👍");

        // The page joined the window, so live changes reach the same objects.
        await context.Process(Sync(Reaction("$r2", "$3", "🎉")));
        var added = (ReactionAdded)context.ReadUpdates().Single().Value!;
        await Assert.That(ReferenceEquals(added.Message, history[3])).IsTrue();
    }

    [Test]
    public async Task Walk_UsesTheWindowFirst_ThenContinuesFromPrevBatch()
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

        // The start was reached and everything is in the window now.
        await Assert.That(await room.GetMessagesAsync(limit: 10, before: oldest[0])).IsEmpty();
        var all = await room.GetMessagesAsync(limit: 10);
        await Assert.That(Ids(all)).IsEqualTo("$1,$2,$3,$4,$5");
        await Assert.That(ReferenceEquals(all[1], older[0])).IsTrue();
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
    public async Task LiveReaction_ToAMessageNotLoaded_IsAppliedWhenItLoads_UnlessRedacted()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(
            Reaction("$r1", "$old", "👍"),
            Reaction("$r2", "$old", "🎉"),
            Redaction("$x", "$r2", sender: "@bob:example.org")));
        await Assert.That(context.ReadUpdates()).IsEmpty();
        Serve(context, ("/messages", Page(null, Text("$old", "old"))));

        var old = (await context.Room().GetMessagesAsync(limit: 1)).Single();

        await Assert.That(old.Reactions.Single().Key).IsEqualTo("👍");
    }

    [Test]
    public async Task BundledEdit_IsApplied()
    {
        using var context = new MatrixTestContext();
        var edit = Edit("$e", "$m", "hello", ts: 1700000005000);
        Serve(context, ("/messages", Page(null,
            Event("$m", "m.room.message", """{"msgtype":"m.text","body":"helo"}""", extra: $$$""","unsigned":{"m.relations":{"m.replace":{{{edit}}}}}"""))));

        var message = (await context.Room().GetMessagesAsync(limit: 1)).Single();

        await Assert.That(message.Body).IsEqualTo("hello");
        await Assert.That(message.EditedAt).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1700000005000));
    }

    [Test]
    public async Task UnknownId_FindsItsPositionOnce_AndResultsStayOutOfTheWindow()
    {
        using var context = new MatrixTestContext();
        Serve(context,
            ("/context/%24far?limit=0", """{"start":"c0"}"""),
            ("from=c0", Page("e1", Text("$b", "b"), Text("$a", "a"))),
            ("from=e1", Page(null)));
        var room = context.Room();

        var history = await room.GetMessagesAsync(limit: 2, beforeId: "$far");
        await Assert.That(Ids(history)).IsEqualTo("$a,$b");
        await Assert.That(room.Window.Count).IsEqualTo(0);

        await Assert.That(await room.GetMessagesAsync(limit: 2, before: history[0])).IsEmpty();
        await Assert.That(context.Requests.Count(r => r.Path.Contains("/context/"))).IsEqualTo(1);
    }

    [Test]
    public async Task GetMessage_UsesTheWindow_ThenTheServer()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(Text("$reply", "re", relatesTo: ""","m.relates_to":{"m.in_reply_to":{"event_id":"$target"}}""")));
        var reply = context.ReadMessages().Single();
        var room = context.Room();
        await Assert.That(ReferenceEquals(await room.GetMessageAsync("$reply"), reply)).IsTrue();
        await Assert.That(context.Requests).IsEmpty();

        Serve(context,
            ("/event/%24target", Text("$target", "target")),
            ("/event/%24reaction", Reaction("$reaction", "$target", "👍")));
        var target = await reply.GetReplyToAsync();
        await Assert.That(target!.Body).IsEqualTo("target");
        await Assert.That(room.Window.Find("$target")).IsNull();
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
    public async Task ThreadMessages_ComeFromTheWindow_WhenItHoldsTheRoot()
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
        var inWindow = context.ReadMessages().Single();
        Serve(context,
            ("from=n1", Relations(null, ThreadReply("$t1", "one", 1))),
            ("from=n0", Relations("n1", ThreadReply("$t2", "two", 2))),
            ("m.thread?dir=b&limit=1", Relations("n0", ThreadReply("$t3", "three", 3))));
        var room = context.Room();

        // Starting at a reply without a token skips the newer replies first.
        var page = await room.GetThreadMessagesAsync("$root", limit: 1, before: inWindow);
        await Assert.That(Ids(page)).IsEqualTo("$t2");
        var rest = await room.GetThreadMessagesAsync("$root", limit: 5, before: page[0]);
        await Assert.That(Ids(rest)).IsEqualTo("$t1");
        await Assert.That(context.Requests.Count).IsEqualTo(3);
        await Assert.That(context.Requests[0].Path).Contains("/_matrix/client/v1/rooms/%21room%3Aexample.org/relations/%24root/m.thread?dir=b&limit=1");
    }

    [Test]
    public async Task ThreadSummary_FollowsRepliesInTheWindow()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync(Text("$root", "root"), ThreadReply("$t1", "one", 1)));
        var root = context.ReadMessages()[0];
        await Assert.That(root.Thread!.ReplyCount).IsEqualTo(1);
        await Assert.That(root.Thread.CurrentUserParticipated).IsFalse();

        await context.Process(Sync(Text("$t2", "mine", sender: "@bot:example.org",
            relatesTo: ""","m.relates_to":{"rel_type":"m.thread","event_id":"$root","is_falling_back":true}""")));
        await Assert.That(root.Thread.ReplyCount).IsEqualTo(2);
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
}
