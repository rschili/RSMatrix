using RSMatrix.Models;

namespace RSMatrix.Tests;

public class RoomEncryptionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task KnownEncryptedRoom_WithoutEncryption_RejectsAllContentSends(bool timeline)
    {
        using var context = new MatrixTestContext();
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""", timeline);
        var room = context.Room();
        var sender = context.Client.GetOrAddUser(context.Client.CurrentUser);
        var message = new Message("$original", room, context.Client.GetOrAddUser(sender, room),
            DateTimeOffset.UtcNow, true, false, null, "$thread",
            new MessageContent(MessageKind.Text, "received", null, []));

        Func<Task>[] sends = [
            () => room.SendMessageAsync("secret"),
            () => room.SendMessageAsync("secret", "<b>secret</b>", notice: true, threadRootId: "$thread"),
            () => room.EditMessageAsync("$original", "secret", "<b>secret</b>"),
            () => message.ReplyAsync("secret"),
            () => message.ReplyAsync("secret", "<b>secret</b>", notice: true, quote: false),
            () => message.EditAsync("secret", "<b>secret</b>"),
            () => room.SendReactionAsync("$original", "🤫"),
            () => message.SendReactionAsync("🤫")
        ];
        foreach (var send in sends)
        {
            var error = await Assert.ThrowsAsync<NotSupportedException>(send);
            await Assert.That(error!.Message.Contains(room.RoomId.Full)).IsTrue();
        }
        await Assert.That(context.Requests.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("{\"algorithm\":\"com.example.unknown\"}")]
    [Arguments("{}")]
    [Arguments("{\"algorithm\":null}")]
    [Arguments("{\"algorithm\":42}")]
    [Arguments("[]")]
    [Arguments("null")]
    public async Task UninterpretableEncryptionState_StillPreventsPlaintext(string content)
    {
        using var context = new MatrixTestContext();
        await SetEncryption(context, content);
        await Assert.That(context.Room().IsEncrypted).IsTrue();
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Room().SendMessageAsync("secret"));
        await Assert.That(context.Requests.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InviteEncryption_IsRecordedBeforeJoining(bool autoJoin)
    {
        foreach (var content in new[] { "{\"algorithm\":\"m.megolm.v1.aes-sha2\"}", "{\"algorithm\":\"unknown\"}", "{}", "[]" })
        {
            using var context = new MatrixTestContext();
            context.Client.AutoJoinOnInvite = autoJoin;
            context.Send = async (_, _) =>
            {
                await Assert.That(context.Room("!invite:example.org").IsEncrypted).IsTrue();
                return MatrixTestContext.Json("""{"room_id":"!invite:example.org"}""");
            };
            await context.Process("""
                {"next_batch":"next","rooms":{"invite":{"!invite:example.org":{"invite_state":{"events":[
                {"type":"m.room.encryption","state_key":"","sender":"@admin:example.org","content":CONTENT}]}}}}}
                """.Replace("CONTENT", content));
            var room = context.Room("!invite:example.org");
            await Assert.That(room.IsEncrypted).IsTrue();
            await Assert.ThrowsAsync<NotSupportedException>(() => room.SendMessageAsync("secret"));
            await Assert.That(context.Requests.Count).IsEqualTo(autoJoin ? 1 : 0);
        }
    }

    [Test]
    public async Task ClearingEncryptionState_DoesNotReenablePlaintext()
    {
        using var context = new MatrixTestContext();
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""");
        await SetEncryption(context, "{}", timeline: true);
        await Assert.That(context.Room().IsEncrypted).IsTrue();
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Room().SendMessageAsync("secret", notice: true));
    }

    [Test]
    public async Task AnotherRoomWrapper_CannotBypassKnownEncryption()
    {
        using var context = new MatrixTestContext();
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""");
        var another = new Room(context.Room().RoomId, context.Client);
        await Assert.ThrowsAsync<NotSupportedException>(() => another.SendMessageAsync("secret"));
        await Assert.That(context.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PlainRoomStillSends_AndEncryptedRoomAllowsUnencryptedControlEvents()
    {
        using var context = new MatrixTestContext();
        await Assert.That(await context.Room().SendMessageAsync("hello")).IsEqualTo("$sent");
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""");
        await context.Room().RedactEventAsync("$event");
        await context.Room().SendTypingNotificationAsync();
        await Assert.That(context.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task WithoutEncryption_PlaintextIsMarkedUnencrypted_AndEncryptedEventsAreSkipped()
    {
        using var context = new MatrixTestContext();
        await Assert.That(context.Client.EncryptionState).IsEqualTo(EncryptionState.Off);
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""");
        await context.Process("""
        {"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[
         {"type":"m.room.encrypted","sender":"@a:example.org","event_id":"$encrypted","origin_server_ts":1700000000000,
          "content":{"algorithm":"m.megolm.v1.aes-sha2","session_id":"s","ciphertext":"c"}},
         {"type":"m.room.message","sender":"@a:example.org","event_id":"$plain","origin_server_ts":1700000000000,
          "content":{"msgtype":"m.text","body":"hello"}}]}}}}}
        """);
        var updates = context.ReadUpdates();
        await Assert.That(updates.Count).IsEqualTo(2);
        await Assert.That(updates[0].Value is ClientIssue { Kind: ClientIssueKind.DecryptionFailed, EventId: "$encrypted" }).IsTrue();
        var message = ((NewMessage)updates[1].Value!).Message;
        await Assert.That(message.Id).IsEqualTo("$plain");
        await Assert.That(message.IsEncrypted).IsFalse();
        await Assert.That(message.Room.IsEncrypted).IsTrue();
    }

    private static Task SetEncryption(MatrixTestContext context, string content, bool timeline = false)
        => context.Process("""
        {"next_batch":"next","rooms":{"join":{"!room:example.org":{"SECTION":{"events":[
          {"type":"m.room.encryption","state_key":"","sender":"@admin:example.org","event_id":"$encryption",
           "origin_server_ts":1700000000000,"content":CONTENT}]}}}}}
        """.Replace("SECTION", timeline ? "timeline" : "state").Replace("CONTENT", content));
}
