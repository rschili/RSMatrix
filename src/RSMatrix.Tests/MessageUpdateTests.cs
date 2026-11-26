using RSMatrix.Models;

namespace RSMatrix.Tests;

public class MessageUpdateTests
{
    private const string Room = "!room:example.org";
    private const string RoomPath = "/rooms/%21room%3Aexample.org/";

    internal static string Sync(params string[] timeline)
        => $$"""{"next_batch":"next","rooms":{"join":{"{{Room}}":{"timeline":{"events":[""" + string.Join(",", timeline) + "]}}}}}";

    internal static string Event(string id, string type, string content, string sender = "@alice:example.org", long ts = 1700000000000, string extra = "")
        => $$"""{"type":"{{type}}","sender":"{{sender}}","event_id":"{{id}}","origin_server_ts":{{ts}},"content":{{content}}{{extra}}}""";

    internal static string Text(string id, string body, string sender = "@alice:example.org", long ts = 1700000000000, string relatesTo = "")
        => Event(id, "m.room.message", $$"""{"msgtype":"m.text","body":"{{body}}"{{relatesTo}}}""", sender, ts);

    internal static string Edit(string id, string target, string body, string sender = "@alice:example.org", long ts = 1700000001000, string msgType = "m.text")
        => Event(id, "m.room.message", $$$"""
            {"msgtype":"{{{msgType}}}","body":"* {{{body}}}","m.new_content":{"msgtype":"{{{msgType}}}","body":"{{{body}}}"},
             "m.relates_to":{"rel_type":"m.replace","event_id":"{{{target}}}"}}
            """, sender, ts);

    internal static string Reaction(string id, string target, string key, string sender = "@bob:example.org")
        => Event(id, "m.reaction", $$$"""{"m.relates_to":{"rel_type":"m.annotation","event_id":"{{{target}}}","key":"{{{key}}}"}}""", sender);

    internal static string Redaction(string id, string target, string sender = "@alice:example.org")
        => Event(id, "m.room.redaction", $$"""{"redacts":"{{target}}"}""", sender);

    private static async Task<List<object>> ProcessAsync(MatrixTestContext context, params string[] timeline)
    {
        await context.Process(Sync(timeline));
        return context.ReadUpdates().Select(u => u.Value!).ToList();
    }

    private static async Task<Message> ReceiveAsync(MatrixTestContext context, string messageEvent)
        => ((NewMessage)(await ProcessAsync(context, messageEvent)).Single()).Message;

    [Test]
    public async Task TextMessage_ComesOutAsNewMessage()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "hello"));
        await Assert.That(message.Kind).IsEqualTo(MessageKind.Text);
        await Assert.That(message.Body).IsEqualTo("hello");
        await Assert.That(message.Sender.User.UserId.Full).IsEqualTo("@alice:example.org");
        await Assert.That(message.IsOwn).IsFalse();
        await Assert.That(message.EditedAt).IsNull();

        var own = await ReceiveAsync(context, Text("$own", "mine", sender: "@bot:example.org"));
        await Assert.That(own.IsOwn).IsTrue();
    }

    [Test]
    [Arguments("m.image", MessageKind.Image)]
    [Arguments("m.emote", MessageKind.Emote)]
    [Arguments("org.example.custom", MessageKind.Unknown)]
    public async Task MessageKind_FollowsMsgType(string msgType, MessageKind kind)
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Event("$m", "m.room.message", $$"""{"msgtype":"{{msgType}}","body":"fallback"}"""));
        await Assert.That(message.Kind).IsEqualTo(kind);
        await Assert.That(message.Body).IsEqualTo("fallback");
    }

    [Test]
    [Arguments("org.matrix.custom.html", "<b>hi</b>")]
    [Arguments("org.example.other", null)]
    public async Task Html_OnlyForMatrixHtml(string format, string? html)
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Event("$m", "m.room.message",
            $$"""{"msgtype":"m.text","body":"hi","format":"{{format}}","formatted_body":"<b>hi</b>"}"""));
        await Assert.That(message.Html).IsEqualTo(html);
    }

    [Test]
    public async Task Replies_AndThreads()
    {
        using var context = new MatrixTestContext();
        var reply = await ReceiveAsync(context, Text("$reply", "re", relatesTo: ""","m.relates_to":{"m.in_reply_to":{"event_id":"$target"}}"""));
        await Assert.That(reply.ReplyToId).IsEqualTo("$target");
        await Assert.That(reply.ThreadRootId).IsNull();

        var inThread = await ReceiveAsync(context, Text("$t1", "in thread", relatesTo: """
            ,"m.relates_to":{"rel_type":"m.thread","event_id":"$root","is_falling_back":true,"m.in_reply_to":{"event_id":"$previous"}}
            """));
        await Assert.That(inThread.ThreadRootId).IsEqualTo("$root");
        await Assert.That(inThread.ReplyToId).IsNull();

        var replyInThread = await ReceiveAsync(context, Text("$t2", "reply in thread", relatesTo: """
            ,"m.relates_to":{"rel_type":"m.thread","event_id":"$root","is_falling_back":false,"m.in_reply_to":{"event_id":"$t1"}}
            """));
        await Assert.That(replyInThread.ThreadRootId).IsEqualTo("$root");
        await Assert.That(replyInThread.ReplyToId).IsEqualTo("$t1");
    }

    [Test]
    public async Task Edit_UpdatesTheSameMessage()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "helo"));

        var updates = await ProcessAsync(context, Edit("$e", "$m", "hello", ts: 1700000005000));
        await Assert.That(updates.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(((MessageEdited)updates[0]).Message, message)).IsTrue();
        await Assert.That(message.Body).IsEqualTo("hello");
        await Assert.That(message.EditedAt).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1700000005000));
    }

    [Test]
    public async Task Edit_OfUnknownMessage_IsDropped()
    {
        using var context = new MatrixTestContext();
        await Assert.That(await ProcessAsync(context, Edit("$e", "$unknown", "hello"))).IsEmpty();
    }

    [Test]
    public async Task Edit_FromAnotherSender_IsIgnored()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "original"));
        await Assert.That(await ProcessAsync(context, Edit("$e", "$m", "forged", sender: "@mallory:example.org"))).IsEmpty();
        await Assert.That(message.Body).IsEqualTo("original");
    }

    [Test]
    public async Task Edits_NewestWins_ByTimestamp()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "original"));
        await ProcessAsync(context, Edit("$new", "$m", "newer", ts: 1700000009000));
        await Assert.That(await ProcessAsync(context, Edit("$old", "$m", "older", ts: 1700000002000))).IsEmpty();
        await Assert.That(message.Body).IsEqualTo("newer");
    }

    [Test]
    public async Task Edit_CanChangeTheKind()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "original"));
        await ProcessAsync(context, Edit("$e", "$m", "notice now", msgType: "m.notice"));
        await Assert.That(message.Kind).IsEqualTo(MessageKind.Notice);
    }

    [Test]
    public async Task RedactingTheShownEdit_FallsBackToThePreviousOne()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "original"));
        await ProcessAsync(context, Edit("$e1", "$m", "first", ts: 1700000001000), Edit("$e2", "$m", "second", ts: 1700000002000));
        await Assert.That(message.Body).IsEqualTo("second");

        var updates = await ProcessAsync(context, Redaction("$r2", "$e2"));
        await Assert.That(updates.Single() is MessageEdited).IsTrue();
        await Assert.That(message.Body).IsEqualTo("first");

        await ProcessAsync(context, Redaction("$r1", "$e1"));
        await Assert.That(message.Body).IsEqualTo("original");
        await Assert.That(message.EditedAt).IsNull();
    }

    [Test]
    public async Task Reactions_CountOncePerUserAndKey()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "hello"));

        var added = (ReactionAdded)(await ProcessAsync(context, Reaction("$r1", "$m", "👍"))).Single();
        await Assert.That(added.Key).IsEqualTo("👍");
        await Assert.That(added.User.User.UserId.Full).IsEqualTo("@bob:example.org");

        await Assert.That(await ProcessAsync(context, Reaction("$r2", "$m", "👍"))).IsEmpty();
        await Assert.That(message.Reactions.Single().Users.Count).IsEqualTo(1);

        await Assert.That(await ProcessAsync(context, Redaction("$x1", "$r1", sender: "@bob:example.org"))).IsEmpty();
        await Assert.That(message.Reactions.Single().Key).IsEqualTo("👍");

        var removed = (ReactionRemoved)(await ProcessAsync(context, Redaction("$x2", "$r2", sender: "@bob:example.org"))).Single();
        await Assert.That(removed.Key).IsEqualTo("👍");
        await Assert.That(message.Reactions).IsEmpty();
    }

    [Test]
    public async Task DeletedMessage_StaysDeleted()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "oops"));

        var deleted = (MessageDeleted)(await ProcessAsync(context, Redaction("$x", "$m", sender: "@admin:example.org"))).Single();
        await Assert.That(deleted.DeletedBy.User.UserId.Full).IsEqualTo("@admin:example.org");
        await Assert.That(message.IsDeleted).IsTrue();
        await Assert.That(message.Body).IsEqualTo("");

        await Assert.That(await ProcessAsync(context, Reaction("$r", "$m", "👍"), Edit("$e", "$m", "back"))).IsEmpty();
        await Assert.That(message.Body).IsEqualTo("");
    }

    [Test]
    public async Task Redaction_WithTopLevelRedacts_Works()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "oops"));
        await ProcessAsync(context, Event("$x", "m.room.redaction", "{}", extra: ""","redacts":"$m" """));
        await Assert.That(message.IsDeleted).IsTrue();
    }

    [Test]
    public async Task MessageDeletedBeforeWeSawIt_IsSkipped()
    {
        using var context = new MatrixTestContext();
        var updates = await ProcessAsync(context, Event("$m", "m.room.message", "{}",
            extra: ""","unsigned":{"redacted_because":{"type":"m.room.redaction"}}"""));
        await Assert.That(updates).IsEmpty();
    }

    [Test]
    public async Task SameEventTwice_ComesOutOnce()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "hello"));
        await Assert.That(await ProcessAsync(context, Text("$m", "hello"))).IsEmpty();
    }

    [Test]
    public async Task MarkAsRead_SendsOneRequest_AndSkipsOlderMessages()
    {
        using var context = new MatrixTestContext();
        var updates = await ProcessAsync(context, Text("$old", "first", ts: 1700000000000), Text("$new", "second", ts: 1700000001000));
        await Assert.That(context.Requests.Where(r => r.Path.Contains("/read_markers"))).IsEmpty();
        var older = ((NewMessage)updates[0]).Message;
        var newer = ((NewMessage)updates[1]).Message;

        await newer.MarkAsReadAsync();
        await older.MarkAsReadAsync();

        var request = context.Requests.Single(r => r.Path.Contains("/read_markers"));
        await Assert.That(request.Path).EndsWith(RoomPath + "read_markers");
        await Assert.That(request.Body).Contains("\"m.fully_read\":\"$new\"");
        await Assert.That(request.Body).Contains("\"m.read\":\"$new\"");
    }

    [Test]
    public async Task RemoveReaction_RedactsOurReaction()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "hello"));
        await ProcessAsync(context, Reaction("$mine", "$m", "👍", sender: "@bot:example.org"), Reaction("$theirs", "$m", "🎉"));

        await message.RemoveReactionAsync("🎉");
        await Assert.That(context.Requests).IsEmpty();

        await message.RemoveReactionAsync("👍");
        await Assert.That(context.Requests.Single().Path).Contains(RoomPath + "redact/%24mine/");
    }
}
