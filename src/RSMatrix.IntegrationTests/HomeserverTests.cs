using System.Net;
using System.Net.Http.Json;
using RSMatrix.Http;
using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

public class HomeserverTests
{
    [Test]
    public async Task PasswordLogin_WhoAmI_Logout_AndInvalidPassword()
    {
        await using var server = new LocalHomeserver();
        var invalid = await Assert.ThrowsAsync<MatrixResponseException>(() =>
            MatrixHelper.PasswordLoginAsync(server.Parameters(), "@alice:localhost",
                "not-the-test-password", $"RSMATRIX-{Guid.NewGuid():N}", null));
        await Assert.That(invalid!.ErrorCode).IsEqualTo("M_FORBIDDEN");
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        var alice = await server.LoginAsync("alice");
        var who = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/account/whoami", HttpMethod.Get);
        await Assert.That(who.GetProperty("user_id").GetString()).IsEqualTo("@alice:localhost");

        await MatrixHelper.LogoutAsync(alice);
        var revoked = await Assert.ThrowsAsync<MatrixResponseException>(() =>
            LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/account/whoami", HttpMethod.Get));
        await Assert.That(revoked!.ErrorCode).IsEqualTo("M_UNKNOWN_TOKEN");
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Invite_Accept_Sync_TextReply_Notice_AndHistory()
    {
        await using var server = new LocalHomeserver();
        var alice = await server.LoginAsync("alice");
        var bob = await server.LoginClientAsync("bob");

        var marker = Guid.NewGuid().ToString("N");
        // Room creation/invites are fixture setup, not yet high-level library APIs.
        // Use Synapse's default room version to catch compatibility regressions.
        var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom",
            HttpMethod.Post, new
            {
                name = $"RSMatrix integration {marker}",
                preset = "private_chat",
                invite = new[] { "@bob:localhost" },
                initial_state = new[]
                {
                    new { type = "io.rsmatrix.test", state_key = "", content = new { marker } }
                }
            });
        var roomId = created.GetProperty("room_id").GetString()!;
        var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}";
        await AcceptInviteAsync(bob, roomId, server.CancellationToken);
        var body = $"ping {marker} — Grüße 👋";
        var seed = await LocalHomeserver.RequestAsync(alice,
            $"{roomPath}/send/m.room.message/{Guid.NewGuid():N}", HttpMethod.Put,
            new { msgtype = "m.text", body });
        var seedId = seed.GetProperty("event_id").GetString()!;

        // No fixed startup sleeps: await the exact event through the public channel.
        var received = await ReceiveAsync(bob, seedId, server.CancellationToken);
        await Assert.That(received.Body).IsEqualTo(body);
        await Assert.That(received.Sender.User.UserId.Full).IsEqualTo("@alice:localhost");
        await Assert.That(received.Room.RoomId.Full).IsEqualTo(roomId);
        await Assert.That(received.Room.IsEncrypted).IsFalse();
        await Assert.That(received.Room.StateEvents.ContainsKey(("io.rsmatrix.test", ""))).IsTrue();

        var replyBody = $"pong {marker}";
        var replyId = await received.ReplyAsync(replyBody);
        var reply = await LocalHomeserver.RequestAsync(alice,
            $"{roomPath}/event/{Uri.EscapeDataString(replyId)}", HttpMethod.Get);
        await Assert.That(reply.GetProperty("sender").GetString()).IsEqualTo("@bob:localhost");
        var replyContent = reply.GetProperty("content");
        await Assert.That(replyContent.GetProperty("body").GetString()).IsEqualTo(replyBody);
        await Assert.That(replyContent.GetProperty("msgtype").GetString()).IsEqualTo("m.text");
        await Assert.That(replyContent.GetProperty("m.relates_to").GetProperty("m.in_reply_to")
            .GetProperty("event_id").GetString()).IsEqualTo(seedId);

        var noticeId = await received.Room.SendMessageAsync($"notice {marker}", notice: true);
        var notice = await ReceiveAsync(bob, noticeId, server.CancellationToken);
        await Assert.That(notice.Kind).IsEqualTo(MessageKind.Notice);
        await Assert.That(notice.Body).IsEqualTo($"notice {marker}");
        var history = await received.Room.GetMessagesAsync(limit: 20);
        await Assert.That(history.Any(message => message.Id == replyId && message.Body == replyBody))
            .IsTrue();
        await Assert.That(history.Any(message => message.Id == noticeId)).IsTrue();
    }

}
