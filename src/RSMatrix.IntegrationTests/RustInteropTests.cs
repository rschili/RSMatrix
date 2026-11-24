using System.Diagnostics;
using RSMatrix.Models;

namespace RSMatrix.IntegrationTests;

/// <summary>
/// Talks to an independent implementation: a matrix-rust-sdk client (the SDK behind Element X).
/// Opt-in: set RSMATRIX_RUST_PEER to the built rust-peer binary.
/// </summary>
public class RustInteropTests
{
    [Test]
    [Arguments("bob-first")]
    [Arguments("rust-first")]
    public async Task EncryptedConversation_WithMatrixRustSdk(string mode)
    {
        var peer = Environment.GetEnvironmentVariable("RSMATRIX_RUST_PEER");
        TUnit.Core.Skip.When(string.IsNullOrEmpty(peer), "Set RSMATRIX_RUST_PEER to run the matrix-rust-sdk interop test.");

        await using var server = new LocalHomeserver();
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-interop-{Guid.NewGuid():N}");
        try
        {
            var alice = await server.LoginAsync("alice");
            var bob = await server.ConnectAsync("bob", $"BOB{Guid.NewGuid():N}",
                Path.Combine(storeRoot, "bob"), server.CancellationToken);
            bob.AutoJoinOnInvite = true;

            var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post, new
            {
                preset = "private_chat",
                invite = new[] { "@bob:localhost", "@carol:localhost" },
                initial_state = new[]
                {
                    new { type = "m.room.encryption", state_key = "", content = new { algorithm = "m.megolm.v1.aes-sha2" } }
                }
            });
            var roomId = created.GetProperty("room_id").GetString()!;
            var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}";

            var marker = Guid.NewGuid().ToString("N");
            // Other tests use carol too; only look at this room.
            bool fromPeer(ReceivedTextMessage m) => m.Room.RoomId.Full == roomId && m.Sender.User.UserId.Full == "@carol:localhost";
            using var process = Process.Start(new ProcessStartInfo(peer!,
                [server.BaseUri.AbsoluteUri.TrimEnd('/'), "carol", LocalHomeserver.Password, roomId, marker, mode])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await WaitForJoinedMembersAsync(alice, roomPath, 3, server.CancellationToken);

                if (mode == "bob-first")
                {
                    // A plaintext ping hands bob the Room with its synced encryption state.
                    var ping = await LocalHomeserver.RequestAsync(alice, $"{roomPath}/send/m.room.message/{Guid.NewGuid():N}",
                        HttpMethod.Put, new { msgtype = "m.text", body = "ping" });
                    var room = (await ReceiveAsync(bob, m => m.EventId == ping.GetProperty("event_id").GetString(), server.CancellationToken)).Room;
                    await room.SendTextMessageAsync($"bob says {marker}", null, null);
                    var echo = await ReceiveAsync(bob, fromPeer, server.CancellationToken);
                    await Assert.That(echo.Body).IsEqualTo($"rust echo: bob says {marker}");
                    await echo.SendResponseAsync($"bob ack {marker}");
                }
                else
                {
                    var hello = await ReceiveAsync(bob, fromPeer, server.CancellationToken);
                    await Assert.That(hello.Body).IsEqualTo($"rust says {marker}");
                    await hello.SendResponseAsync($"bob ack {marker}");
                }

                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(server.CancellationToken);
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(exitTimeout.Token);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill();
                Console.WriteLine(await output);
                Console.Error.WriteLine(await errors);
            }
            await Assert.That(process.ExitCode).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    private static async Task WaitForJoinedMembersAsync(RSMatrix.Http.HttpClientParameters user, string roomPath, int count,
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

    private static async Task<ReceivedTextMessage> ReceiveAsync(MatrixTextClient client, Func<ReceivedTextMessage, bool> match,
        CancellationToken cancellationToken)
    {
        await foreach (var message in client.Messages.ReadAllAsync(cancellationToken))
        {
            if (match(message))
                return message;
        }
        throw new InvalidOperationException("Sync ended before receiving the expected message.");
    }
}
