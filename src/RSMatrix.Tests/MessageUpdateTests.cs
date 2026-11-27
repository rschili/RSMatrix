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
        => (await ProcessAsync(context, messageEvent)).OfType<NewMessage>().First().Message;

    /// <summary>The message of a change to a message the client holds.</summary>
    private static Message Changed(object update, MessageChange expected)
    {
        var changed = (MessageChanged)update;
        if (changed.Change != expected)
            throw new InvalidOperationException($"Expected {expected}, got {changed.Change}.");
        if (changed.Message == null || changed.Message.Id != changed.Reference.Id)
            throw new InvalidOperationException("Expected the change to carry the message.");
        return changed.Message;
    }

    /// <summary>The reference of a change to a message the client doesn't hold.</summary>
    private static MessageReference ChangedElsewhere(object update, MessageChange expected)
    {
        var changed = (MessageChanged)update;
        if (changed.Change != expected)
            throw new InvalidOperationException($"Expected {expected}, got {changed.Change}.");
        if (changed.Message != null)
            throw new InvalidOperationException("Expected the change to come without the message.");
        return changed.Reference;
    }

    /// <summary>The message as the client has it now, from memory.</summary>
    private static async Task<Message> CurrentAsync(MatrixTestContext context, string id)
        => (await context.Room().GetMessageAsync(id))!;

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
        await Assert.That(message.Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));

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
    public async Task Edit_ComesOutWithTheEditedMessage()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, Text("$m", "helo"));

        var updates = await ProcessAsync(context, Edit("$e", "$m", "hello", ts: 1700000005000));
        var edited = Changed(updates.Single(), MessageChange.Edited);
        await Assert.That(edited.Id).IsEqualTo("$m");
        await Assert.That(edited.Body).IsEqualTo("hello");
        await Assert.That(edited.EditedAt).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1700000005000));
        // Messages don't change after they were built.
        await Assert.That(message.Body).IsEqualTo("helo");
    }

    [Test]
    public async Task ChangesToMessagesTheClientDoesntHold_ComeWithAReference()
    {
        using var context = new MatrixTestContext();
        var updates = await ProcessAsync(context, Edit("$e", "$old", "hello"), Reaction("$r", "$old", "👍"), Redaction("$x", "$r"),
            Text("$t", "reply", relatesTo: ""","m.relates_to":{"rel_type":"m.thread","event_id":"$root"}"""));
        await Assert.That(ChangedElsewhere(updates[0], MessageChange.Edited).Id).IsEqualTo("$old");
        await Assert.That(ChangedElsewhere(updates[1], MessageChange.Reactions).Id).IsEqualTo("$old");
        // The reaction is in memory now, so its deletion can be traced to the message.
        await Assert.That(ChangedElsewhere(updates[2], MessageChange.Reactions).Id).IsEqualTo("$old");
        await Assert.That(((NewMessage)updates[3]).Message.Id).IsEqualTo("$t");
        await Assert.That(ChangedElsewhere(updates[4], MessageChange.Thread).Id).IsEqualTo("$root");
        await Assert.That(updates.Count).IsEqualTo(5);
    }

    [Test]
    public async Task DeletingAnEventTheClientNeverSaw_IsDropped()
    {
        using var context = new MatrixTestContext();
        await Assert.That(await ProcessAsync(context, Redaction("$x", "$unknown"))).IsEmpty();
    }

    [Test]
    public async Task ReactionsToReactions_AreNoChange()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "hello"));
        await ProcessAsync(context, Reaction("$r", "$m", "👍"));
        await Assert.That(await ProcessAsync(context, Reaction("$r2", "$r", "😂"), Edit("$e", "$r", "nope"))).IsEmpty();
    }

    [Test]
    public async Task Edit_FromAnotherSender_IsIgnored()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "original"));
        await Assert.That(await ProcessAsync(context, Edit("$e", "$m", "forged", sender: "@mallory:example.org"))).IsEmpty();
        await Assert.That((await CurrentAsync(context, "$m")).Body).IsEqualTo("original");
    }

    [Test]
    public async Task EncryptedMessage_IgnoresUnencryptedEdits()
    {
        using var context = new MatrixTestContext();
        var room = context.Room();
        UserId.TryParse("@alice:example.org", out var aliceId);
        var alice = context.Client.GetOrAddUser(context.Client.GetOrAddUser(aliceId!), room);
        var content = new MessageContent(MessageKind.Text, "secret", null, []);
        var message = new MessageEntry("$m", alice, DateTimeOffset.UnixEpoch, false, IsEncrypted: true, null, null, content,
            IsDeleted: false, BundledEdit: null, BundledThread: null);
        room.Cache.Append(message);

        // The server could add an unencrypted edit to an encrypted room.
        room.Cache.Append(new EditEntry("$forged", "$m", "@alice:example.org", IsEncrypted: false,
            new MessageEdit("$forged", DateTimeOffset.UnixEpoch.AddSeconds(1), content with { Body = "forged" })));
        await Assert.That(room.Cache.Build(message).Body).IsEqualTo("secret");

        room.Cache.Append(new EditEntry("$edit", "$m", "@alice:example.org", IsEncrypted: true,
            new MessageEdit("$edit", DateTimeOffset.UnixEpoch.AddSeconds(1), content with { Body = "edited" })));
        await Assert.That(room.Cache.Build(message).Body).IsEqualTo("edited");
    }

    [Test]
    public async Task Edits_NewestWins_ByTimestamp()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "original"));
        await ProcessAsync(context, Edit("$new", "$m", "newer", ts: 1700000009000));
        await Assert.That(await ProcessAsync(context, Edit("$old", "$m", "older", ts: 1700000002000))).IsEmpty();
        await Assert.That((await CurrentAsync(context, "$m")).Body).IsEqualTo("newer");
    }

    [Test]
    public async Task Edit_CanChangeTheKind()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "original"));
        var edited = Changed((await ProcessAsync(context, Edit("$e", "$m", "notice now", msgType: "m.notice"))).Single(), MessageChange.Edited);
        await Assert.That(edited.Kind).IsEqualTo(MessageKind.Notice);
    }

    [Test]
    public async Task RedactingTheShownEdit_FallsBackToThePreviousOne()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "original"));
        await ProcessAsync(context, Edit("$e1", "$m", "first", ts: 1700000001000), Edit("$e2", "$m", "second", ts: 1700000002000));
        await Assert.That((await CurrentAsync(context, "$m")).Body).IsEqualTo("second");

        var updates = await ProcessAsync(context, Redaction("$r2", "$e2"));
        await Assert.That(Changed(updates.Single(), MessageChange.Edited).Body).IsEqualTo("first");

        var last = Changed((await ProcessAsync(context, Redaction("$r1", "$e1"))).Single(), MessageChange.Edited);
        await Assert.That(last.Body).IsEqualTo("original");
        await Assert.That(last.EditedAt).IsNull();
    }

    [Test]
    public async Task Reactions_CountOncePerUserAndKey()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "hello"));

        var added = Changed((await ProcessAsync(context, Reaction("$r1", "$m", "👍"))).Single(), MessageChange.Reactions);
        var reaction = added.Reactions.Single();
        await Assert.That(reaction.Key).IsEqualTo("👍");
        await Assert.That(reaction.Users.Single().User.UserId.Full).IsEqualTo("@bob:example.org");

        await Assert.That(await ProcessAsync(context, Reaction("$r2", "$m", "👍"))).IsEmpty();
        await Assert.That((await CurrentAsync(context, "$m")).Reactions.Single().Users.Count).IsEqualTo(1);

        await Assert.That(await ProcessAsync(context, Redaction("$x1", "$r1", sender: "@bob:example.org"))).IsEmpty();
        await Assert.That((await CurrentAsync(context, "$m")).Reactions.Single().Key).IsEqualTo("👍");

        var removed = Changed((await ProcessAsync(context, Redaction("$x2", "$r2", sender: "@bob:example.org"))).Single(), MessageChange.Reactions);
        await Assert.That(removed.Reactions).IsEmpty();
    }

    [Test]
    public async Task DeletedMessage_StaysDeleted()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "oops"));

        var deleted = Changed((await ProcessAsync(context, Redaction("$x", "$m", sender: "@admin:example.org"))).Single(), MessageChange.Deleted);
        await Assert.That(deleted.IsDeleted).IsTrue();
        await Assert.That(deleted.Body).IsEqualTo("");

        await Assert.That(await ProcessAsync(context, Reaction("$r", "$m", "👍"), Edit("$e", "$m", "back"))).IsEmpty();
        await Assert.That((await CurrentAsync(context, "$m")).Body).IsEqualTo("");
    }

    [Test]
    public async Task Redaction_WithTopLevelRedacts_Works()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "oops"));
        var updates = await ProcessAsync(context, Event("$x", "m.room.redaction", "{}", extra: ""","redacts":"$m" """));
        await Assert.That(Changed(updates.Single(), MessageChange.Deleted).IsDeleted).IsTrue();
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
    public async Task ThreadReply_ChangesTheRoot()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$root", "root"));
        const string inThread = ""","m.relates_to":{"rel_type":"m.thread","event_id":"$root"}""";

        var updates = await ProcessAsync(context, Text("$t1", "reply", relatesTo: inThread));
        await Assert.That(((NewMessage)updates[0]).Message.Id).IsEqualTo("$t1");
        var root = Changed(updates[1], MessageChange.Thread);
        await Assert.That(root.Id).IsEqualTo("$root");
        await Assert.That(root.Thread!.ReplyCount).IsEqualTo(1);

        // Reactions to a reply don't change the thread.
        await Assert.That((await ProcessAsync(context, Reaction("$r", "$t1", "👍"))).Count).IsEqualTo(1);

        updates = await ProcessAsync(context, Redaction("$x", "$t1"));
        await Assert.That(Changed(updates[0], MessageChange.Deleted).Id).IsEqualTo("$t1");
        await Assert.That(Changed(updates[1], MessageChange.Thread).Thread?.ReplyCount ?? 0).IsEqualTo(0);
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
    public async Task MarkAsRead_OfOurOwnMessage_PointsAtTheEventBefore()
    {
        using var context = new MatrixTestContext();
        var updates = await ProcessAsync(context, Text("$theirs", "hi"), Text("$own", "mine", sender: "@bot:example.org"));
        var own = ((NewMessage)updates[1]).Message;

        await own.MarkAsReadAsync();

        var request = context.Requests.Single(r => r.Path.Contains("/read_markers"));
        await Assert.That(request.Body).IsEqualTo("""{"m.read":"$theirs","m.fully_read":"$theirs"}""");
    }

    [Test]
    public async Task RemoveReaction_RedactsOurReaction()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, Text("$m", "hello"));
        await ProcessAsync(context, Reaction("$mine", "$m", "👍", sender: "@bot:example.org"), Reaction("$theirs", "$m", "🎉"));
        var message = await CurrentAsync(context, "$m");

        await message.RemoveReactionAsync("🎉");
        await Assert.That(context.Requests).IsEmpty();

        await message.RemoveReactionAsync("👍");
        await Assert.That(context.Requests.Single().Path).Contains(RoomPath + "redact/%24mine/");
    }
}
