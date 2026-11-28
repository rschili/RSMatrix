using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

public class PresenceTests
{
    [Test]
    public async Task PresenceAndStatus_ReachOthersAndOurselves_AndAwayStaysAcrossSyncs()
    {
        await using var server = new LocalHomeserver();
        var aliceName = await server.RegisterUserAsync("alice");
        var bobName = await server.RegisterUserAsync("bob");
        var alice = await server.LoginClientAsync(aliceName);
        var bob = await server.LoginClientAsync(bobName);
        var created = await LocalHomeserver.RequestAsync(await server.LoginAsync(aliceName), "/_matrix/client/v3/createRoom",
            HttpMethod.Post, new { preset = "private_chat", invite = new[] { $"@{bobName}:localhost" } });
        var roomId = created.GetProperty("room_id").GetString()!;
        await AcceptInviteAsync(bob, roomId, server.CancellationToken);
        var room = (await ReceiveUpdateAsync<RoomChanged>(alice, c => c.Room.RoomId.Full == roomId && c.Room.JoinedMemberCount == 2,
            server.CancellationToken)).Room;

        await alice.SetStatusMessageAsync("lunch", server.CancellationToken);
        await ReceiveUpdateAsync<PresenceChanged>(alice, c => c.User == alice.CurrentUser && c.User.StatusMessage == "lunch",
            server.CancellationToken);
        var aliceAtBob = (await ReceiveUpdateAsync<PresenceChanged>(bob,
            c => c.User.UserId.Equals(alice.CurrentUser.UserId) && c.User.StatusMessage == "lunch", server.CancellationToken)).User;
        await Assert.That(aliceAtBob.Presence).IsEqualTo(Presence.Online);

        // The status text stays when only the presence changes.
        await alice.SetPresenceAsync(Presence.Unavailable, server.CancellationToken);
        await ReceiveUpdateAsync<PresenceChanged>(bob, c => c.User == aliceAtBob && c.User.Presence == Presence.Unavailable,
            server.CancellationToken);
        await Assert.That(aliceAtBob.StatusMessage).IsEqualTo("lunch");

        // Synapse puts Alice online when she sends a message, and her next sync makes her away again.
        var sent = await room.SendMessageAsync("still away");
        await ReceiveAsync(bob, sent, server.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), server.CancellationToken);
        await Assert.That(aliceAtBob.Presence).IsEqualTo(Presence.Unavailable);

        // A new device knows the status text from the start.
        var second = await server.LoginClientAsync(aliceName);
        await Assert.That(second.CurrentUser.StatusMessage).IsEqualTo("lunch");
    }
}
