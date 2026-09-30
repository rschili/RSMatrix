using RSMatrix.Models;

namespace RSMatrix.Tests;

public class RoomEncryptionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task KnownEncryptedRoom_RejectsAllMessageAndEditHelpers(bool timeline)
    {
        using var context = new MatrixTestContext();
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""", timeline);
        var room = context.Room();
        var sender = context.Client.GetOrAddUser(context.Client.CurrentUser);
        var message = new ReceivedTextMessage("received", room, context.Client.GetOrAddUser(sender, room),
            "$original", DateTimeOffset.UtcNow, "$thread", "m.text", context.Client);

        Func<Task>[] sends = [
            () => room.SendTextMessageAsync("secret", null, null),
            () => room.SendHtmlMessageAsync("secret", "<b>secret</b>", null, null),
            () => room.SendNoticeAsync("secret", null, null),
            () => room.SendHtmlNoticeAsync("secret", "<b>secret</b>", null, null),
            () => room.EditMessageAsync("$original", "secret"),
            () => room.EditHtmlMessageAsync("$original", "secret", "<b>secret</b>"),
            () => message.SendResponseAsync("secret"),
            () => message.SendHtmlResponseAsync("secret", "<b>secret</b>"),
            () => message.SendNoticeResponseAsync("secret"),
            () => message.SendHtmlNoticeResponseAsync("secret", "<b>secret</b>"),
            () => message.EditAsync("secret"),
            () => message.EditHtmlAsync("secret", "<b>secret</b>")
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
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Room().SendTextMessageAsync("secret", null, null));
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
            await Assert.ThrowsAsync<NotSupportedException>(() => room.SendTextMessageAsync("secret", null, null));
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
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Room().SendNoticeAsync("secret", null, null));
    }

    [Test]
    public async Task AnotherRoomWrapper_CannotBypassKnownEncryption()
    {
        using var context = new MatrixTestContext();
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""");
        var another = new Room(context.Room().RoomId, context.Client);
        await Assert.ThrowsAsync<NotSupportedException>(() => another.SendTextMessageAsync("secret", null, null));
        await Assert.That(context.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PlainRoomStillSends_AndEncryptedRoomAllowsUnencryptedControlEvents()
    {
        using var context = new MatrixTestContext();
        await Assert.That(await context.Room().SendTextMessageAsync("hello", null, null)).IsEqualTo("$sent");
        await SetEncryption(context, """{"algorithm":"m.megolm.v1.aes-sha2"}""");
        await context.Room().SendReactionAsync("$event", "👍");
        await context.Room().RedactEventAsync("$event");
        await context.Room().SendTypingNotificationAsync();
        await Assert.That(context.Requests.Count).IsEqualTo(4);
    }

    private static Task SetEncryption(MatrixTestContext context, string content, bool timeline = false)
        => context.Process("""
        {"next_batch":"next","rooms":{"join":{"!room:example.org":{"SECTION":{"events":[
          {"type":"m.room.encryption","state_key":"","sender":"@admin:example.org","event_id":"$encryption",
           "origin_server_ts":1700000000000,"content":CONTENT}]}}}}}
        """.Replace("SECTION", timeline ? "timeline" : "state").Replace("CONTENT", content));
}
