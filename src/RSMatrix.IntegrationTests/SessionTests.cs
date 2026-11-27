using RSMatrix.Http;
using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

public class SessionTests
{
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"rsmatrix-it-session-{Guid.NewGuid():N}");

    [Test]
    public async Task Restart_ResumesSession_AndDeliversMessagesSentWhileOffline()
    {
        await using var server = new LocalHomeserver();
        var store = NewDirectory();
        try
        {
            // Alice is the only test user no other test connects with the client. Each connect sets
            // presence, which Synapse rate-limits per user.
            var bob = await server.LoginAsync("bob");
            // Alice is a shared test user, so the test must not give her a cross-signing identity.
            // The server creates the device ID, and the store keeps it.
            var first = await server.LoginClientAsync("alice", store, crossSign: false);

            var created = await LocalHomeserver.RequestAsync(bob, "/_matrix/client/v3/createRoom", HttpMethod.Post,
                new { preset = "private_chat", invite = new[] { "@alice:localhost" } });
            var roomId = created.GetProperty("room_id").GetString()!;
            var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}";
            await AcceptInviteAsync(first, roomId, server.CancellationToken);
            var online = await SendTextAsync(bob, roomPath, "before restart");
            await ReceiveAsync(first, online, server.CancellationToken);
            await first.DisposeAsync();
            var tokenBefore = ReadSession(store).AccessToken;

            var offline = await SendTextAsync(bob, roomPath, "while offline");
            // An intact store resumes the session without the password.
            var restarted = await server.ResumeClientAsync("alice", store, crossSign: false);
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
        try
        {
            var first = await server.LoginClientAsync("alice", store, crossSign: false);
            await WaitForSyncAsync(first, server.CancellationToken);
            await first.DisposeAsync();

            using (var session = SessionStore.Open(store, "@alice:localhost"))
            {
                var stored = session.Session!;
                session.Save(new SessionData
                {
                    UserId = stored.UserId,
                    DeviceId = stored.DeviceId,
                    Homeserver = stored.Homeserver,
                    AccessToken = stored.AccessToken,
                    SyncToken = "m1234~5.6",
                });
            }

            var restarted = await server.ResumeClientAsync("alice", store, crossSign: false);
            await WaitForSyncAsync(restarted, server.CancellationToken);
            await restarted.DisposeAsync();
            var syncToken = ReadSession(store).SyncToken;
            await Assert.That(syncToken).IsNotNull();
            await Assert.That(syncToken).IsNotEqualTo("m1234~5.6");
        }
        finally
        {
            DeleteDirectory(store);
        }
    }

    private static SessionData ReadSession(string store) => SessionStore.Read(store, "@alice:localhost")!;

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
            if (!client.IsSyncing)
            {
                ReadIssues(client); // the channel only completes once everything was read
                await client.Updates.Completion; // surfaces a sync failure
            }
            await Task.Delay(100, cancellationToken);
        }
    }


    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
