using System.Text.Json;
using RSMatrix.Http;
using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

public class EncryptionTests
{
    [Test]
    public async Task EncryptedRoom_SendReceiveReplyEditReactionHistory_AndRestart()
    {
        await using var server = new LocalHomeserver();
        var bobUser = await server.RegisterUserAsync("bob");
        var carolUser = await server.RegisterUserAsync("carol");
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-e2ee-{Guid.NewGuid():N}");
        try
        {
            var alice = await server.LoginAsync("alice");
            var bob = await server.LoginClientAsync(bobUser, Path.Combine(storeRoot, "bob"));
            var carolStore = Path.Combine(storeRoot, "carol");
            var carol = await server.LoginClientAsync(carolUser, carolStore);
            bob.AutoJoinOnInvite = true;
            carol.AutoJoinOnInvite = true;

            // Alice only sets up the room over plain HTTP; she has no encryption.
            var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post, new
            {
                preset = "private_chat",
                invite = new[] { $"@{bobUser}:localhost", $"@{carolUser}:localhost" },
                initial_state = new[]
                {
                    new { type = "m.room.encryption", state_key = "", content = new { algorithm = "m.megolm.v1.aes-sha2" } }
                }
            });
            var roomId = created.GetProperty("room_id").GetString()!;
            var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}";
            await WaitForJoinedMembersAsync(alice, roomPath, 3, server.CancellationToken);

            // A plaintext ping hands each client its Room, with encryption state already synced.
            var ping = await LocalHomeserver.RequestAsync(alice, $"{roomPath}/send/m.room.message/{Guid.NewGuid():N}",
                HttpMethod.Put, new { msgtype = "m.text", body = "ping" });
            var pingId = ping.GetProperty("event_id").GetString()!;
            var bobPing = await ReceiveAsync(bob, pingId, server.CancellationToken);
            var bobRoom = bobPing.Room;
            await ReceiveAsync(carol, pingId, server.CancellationToken);
            await Assert.That(bob.EncryptionState).IsEqualTo(EncryptionState.Ready);
            await Assert.That(bobRoom.IsEncrypted).IsTrue();
            await Assert.That(bobPing.IsEncrypted).IsFalse();

            var secretId = await bobRoom.SendMessageAsync("secret body");
            var secret = await ReceiveAsync(carol, secretId, server.CancellationToken);
            await Assert.That(secret.Body).IsEqualTo("secret body");
            await Assert.That(secret.IsEncrypted).IsTrue();
            await Assert.That(secret.Sender.User.UserId.Full).IsEqualTo($"@{bobUser}:localhost");
            var rawSecret = await GetEventAsync(alice, roomPath, secretId);
            await Assert.That(rawSecret.GetProperty("type").GetString()).IsEqualTo("m.room.encrypted");
            await Assert.That(rawSecret.GetRawText().Contains("secret body")).IsFalse();

            var replyId = await secret.ReplyAsync("reply body");
            var reply = await ReceiveAsync(bob, replyId, server.CancellationToken);
            await Assert.That(reply.Body).IsEqualTo("reply body");
            var rawReply = await GetEventAsync(alice, roomPath, replyId);
            await Assert.That(rawReply.GetProperty("type").GetString()).IsEqualTo("m.room.encrypted");
            await Assert.That(rawReply.GetProperty("content").GetProperty("m.relates_to").GetProperty("m.in_reply_to")
                .GetProperty("event_id").GetString()).IsEqualTo(secretId);

            var reactionId = await reply.SendReactionAsync("👍");
            var rawReaction = await GetEventAsync(alice, roomPath, reactionId);
            await Assert.That(rawReaction.GetProperty("type").GetString()).IsEqualTo("m.room.encrypted");
            await Assert.That(rawReaction.GetProperty("content").GetProperty("m.relates_to").GetProperty("rel_type")
                .GetString()).IsEqualTo("m.annotation");
            var reaction = await ReceiveUpdateAsync<ReactionAdded>(carol, u => u.Message.Id == replyId, server.CancellationToken);
            await Assert.That(reaction.Key).IsEqualTo("👍");
            await Assert.That(reaction.User.User.UserId.Full).IsEqualTo($"@{bobUser}:localhost");

            var editId = await bobRoom.EditMessageAsync(secretId, "edited body");
            var edited = await ReceiveUpdateAsync<MessageEdited>(carol, u => u.Message.Id == secretId, server.CancellationToken);
            await Assert.That(edited.Message.Body).IsEqualTo("edited body");

            var history = await secret.Room.GetMessagesAsync(limit: 20);
            await Assert.That(string.Join(",", history.Select(m => m.Id))).IsEqualTo($"{pingId},{secretId},{replyId}");
            await Assert.That(history[1].Body).IsEqualTo("edited body");
            await Assert.That(history[0].IsEncrypted).IsFalse();
            var source = await secret.GetSourceAsync();
            await Assert.That(source).Contains("\"type\": \"m.room.message\"");
            await Assert.That(source).Contains("secret body");

            // Restart Carol with the same device and store: old sessions must still work both ways.
            await carol.DisposeAsync();
            var restarted = await server.ResumeClientAsync(carolUser, carolStore);
            restarted.AutoJoinOnInvite = true;

            var afterRestartId = await bobRoom.SendMessageAsync("after restart");
            var afterRestart = await ReceiveAsync(restarted, afterRestartId, server.CancellationToken);
            await Assert.That(afterRestart.Body).IsEqualTo("after restart");

            // The restarted cache starts after the restart, so the edit and the reaction come from history.
            var loaded = await afterRestart.Room.GetMessageAsync(secretId);
            await Assert.That(loaded!.Body).IsEqualTo("edited body");
            await Assert.That(loaded.IsEncrypted).IsTrue();
            var walked = await afterRestart.Room.GetMessagesAsync(limit: 20, before: afterRestart);
            await Assert.That(string.Join(",", walked.Select(m => m.Id))).IsEqualTo($"{pingId},{secretId},{replyId}");
            await Assert.That(walked.Any(m => m.Id == editId)).IsFalse();
            await Assert.That(walked[1].Body).IsEqualTo("edited body");
            await Assert.That(walked[2].Reactions.Single().Key).IsEqualTo("👍");
            var walkedById = await afterRestart.Room.GetMessagesAsync(limit: 1, beforeId: replyId);
            await Assert.That(walkedById.Single().Id).IsEqualTo(secretId);
            await Assert.That(walkedById.Single().HasAllReactions).IsTrue();

            var answerId = await afterRestart.ReplyAsync("answer after restart", quote: false);
            var answer = await ReceiveAsync(bob, answerId, server.CancellationToken);
            await Assert.That(answer.Body).IsEqualTo("answer after restart");
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    [Test]
    public async Task LoginAgain_ReplacesStoredSession_AndLogsOutOldDevice()
    {
        await using var server = new LocalHomeserver();
        var bobUser = await server.RegisterUserAsync("bob");
        var bobId = $"@{bobUser}:localhost";
        var store = Path.Combine(Path.GetTempPath(), $"rsmatrix-e2ee-{Guid.NewGuid():N}");
        try
        {
            var observer = await server.LoginAsync("alice");
            var first = await server.LoginClientAsync(bobUser, store);
            await first.DisposeAsync();
            var firstDevice = MatrixClient.GetStoredSession(store, bobId)!.DeviceId;

            await server.LoginClientAsync(bobUser, store);
            var secondDevice = MatrixClient.GetStoredSession(store, bobId)!.DeviceId;
            await Assert.That(secondDevice).IsNotEqualTo(firstDevice);
            var devices = (await LocalHomeserver.RequestAsync(observer, "/_matrix/client/v3/keys/query", HttpMethod.Post,
                new Dictionary<string, object> { ["device_keys"] = new Dictionary<string, string[]> { [bobId] = [] } }))
                .GetProperty("device_keys").GetProperty(bobId);
            await Assert.That(devices.TryGetProperty(firstDevice, out _)).IsFalse();
            await Assert.That(devices.TryGetProperty(secondDevice, out _)).IsTrue();
        }
        finally
        {
            Directory.Delete(store, true);
        }
    }

    [Test]
    public async Task RoomKey_RotatesAfterConfiguredMessageCount()
    {
        await using var server = new LocalHomeserver();
        var bobUser = await server.RegisterUserAsync("bob");
        var carolUser = await server.RegisterUserAsync("carol");
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-e2ee-{Guid.NewGuid():N}");
        try
        {
            var alice = await server.LoginAsync("alice");
            var bob = await server.LoginClientAsync(bobUser, Path.Combine(storeRoot, "bob"));
            var carol = await server.LoginClientAsync(carolUser, Path.Combine(storeRoot, "carol"));
            var (roomPath, bobRoom) = await CreateEncryptedRoomAsync(alice, bob, [carol],
                new { algorithm = "m.megolm.v1.aes-sha2", rotation_period_msgs = 2 }, server.CancellationToken);

            var sessionIds = new List<string>();
            foreach (var body in new[] { "one", "two", "three" })
            {
                var eventId = await bobRoom.SendMessageAsync(body);
                await Assert.That((await ReceiveAsync(carol, eventId, server.CancellationToken)).Body).IsEqualTo(body);
                sessionIds.Add(await GetSessionIdAsync(alice, roomPath, eventId));
            }
            await Assert.That(sessionIds[1]).IsEqualTo(sessionIds[0]);
            await Assert.That(sessionIds[2]).IsNotEqualTo(sessionIds[1]);
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    [Test]
    public async Task NewDevice_ReceivesCurrentKey_ReportsOlderHistory_AndLeaveRotatesKey()
    {
        await using var server = new LocalHomeserver();
        var bobUser = await server.RegisterUserAsync("bob");
        var carolUser = await server.RegisterUserAsync("carol");
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-e2ee-{Guid.NewGuid():N}");
        try
        {
            var alice = await server.LoginAsync("alice");
            var bob = await server.LoginClientAsync(bobUser, Path.Combine(storeRoot, "bob"));
            var carol = await server.LoginClientAsync(carolUser, Path.Combine(storeRoot, "carol"));
            var (roomPath, bobRoom) = await CreateEncryptedRoomAsync(alice, bob, [carol],
                new { algorithm = "m.megolm.v1.aes-sha2" }, server.CancellationToken);

            var beforeId = await bobRoom.SendMessageAsync("before the new device");
            await ReceiveAsync(carol, beforeId, server.CancellationToken);
            var firstSession = await GetSessionIdAsync(alice, roomPath, beforeId);

            // Carol adds a second device. A later plaintext ping shows that Bob has synced past its device list change.
            var carolPhone = await server.LoginClientAsync(carolUser, Path.Combine(storeRoot, "carol-phone"));
            var pingId = await SendPlaintextAsync(alice, roomPath, "ping");
            await ReceiveAsync(bob, pingId, server.CancellationToken);
            await ReceiveAsync(carolPhone, pingId, server.CancellationToken);

            var afterId = await bobRoom.SendMessageAsync("after the new device");
            var after = await ReceiveAsync(carolPhone, afterId, server.CancellationToken);
            await Assert.That(after.Body).IsEqualTo("after the new device");
            await Assert.That(after.IsEncrypted).IsTrue();
            await Assert.That(await GetSessionIdAsync(alice, roomPath, afterId)).IsEqualTo(firstSession);

            // The new device only got the key from its current index on, so older history stays unreadable.
            var history = await after.Room.GetMessagesAsync(limit: 20);
            await Assert.That(history.Any(m => m.Id == beforeId)).IsFalse();
            await Assert.That(ReadIssues(carolPhone).Any(i => i.Kind == ClientIssueKind.DecryptionFailed && i.EventId == beforeId)).IsTrue();

            // Once Bob has seen Carol leave, his next message uses a new key.
            await LocalHomeserver.RequestAsync(alice, $"{roomPath}/kick", HttpMethod.Post, new { user_id = $"@{carolUser}:localhost" });
            await WaitUntilAsync(() => bobRoom.Users.TryGetValue($"@{carolUser}:localhost", out var member) && member.Membership == Membership.Leave,
                server.CancellationToken);
            var afterLeaveId = await bobRoom.SendMessageAsync("after carol left");
            await Assert.That(await GetSessionIdAsync(alice, roomPath, afterLeaveId)).IsNotEqualTo(firstSession);
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    /// <summary>Alice creates the room over plain HTTP and invites everyone. Returns Bob's room once all clients have synced it.</summary>
    internal static async Task<(string RoomPath, Room BobRoom)> CreateEncryptedRoomAsync(HttpClientParameters alice,
        MatrixClient bob, MatrixClient[] others, object encryption, CancellationToken cancellationToken)
    {
        MatrixClient[] clients = [bob, .. others];
        foreach (var client in clients)
            client.AutoJoinOnInvite = true;
        var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post, new
        {
            preset = "private_chat",
            invite = clients.Select(c => c.CurrentUser.Full).Distinct().ToArray(),
            initial_state = new[] { new { type = "m.room.encryption", state_key = "", content = encryption } }
        });
        var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(created.GetProperty("room_id").GetString()!)}";
        await WaitForJoinedMembersAsync(alice, roomPath, clients.Select(c => c.CurrentUser.Full).Distinct().Count() + 1, cancellationToken);

        var pingId = await SendPlaintextAsync(alice, roomPath, "ping");
        Room? bobRoom = null;
        foreach (var client in clients)
        {
            var ping = await ReceiveAsync(client, pingId, cancellationToken);
            bobRoom ??= ping.Room;
        }
        return (roomPath, bobRoom!);
    }

    internal static async Task<string> SendPlaintextAsync(HttpClientParameters user, string roomPath, string body)
    {
        var sent = await LocalHomeserver.RequestAsync(user, $"{roomPath}/send/m.room.message/{Guid.NewGuid():N}",
            HttpMethod.Put, new { msgtype = "m.text", body });
        return sent.GetProperty("event_id").GetString()!;
    }

    private static async Task<string> GetSessionIdAsync(HttpClientParameters user, string roomPath, string eventId)
        => (await GetEventAsync(user, roomPath, eventId)).GetProperty("content").GetProperty("session_id").GetString()!;


    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
            await Task.Delay(100, cancellationToken);
    }

    internal static async Task WaitForJoinedMembersAsync(HttpClientParameters user, string roomPath, int count,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var members = await LocalHomeserver.RequestAsync(user, $"{roomPath}/joined_members", HttpMethod.Get);
            if (members.GetProperty("joined").EnumerateObject().Count() >= count)
                return;
            await Task.Delay(200, cancellationToken);
        }
    }

    private static Task<JsonElement> GetEventAsync(HttpClientParameters user, string roomPath, string eventId)
        => LocalHomeserver.RequestAsync(user, $"{roomPath}/event/{Uri.EscapeDataString(eventId)}", HttpMethod.Get);

}
