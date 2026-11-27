using System.Diagnostics;
using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

/// <summary>
/// Talks to an independent implementation: a matrix-rust-sdk client (the SDK behind Element X).
/// The peer only trusts cross-signed devices, so this also checks that our device is cross-signed.
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
        // New users: the peer creates its own cross-signing identity, which only works for a user without one.
        var bobUser = await server.RegisterUserAsync("bob");
        var peerUser = await server.RegisterUserAsync("rust");
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-interop-{Guid.NewGuid():N}");
        try
        {
            var alice = await server.LoginAsync("alice");
            var bob = await server.LoginClientAsync(bobUser, Path.Combine(storeRoot, "bob"));

            var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post, new
            {
                preset = "private_chat",
                invite = new[] { $"@{bobUser}:localhost", $"@{peerUser}:localhost" },
                initial_state = new[]
                {
                    new { type = "m.room.encryption", state_key = "", content = new { algorithm = "m.megolm.v1.aes-sha2" } }
                }
            });
            var roomId = created.GetProperty("room_id").GetString()!;
            var roomPath = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}";
            await AcceptInviteAsync(bob, roomId, server.CancellationToken);

            var marker = Guid.NewGuid().ToString("N");
            bool fromPeer(Message m) => m.Room.RoomId.Full == roomId && m.Sender.User.UserId.Full == $"@{peerUser}:localhost";
            using var process = Process.Start(new ProcessStartInfo(peer!,
                [server.BaseUri.AbsoluteUri.TrimEnd('/'), peerUser, LocalHomeserver.Password, roomId, marker, mode])
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
                    var room = (await ReceiveAsync(bob, m => m.Id == ping.GetProperty("event_id").GetString(), server.CancellationToken)).Room;
                    await room.SendMessageAsync($"bob says {marker}");
                    var echo = await ReceiveAsync(bob, fromPeer, server.CancellationToken);
                    await Assert.That(echo.Body).IsEqualTo($"rust echo: bob says {marker}");
                    await echo.ReplyAsync($"bob ack {marker}", quote: false);
                }
                else
                {
                    var hello = await ReceiveAsync(bob, fromPeer, server.CancellationToken);
                    await Assert.That(hello.Body).IsEqualTo($"rust says {marker}");
                    await hello.ReplyAsync($"bob ack {marker}", quote: false);
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

}
