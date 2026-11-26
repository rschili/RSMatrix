using System.Text.Json;
using RSMatrix.Crypto;
using RSMatrix.Http;
using RSMatrix.Models;
using static RSMatrix.IntegrationTests.EncryptionTests;

namespace RSMatrix.IntegrationTests;

public class CrossSigningTests
{
    [Test]
    public async Task LoggingInCreatesNoIdentity_SetUpOnce_AndEveryDeviceWithTheRecoveryKeyIsSigned()
    {
        await using var server = new LocalHomeserver();
        var user = await server.RegisterUserAsync("dana");
        var userId = $"@{user}:localhost";
        var storeRoot = Path.Combine(Path.GetTempPath(), $"rsmatrix-xsign-{Guid.NewGuid():N}");
        try
        {
            var observer = await server.LoginAsync("alice");
            var firstDevice = $"FIRST{Guid.NewGuid():N}";
            var firstStore = Path.Combine(storeRoot, "first");
            var first = await server.LoginClientAsync(user, firstStore, firstDevice, crossSign: false);
            await Assert.That(ReadIssues(first).Count).IsEqualTo(0);
            await Assert.That(first.EncryptionState).IsEqualTo(EncryptionState.NoIdentity);
            await Assert.That(CrossSigning.ReadIdentity(await QueryKeysAsync(observer, userId), userId)).IsNull();

            server.RecoveryKeys[user] = await first.SetUpCrossSigningAsync(LocalHomeserver.Password);
            await Assert.That(first.EncryptionState).IsEqualTo(EncryptionState.Ready);
            await first.DisposeAsync();
            var identity = await QueryIdentityAsync(observer, userId);
            await Assert.That(await IsCrossSignedAsync(observer, userId, firstDevice)).IsTrue();

            // A signed device restarts without needing the key.
            var restarted = await server.ResumeClientAsync(user, firstStore, crossSign: false);
            await Assert.That(ReadIssues(restarted).Count).IsEqualTo(0);
            await Assert.That(restarted.EncryptionState).IsEqualTo(EncryptionState.Ready);

            var secondDevice = $"SECOND{Guid.NewGuid():N}";
            var second = await server.LoginClientAsync(user, Path.Combine(storeRoot, "second"), secondDevice);
            await Assert.That(ReadIssues(second).Count).IsEqualTo(0);
            await Assert.That(await IsCrossSignedAsync(observer, userId, secondDevice)).IsTrue();
            await Assert.That((await QueryIdentityAsync(observer, userId)).MasterKey).IsEqualTo(identity.MasterKey);

            var thirdDevice = $"THIRD{Guid.NewGuid():N}";
            var third = await server.LoginClientAsync(user, Path.Combine(storeRoot, "third"), thirdDevice, crossSign: false);
            await Assert.That(ReadIssues(third).Single().Kind).IsEqualTo(ClientIssueKind.DeviceNotCrossSigned);
            await Assert.That(third.EncryptionState).IsEqualTo(EncryptionState.DeviceNotSigned);
            await Assert.That(await IsCrossSignedAsync(observer, userId, thirdDevice)).IsFalse();

            var wrongKey = SecretStorage.CreateRecoveryKey();
            await Assert.That(async () => await server.LoginClientAsync(user, Path.Combine(storeRoot, "fourth"),
                recoveryKey: wrongKey))
                .Throws<InvalidOperationException>().WithMessageContaining("does not match");
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }
    }

    [Test]
    public async Task SetUp_ReplacesLostRecoveryKey_AndOtherDevicesNeedTheNewOne()
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
            var first = await server.LoginClientAsync(user, firstStore, firstDevice);
            await first.DisposeAsync();
            var firstKey = server.RecoveryKeys[user];
            var oldMaster = (await QueryIdentityAsync(observer, userId)).MasterKey;

            // The key got lost: a new device can't sign itself and sets up a new identity instead.
            var secondDevice = $"SECOND{Guid.NewGuid():N}";
            var second = await server.LoginClientAsync(user, Path.Combine(storeRoot, "second"), secondDevice, crossSign: false);
            await Assert.That(await IsCrossSignedAsync(observer, userId, secondDevice)).IsFalse();
            server.RecoveryKeys[user] = await second.SetUpCrossSigningAsync(LocalHomeserver.Password);

            await Assert.That(server.RecoveryKeys[user]).IsNotEqualTo(firstKey);
            await Assert.That((await QueryIdentityAsync(observer, userId)).MasterKey).IsNotEqualTo(oldMaster);
            await Assert.That(await IsCrossSignedAsync(observer, userId, secondDevice)).IsTrue();
            await Assert.That(await IsCrossSignedAsync(observer, userId, firstDevice)).IsFalse();

            // The first device is signed again when it resumes with the new key.
            var restarted = await server.ResumeClientAsync(user, firstStore);
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
            var bob = await server.LoginClientAsync(bobUser, Path.Combine(storeRoot, "bob"));
            var carol = await server.LoginClientAsync(carolUser, Path.Combine(storeRoot, "carol"));
            // Carol's second device has no recovery key, so it stays unsigned.
            var carolUnsigned = await server.LoginClientAsync(carolUser, Path.Combine(storeRoot, "carol-unsigned"),
                crossSign: false);
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
