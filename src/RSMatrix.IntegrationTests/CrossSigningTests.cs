using System.Text.Json;
using RSMatrix.Crypto;
using RSMatrix.Http;
using RSMatrix.Models;
using static RSMatrix.IntegrationTests.EncryptionTests;

namespace RSMatrix.IntegrationTests;

public class CrossSigningTests
{
    [Test]
    public async Task SigningKeys_AreCreatedOnce_AndEveryDeviceWithTheRecoveryKeyIsSigned()
    {
        await using var server = new LocalHomeserver();
        var user = await server.RegisterUserAsync("dana");
        var userId = $"@{user}:localhost";
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-xsign-{Guid.NewGuid():N}");
        try
        {
            var observer = await server.LoginAsync("alice");
            var firstDevice = $"FIRST{Guid.NewGuid():N}";
            using (var firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(server.CancellationToken))
            {
                var first = await server.ConnectAsync(user, firstDevice, Path.Combine(storeRoot, "first"), firstLifetime.Token,
                    getRecoveryKey: () => throw new InvalidOperationException("A new account must not ask for a recovery key."));
                await Assert.That(ReadIssues(first).Count).IsEqualTo(0);
                await firstLifetime.CancelAsync();
                await foreach (var unused in first.Messages.ReadAllAsync(server.CancellationToken)) { }
            }
            await Assert.That(server.RecoveryKeys.ContainsKey(user)).IsTrue();
            var identity = await QueryIdentityAsync(observer, userId);
            await Assert.That(await IsCrossSignedAsync(observer, userId, firstDevice)).IsTrue();

            // A signed device restarts without being asked for the key.
            var restarted = await server.ConnectAsync(user, firstDevice, Path.Combine(storeRoot, "first"), server.CancellationToken,
                getRecoveryKey: () => throw new InvalidOperationException("A signed device must not ask for a recovery key."));
            await Assert.That(ReadIssues(restarted).Count).IsEqualTo(0);

            var secondDevice = $"SECOND{Guid.NewGuid():N}";
            var second = await server.ConnectAsync(user, secondDevice, Path.Combine(storeRoot, "second"), server.CancellationToken);
            await Assert.That(ReadIssues(second).Count).IsEqualTo(0);
            await Assert.That(await IsCrossSignedAsync(observer, userId, secondDevice)).IsTrue();
            await Assert.That((await QueryIdentityAsync(observer, userId)).MasterKey).IsEqualTo(identity.MasterKey);

            var thirdDevice = $"THIRD{Guid.NewGuid():N}";
            var third = await server.ConnectAsync(user, thirdDevice, Path.Combine(storeRoot, "third"), server.CancellationToken,
                getRecoveryKey: () => null);
            await Assert.That(ReadIssues(third).Single().Kind).IsEqualTo(ClientIssueKind.DeviceNotCrossSigned);
            await Assert.That(await IsCrossSignedAsync(observer, userId, thirdDevice)).IsFalse();

            var wrongKey = SecretStorage.CreateRecoveryKey();
            await Assert.That(async () => await server.ConnectAsync(user, $"FOURTH{Guid.NewGuid():N}", Path.Combine(storeRoot, "fourth"),
                server.CancellationToken, getRecoveryKey: () => wrongKey))
                .Throws<InvalidOperationException>().WithMessageContaining("does not match");
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    [Test]
    public async Task Reset_ReplacesLostRecoveryKey_AndOtherDevicesNeedTheNewOne()
    {
        await using var server = new LocalHomeserver();
        var user = await server.RegisterUserAsync("erin");
        var userId = $"@{user}:localhost";
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-xsign-{Guid.NewGuid():N}");
        try
        {
            var observer = await server.LoginAsync("alice");
            var firstDevice = $"FIRST{Guid.NewGuid():N}";
            var firstStore = Path.Combine(storeRoot, "first");
            using (var firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(server.CancellationToken))
            {
                var first = await server.ConnectAsync(user, firstDevice, firstStore, firstLifetime.Token);
                await firstLifetime.CancelAsync();
                await foreach (var unused in first.Messages.ReadAllAsync(server.CancellationToken)) { }
            }
            var firstKey = server.RecoveryKeys[user];
            var oldMaster = (await QueryIdentityAsync(observer, userId)).MasterKey;

            // The key got lost: a new device can't sign itself and resets instead.
            var secondDevice = $"SECOND{Guid.NewGuid():N}";
            var second = await server.ConnectAsync(user, secondDevice, Path.Combine(storeRoot, "second"), server.CancellationToken,
                getRecoveryKey: () => null);
            await Assert.That(await IsCrossSignedAsync(observer, userId, secondDevice)).IsFalse();
            await second.ResetCrossSigningAsync();

            await Assert.That(server.RecoveryKeys[user]).IsNotEqualTo(firstKey);
            await Assert.That((await QueryIdentityAsync(observer, userId)).MasterKey).IsNotEqualTo(oldMaster);
            await Assert.That(await IsCrossSignedAsync(observer, userId, secondDevice)).IsTrue();
            await Assert.That(await IsCrossSignedAsync(observer, userId, firstDevice)).IsFalse();

            // The first device is signed again when it connects with the new key.
            var restarted = await server.ConnectAsync(user, firstDevice, firstStore, server.CancellationToken);
            await Assert.That(ReadIssues(restarted).Single().Kind).IsEqualTo(ClientIssueKind.IdentityChanged);
            await Assert.That(await IsCrossSignedAsync(observer, userId, firstDevice)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    [Test]
    public async Task DeviceNotCrossSigned_CanNeitherReadNorBeRead()
    {
        await using var server = new LocalHomeserver();
        var bobUser = await server.RegisterUserAsync("bob");
        var carolUser = await server.RegisterUserAsync("carol");
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-xsign-{Guid.NewGuid():N}");
        try
        {
            var alice = await server.LoginAsync("alice");
            var bob = await server.ConnectAsync(bobUser, $"BOB{Guid.NewGuid():N}", Path.Combine(storeRoot, "bob"), server.CancellationToken);
            var carol = await server.ConnectAsync(carolUser, $"CAROL{Guid.NewGuid():N}", Path.Combine(storeRoot, "carol"), server.CancellationToken);
            // Carol's second device has no recovery key, so it stays unsigned.
            var carolUnsigned = await server.ConnectAsync(carolUser, $"CAROL{Guid.NewGuid():N}", Path.Combine(storeRoot, "carol-unsigned"),
                server.CancellationToken, getRecoveryKey: () => null);
            await Assert.That(ReadIssues(carolUnsigned).Single().Kind).IsEqualTo(ClientIssueKind.DeviceNotCrossSigned);
            var (roomPath, bobRoom) = await CreateEncryptedRoomAsync(alice, bob, [carol, carolUnsigned],
                new { algorithm = "m.megolm.v1.aes-sha2" }, server.CancellationToken);

            var fromBobId = await bobRoom.SendTextMessageAsync("for signed devices", null, null);
            await Assert.That((await ReceiveAsync(carol, fromBobId, server.CancellationToken)).Body).IsEqualTo("for signed devices");
            await Assert.That(ReadIssues(bob).Any(i => i.Kind == ClientIssueKind.RoomKeyNotShared && i.Message.Contains("not cross-signed"))).IsTrue();

            var pingId = await SendPlaintextAsync(alice, roomPath, "ping");
            var ping = await ReceiveAsync(carolUnsigned, pingId, server.CancellationToken);
            var unreadable = ReadIssues(carolUnsigned).Single(i => i.EventId == fromBobId);
            await Assert.That(unreadable.Kind).IsEqualTo(ClientIssueKind.DecryptionFailed);
            await Assert.That(unreadable.Message.Contains("m.unverified")).IsTrue();

            var fromUnsignedId = await ping.Room.SendTextMessageAsync("from an unsigned device", null, null);
            var secondPingId = await SendPlaintextAsync(alice, roomPath, "second ping");
            await ReceiveAsync(bob, secondPingId, server.CancellationToken);
            var refused = ReadIssues(bob).Single(i => i.EventId == fromUnsignedId);
            await Assert.That(refused.Kind).IsEqualTo(ClientIssueKind.DecryptionFailed);
            await Assert.That(refused.Message.Contains("not cross-signed")).IsTrue();
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    private static async Task<JsonElement> QueryKeysAsync(HttpClientParameters observer, string userId)
        => await LocalHomeserver.RequestAsync(observer, "/_matrix/client/v3/keys/query", HttpMethod.Post,
            new Dictionary<string, object> { ["device_keys"] = new Dictionary<string, string[]> { [userId] = [] } });

    private static async Task<UserIdentity> QueryIdentityAsync(HttpClientParameters observer, string userId)
        => CrossSigning.ReadIdentity(await QueryKeysAsync(observer, userId), userId)
           ?? throw new InvalidOperationException($"{userId} has no cross-signing identity.");

    private static async Task<bool> IsCrossSignedAsync(HttpClientParameters observer, string userId, string deviceId)
    {
        var response = await QueryKeysAsync(observer, userId);
        var device = response.GetProperty("device_keys").GetProperty(userId).GetProperty(deviceId);
        return CrossSigning.IsDeviceCrossSigned(device, userId, deviceId, CrossSigning.ReadIdentity(response, userId));
    }
}
