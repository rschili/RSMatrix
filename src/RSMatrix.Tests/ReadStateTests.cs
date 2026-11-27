using RSMatrix.Models;
using static RSMatrix.Tests.MessageUpdateTests;

namespace RSMatrix.Tests;

public class ReadStateTests
{
    private const string Own = "@bot:example.org";

    private static string Sync(string[] timeline, string? receipts = null, string? unread = null, bool limited = false)
    {
        var ephemeral = receipts == null ? "" : ""","ephemeral":{"events":[{"type":"m.receipt","content":""" + receipts + "}]}";
        var counts = unread == null ? "" : $$""","unread_notifications":{{unread}}""";
        var limitedJson = limited ? "true" : "false";
        return """{"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"limited":""" + limitedJson
            + ""","events":[""" + string.Join(",", timeline) + "]}" + ephemeral + counts + "}}}}";
    }

    private static string Receipt(string eventId, string user = Own, string type = "m.read", string threadId = "")
        => $$"""{"{{eventId}}":{"{{type}}":{"{{user}}":{"ts":1700000000000{{threadId}}""" + "}}}}";

    private static string Join(string id, string user = "@carol:example.org")
        => Event(id, "m.room.member", """{"membership":"join"}""", user, extra: $$""","state_key":"{{user}}" """);

    private static List<RoomChanged> RoomChanges(MatrixTestContext context)
        => context.ReadUpdates().Select(update => update.Value).OfType<RoomChanged>().ToList();

    [Test]
    public async Task Counts_ComeFromTheServer_AndChangesAreReported()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi")], unread: """{"notification_count":2,"highlight_count":1}"""));
        var room = context.Room();
        await Assert.That(room.NotificationCount).IsEqualTo(2);
        await Assert.That(room.MentionCount).IsEqualTo(1);
        await Assert.That(room.HasUnread).IsTrue();
        await Assert.That(RoomChanges(context)).IsEmpty();

        await context.Process(Sync([], receipts: Receipt("$a"), unread: """{"notification_count":0,"highlight_count":0}"""));

        await Assert.That(room.NotificationCount).IsEqualTo(0);
        await Assert.That(room.HasUnread).IsFalse();
        await Assert.That(RoomChanges(context).Single().Change).IsEqualTo(RoomChange.Unread);
    }

    [Test]
    public async Task HasUnread_FollowsOurReceipt_AlsoOnEventsThatArentMessages()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi"), Join("$join")], receipts: Receipt("$join")));
        var room = context.Room();
        await Assert.That(room.HasUnread).IsFalse();

        await context.Process(Sync([Text("$b", "anyone?")]));
        await Assert.That(room.HasUnread).IsTrue();

        // An older receipt, for example from another device, doesn't move ours back.
        await context.Process(Sync([], receipts: Receipt("$b")));
        await context.Process(Sync([], receipts: Receipt("$a", type: "m.read.private")));
        await Assert.That(room.HasUnread).IsFalse();
    }

    [Test]
    public async Task HasUnread_IgnoresOtherUsersReceipts_AndThreadReceipts()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi")], receipts: Receipt("$a", user: "@alice:example.org")));
        await context.Process(Sync([], receipts: Receipt("$a", threadId: ""","thread_id":"$root" """)));
        await Assert.That(context.Room().HasUnread).IsTrue();

        await context.Process(Sync([], receipts: Receipt("$a", threadId: ""","thread_id":"main" """)));
        await Assert.That(context.Room().HasUnread).IsFalse();
    }

    [Test]
    public async Task HasUnread_OurOwnMessageCountsAsRead()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi"), Text("$own", "hello", sender: Own)]));
        await Assert.That(context.Room().HasUnread).IsFalse();
    }

    [Test]
    public async Task HasUnread_AfterAGap_CountsTheNewMessages()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi")], receipts: Receipt("$a")));
        await Assert.That(context.Room().HasUnread).IsFalse();

        await context.Process(Sync([Text("$z", "much later")], limited: true));
        await Assert.That(context.Room().HasUnread).IsTrue();
    }

    [Test]
    public async Task MarkAsRead_PointsAtTheNewestEventFromSomeoneElse_AndClearsTheCounts()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi"), Reaction("$r", "$a", "👍"), Text("$own", "hello", sender: Own)],
            unread: """{"notification_count":1,"highlight_count":1}"""));
        context.ReadUpdates();
        var room = context.Room();

        await room.MarkAsReadAsync();

        var request = context.Requests.Single(r => r.Path.Contains("/read_markers"));
        await Assert.That(request.Body).IsEqualTo("""{"m.read":"$r","m.fully_read":"$r"}""");
        await Assert.That(room.NotificationCount).IsEqualTo(0);
        await Assert.That(room.MentionCount).IsEqualTo(0);
        await Assert.That(room.HasUnread).IsFalse();
        await Assert.That(RoomChanges(context).Single().Change).IsEqualTo(RoomChange.Unread);

        // Already marked, nothing to send.
        await room.MarkAsReadAsync();
        await Assert.That(context.Requests.Count(r => r.Path.Contains("/read_markers"))).IsEqualTo(1);
    }

    [Test]
    public async Task MarkAsRead_NeverMovesTheReceiptBack()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi"), Text("$b", "there")], receipts: Receipt("$b")));
        var older = (await context.Room().GetMessageAsync("$a"))!;

        await older.MarkAsReadAsync();

        await Assert.That(context.Requests.Where(r => r.Path.Contains("/read_markers"))).IsEmpty();
    }

    [Test]
    public async Task MarkAsRead_WhileARequestRuns_SendsOnlyTheNewestTargetNext()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "one")]));
        var firstRequest = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        context.Send = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/read_markers") && !firstRequest.Task.IsCompleted)
            {
                firstRequest.SetResult();
                await release.Task;
            }
            return MatrixTestContext.Json("{}");
        };
        var room = context.Room();

        var first = room.MarkAsReadAsync();
        await firstRequest.Task;
        await context.Process(Sync([Text("$b", "two")]));
        var second = room.MarkAsReadAsync();
        await context.Process(Sync([Text("$c", "three")]));
        var third = room.MarkAsReadAsync();
        release.SetResult();
        await Task.WhenAll(first, second, third);

        var targets = context.Requests.Where(r => r.Path.Contains("/read_markers")).Select(r => r.Body).ToList();
        await Assert.That(targets).IsEquivalentTo([
            """{"m.read":"$a","m.fully_read":"$a"}""",
            """{"m.read":"$c","m.fully_read":"$c"}"""]);
        await Assert.That(room.HasUnread).IsFalse();
    }

    [Test]
    public async Task MarkAsRead_WhenTheRequestFails_KeepsTheRoomUnread()
    {
        using var context = new MatrixTestContext();
        await context.Process(Sync([Text("$a", "hi")], unread: """{"notification_count":1,"highlight_count":0}"""));
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json("""{"errcode":"M_FORBIDDEN"}""", System.Net.HttpStatusCode.Forbidden));
        var room = context.Room();

        await Assert.ThrowsAsync<RSMatrix.Http.MatrixResponseException>(() => room.MarkAsReadAsync());

        await Assert.That(room.NotificationCount).IsEqualTo(1);
        await Assert.That(room.HasUnread).IsTrue();
    }
}
