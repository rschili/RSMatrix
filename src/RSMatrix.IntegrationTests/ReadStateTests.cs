using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

public class ReadStateTests
{
    [Test]
    public async Task Counts_FollowTheReadReceipt()
    {
        await using var server = new LocalHomeserver();
        var aliceName = await server.RegisterUserAsync("alice");
        var bobName = await server.RegisterUserAsync("bob");
        var alice = await server.LoginAsync(aliceName);
        var bobRaw = await server.LoginAsync(bobName);
        var bobId = $"@{bobName}:localhost";

        var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post,
            new { preset = "private_chat", invite = new[] { bobId } });
        var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(created.GetProperty("room_id").GetString()!)}";
        await LocalHomeserver.RequestAsync(bobRaw, $"{roomPath}/join", HttpMethod.Post, new { });
        async Task<string> SendAsync(object content) => (await LocalHomeserver.RequestAsync(alice,
            $"{roomPath}/send/m.room.message/{Guid.NewGuid():N}", HttpMethod.Put, content)).GetProperty("event_id").GetString()!;
        await SendAsync(new { msgtype = "m.text", body = "hello" });

        var bob = await server.LoginClientAsync(bobName);
        var room = bob.Rooms.Single();
        await Assert.That(room.NotificationCount).IsEqualTo(1);
        await Assert.That(room.MentionCount).IsEqualTo(0);
        await Assert.That(room.HasUnread).IsTrue();

        await SendAsync(new { msgtype = "m.text", body = "look at this" });
        await SendAsync(new Dictionary<string, object>
        {
            ["msgtype"] = "m.text",
            ["body"] = "bob, look at this",
            ["m.mentions"] = new { user_ids = new[] { bobId } },
        });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Change == RoomChange.Unread && room.MentionCount == 1, server.CancellationToken);
        await Assert.That(room.NotificationCount).IsEqualTo(3);

        await room.MarkAsReadAsync(server.CancellationToken);
        await Assert.That(room.NotificationCount).IsEqualTo(0);
        await Assert.That(room.HasUnread).IsFalse();

        // The server counts from the new receipt.
        var oneMore = await SendAsync(new { msgtype = "m.text", body = "one more" });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Change == RoomChange.Unread && room.NotificationCount > 0, server.CancellationToken);
        await Assert.That(room.NotificationCount).IsEqualTo(1);
        await Assert.That(room.MentionCount).IsEqualTo(0);
        await Assert.That(room.HasUnread).IsTrue();

        // Bob reads on another device.
        await LocalHomeserver.RequestAsync(bobRaw, $"{roomPath}/receipt/m.read/{Uri.EscapeDataString(oneMore)}", HttpMethod.Post, new { });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Change == RoomChange.Unread && !room.HasUnread, server.CancellationToken);
        await Assert.That(room.NotificationCount).IsEqualTo(0);

        // A private receipt from another device counts as well.
        var privately = await SendAsync(new { msgtype = "m.text", body = "read me quietly" });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Change == RoomChange.Unread && room.HasUnread, server.CancellationToken);
        await LocalHomeserver.RequestAsync(bobRaw, $"{roomPath}/receipt/m.read.private/{Uri.EscapeDataString(privately)}", HttpMethod.Post, new { });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Change == RoomChange.Unread && !room.HasUnread, server.CancellationToken);
        await Assert.That(room.NotificationCount).IsEqualTo(0);
    }

    [Test]
    public async Task Typing_ComesThrough_UnlessLeftOut()
    {
        await using var server = new LocalHomeserver();
        var aliceName = await server.RegisterUserAsync("alice");
        var bobName = await server.RegisterUserAsync("bob");
        var botName = await server.RegisterUserAsync("carol");
        var alice = await server.LoginAsync(aliceName);
        var aliceId = $"@{aliceName}:localhost";

        var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post,
            new { preset = "private_chat", invite = new[] { $"@{bobName}:localhost", $"@{botName}:localhost" } });
        var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(created.GetProperty("room_id").GetString()!)}";
        foreach (var name in new[] { bobName, botName })
            await LocalHomeserver.RequestAsync(await server.LoginAsync(name), $"{roomPath}/join", HttpMethod.Post, new { });

        var bob = await server.LoginClientAsync(bobName);
        var aliceClient = await server.LoginClientAsync(aliceName);
        var bot = await server.LoginClientAsync(botName, null, deviceId: $"RSMATRIX-{Guid.NewGuid():N}",
            optionalUpdates: OptionalUpdates.None);

        await LocalHomeserver.RequestAsync(alice, $"{roomPath}/typing/{Uri.EscapeDataString(aliceId)}", HttpMethod.Put,
            new { typing = true, timeout = 30000 });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Change == RoomChange.Typing && c.Room.TypingUsers.Any(), server.CancellationToken);
        await Assert.That(bob.Rooms.Single().TypingUsers.Single().User.UserId.Full).IsEqualTo(aliceId);

        // Both get the message while Alice still types; only Bob knows about the typing.
        var sent = await LocalHomeserver.RequestAsync(alice, $"{roomPath}/send/m.room.message/{Guid.NewGuid():N}", HttpMethod.Put,
            new { msgtype = "m.text", body = "hi" });
        var sentId = sent.GetProperty("event_id").GetString()!;
        await ReceiveAsync(bob, sentId, server.CancellationToken);
        await ReceiveAsync(bot, sentId, server.CancellationToken);
        await Assert.That(bob.Rooms.Single().TypingUsers.Count).IsEqualTo(1);
        await Assert.That(bot.Rooms.Single().TypingUsers).IsEmpty();

        // Bob types and stops, Alice's client sees both.
        var bobRoom = bob.Rooms.Single();
        await bobRoom.SetTypingAsync(true, server.CancellationToken);
        await ReceiveUpdateAsync<RoomChanged>(aliceClient,
            c => c.Change == RoomChange.Typing && c.Room.TypingUsers.Any(u => u.User.UserId.Full == $"@{bobName}:localhost"), server.CancellationToken);
        await bobRoom.SetTypingAsync(false, server.CancellationToken);
        await ReceiveUpdateAsync<RoomChanged>(aliceClient,
            c => c.Change == RoomChange.Typing && c.Room.TypingUsers.All(u => u.User.UserId.Full != $"@{bobName}:localhost"), server.CancellationToken);
    }
}
