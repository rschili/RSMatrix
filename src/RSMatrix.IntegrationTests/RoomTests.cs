using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

public class RoomTests
{
    [Test]
    public async Task FirstSync_FillsRooms_ThenChangesComeAsUpdates()
    {
        await using var server = new LocalHomeserver();
        var aliceName = await server.RegisterUserAsync("alice");
        var bobName = await server.RegisterUserAsync("bob");
        var carolName = await server.RegisterUserAsync("carol");
        var alice = await server.LoginAsync(aliceName);
        var bobRaw = await server.LoginAsync(bobName);

        var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post,
            new { preset = "private_chat", invite = new[] { $"@{bobName}:localhost" } });
        var roomId = created.GetProperty("room_id").GetString()!;
        var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}";
        await LocalHomeserver.RequestAsync(bobRaw, $"{roomPath}/join", HttpMethod.Post, new { });

        var bob = await server.LoginClientAsync(bobName);
        while (bob.LastSyncAt == null)
            await Task.Delay(50, server.CancellationToken);

        var room = bob.Rooms.Single();
        await Assert.That(room.RoomId.Full).IsEqualTo(roomId);
        await Assert.That(room.Name).IsNull();
        await Assert.That(room.DisplayName).IsEqualTo(aliceName);
        await Assert.That(room.JoinedMemberCount).IsEqualTo(2);
        await Assert.That(bob.Updates.TryRead(out _)).IsFalse();

        async Task ChangeAsync(string type, object content, RoomChange expected)
        {
            await LocalHomeserver.RequestAsync(alice, $"{roomPath}/state/{type}/", HttpMethod.Put, content);
            await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Room == room && c.Change == expected, server.CancellationToken);
        }

        await ChangeAsync("m.room.name", new { name = "Lounge" }, RoomChange.Name);
        await Assert.That(room.DisplayName).IsEqualTo("Lounge");
        await ChangeAsync("m.room.topic", new { topic = "Chat about things" }, RoomChange.Topic);
        await Assert.That(room.Topic).IsEqualTo("Chat about things");
        await ChangeAsync("m.room.avatar", new { url = "mxc://localhost/avatar" }, RoomChange.Avatar);
        await Assert.That(room.AvatarUrl).IsEqualTo("mxc://localhost/avatar");

        await LocalHomeserver.RequestAsync(alice, $"{roomPath}/invite", HttpMethod.Post, new { user_id = $"@{carolName}:localhost" });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Room == room && c.Change == RoomChange.Members, server.CancellationToken);
        await Assert.That(room.InvitedMemberCount).IsEqualTo(1);

        var members = await room.GetMembersAsync(server.CancellationToken);
        await Assert.That(members.Select(m => (m.User.UserId.Localpart.ToString(), m.Membership))).IsEquivalentTo([
            (aliceName, (Membership?)Membership.Join), (bobName, Membership.Join), (carolName, Membership.Invite)]);

        await ChangeAsync("m.room.encryption", new { algorithm = "m.megolm.v1.aes-sha2" }, RoomChange.Encryption);
        await Assert.That(room.IsEncrypted).IsTrue();

        await LocalHomeserver.RequestAsync(alice, $"{roomPath}/kick", HttpMethod.Post, new { user_id = $"@{bobName}:localhost" });
        await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Room == room && c.Change == RoomChange.Members, server.CancellationToken);
        await Assert.That(room.IsJoined).IsFalse();
        await Assert.That(bob.Rooms).IsEmpty();
    }

    [Test]
    public async Task JoiningLater_ComesAsOneMembersChange()
    {
        await using var server = new LocalHomeserver();
        var aliceName = await server.RegisterUserAsync("alice");
        var bobName = await server.RegisterUserAsync("bob");
        var alice = await server.LoginAsync(aliceName);
        var bob = await server.LoginClientAsync(bobName);
        bob.AutoJoinOnInvite = true;
        while (bob.LastSyncAt == null)
            await Task.Delay(50, server.CancellationToken);

        var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post,
            new { preset = "private_chat", name = "Workshop", topic = "Tools", invite = new[] { $"@{bobName}:localhost" } });
        var roomId = created.GetProperty("room_id").GetString()!;

        var joined = await ReceiveUpdateAsync<RoomChanged>(bob, c => c.Room.RoomId.Full == roomId, server.CancellationToken);
        await Assert.That(joined.Change).IsEqualTo(RoomChange.Members);
        await Assert.That(joined.Room.IsJoined).IsTrue();
        await Assert.That(joined.Room.DisplayName).IsEqualTo("Workshop");
        await Assert.That(joined.Room.Topic).IsEqualTo("Tools");
        await Assert.That(bob.Rooms.Select(r => r.RoomId.Full)).Contains(roomId);
        await Assert.That(bob.Updates.TryRead(out var extra) && extra.Value is RoomChanged).IsFalse();
    }
}
