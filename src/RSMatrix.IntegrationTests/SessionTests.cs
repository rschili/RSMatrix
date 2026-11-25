using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.IntegrationTests;

public class SessionTests
{
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"rsmatrix-it-session-{Guid.NewGuid():N}");

    [Test]
    public async Task Restart_ResumesSession_AndDeliversMessagesSentWhileOffline()
    {
        await using var server = new LocalHomeserver();
        var store = NewDirectory();
        var device = $"ALICE{Guid.NewGuid():N}";
        try
        {
            // Alice is the only test user no other test connects with the client. Each connect sets
            // presence, which Synapse rate-limits per user.
            var bob = await server.LoginAsync("bob");
            using var firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(server.CancellationToken);
            var first = await server.ConnectAsync("alice", device, null, firstLifetime.Token, store);
            first.AutoJoinOnInvite = true;

            var created = await LocalHomeserver.RequestAsync(bob, "/_matrix/client/v3/createRoom", HttpMethod.Post,
                new { preset = "private_chat", invite = new[] { "@alice:localhost" } });
            var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(created.GetProperty("room_id").GetString()!)}";
            var online = await SendTextAsync(bob, roomPath, "before restart");
            await ReceiveAsync(first, online, server.CancellationToken);
            await StopAsync(first, firstLifetime);
            var tokenBefore = ReadSession(store, device).AccessToken;

            var offline = await SendTextAsync(bob, roomPath, "while offline");
            var restarted = await server.ConnectAsync("alice", device, null, server.CancellationToken, store);
            var received = await ReceiveAsync(restarted, offline, server.CancellationToken);
            await Assert.That(received.Body).IsEqualTo("while offline");
            await Assert.That(received.Room.Users.ContainsKey("@bob:localhost")).IsTrue();
            await Assert.That(restarted.HttpClientParameters.BearerToken).IsEqualTo(tokenBefore);
        }
        finally
        {
            DeleteDirectory(store);
        }
    }

    [Test]
    public async Task Restart_WithRejectedSyncToken_SyncsFromScratch()
    {
        await using var server = new LocalHomeserver();
        var store = NewDirectory();
        var device = $"ALICE{Guid.NewGuid():N}";
        try
        {
            using var firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(server.CancellationToken);
            var first = await server.ConnectAsync("alice", device, null, firstLifetime.Token, store);
            await WaitForSyncAsync(first, server.CancellationToken);
            await StopAsync(first, firstLifetime);

            using (var session = SessionStore.Open(store, "@alice:localhost", device))
            {
                var stored = session.Session!;
                session.Save(new StoredSession
                {
                    UserId = stored.UserId,
                    DeviceId = stored.DeviceId,
                    Homeserver = stored.Homeserver,
                    AccessToken = stored.AccessToken,
                    SyncToken = "m1234~5.6",
                });
            }

            using var secondLifetime = CancellationTokenSource.CreateLinkedTokenSource(server.CancellationToken);
            var restarted = await server.ConnectAsync("alice", device, null, secondLifetime.Token, store);
            await WaitForSyncAsync(restarted, server.CancellationToken);
            await StopAsync(restarted, secondLifetime);
            var syncToken = ReadSession(store, device).SyncToken;
            await Assert.That(syncToken).IsNotNull();
            await Assert.That(syncToken).IsNotEqualTo("m1234~5.6");
        }
        finally
        {
            DeleteDirectory(store);
        }
    }

    private static StoredSession ReadSession(string store, string device)
    {
        using var session = SessionStore.Open(store, "@alice:localhost", device);
        return session.Session!;
    }

    private static async Task<string> SendTextAsync(HttpClientParameters sender, string roomPath, string body)
    {
        var sent = await LocalHomeserver.RequestAsync(sender, $"{roomPath}/send/m.room.message/{Guid.NewGuid():N}",
            HttpMethod.Put, new { msgtype = "m.text", body });
        return sent.GetProperty("event_id").GetString()!;
    }

    private static async Task WaitForSyncAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        while (client.LastSyncAt == null)
        {
            if (client.Messages.Completion.IsCompleted)
                await client.Messages.Completion; // surfaces a sync failure
            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>Stops the client and waits until its session file is released.</summary>
    private static async Task StopAsync(MatrixClient client, CancellationTokenSource lifetime)
    {
        await lifetime.CancelAsync();
        await foreach (var unused in client.Messages.ReadAllAsync(CancellationToken.None)) { }
    }

    private static async Task<ReceivedTextMessage> ReceiveAsync(MatrixClient client, string eventId,
        CancellationToken cancellationToken)
    {
        await foreach (var message in client.Messages.ReadAllAsync(cancellationToken))
        {
            if (message.EventId == eventId)
                return message;
        }
        throw new InvalidOperationException($"Sync ended before receiving event {eventId}.");
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
