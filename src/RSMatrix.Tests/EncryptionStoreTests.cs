using RSMatrix.Crypto;

namespace RSMatrix.Tests;

public class EncryptionStoreTests
{
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"rsmatrix-store-{Guid.NewGuid():N}");

    [Test]
    public async Task Reopen_LoadsSameAccount_WithOwnerOnlyFile()
    {
        var directory = NewDirectory();
        try
        {
            byte[] identityKey;
            using (var store = EncryptionStore.Open(directory, "@bot:example.org", "BOT"))
            {
                await Assert.That(store.IsNew).IsTrue();
                identityKey = store.Data.Account.IdentityKey;
                store.Data.DeviceKeysUploaded = true;
                store.Save();
            }

            using var reopened = EncryptionStore.Open(directory, "@bot:example.org", "BOT");
            await Assert.That(reopened.IsNew).IsFalse();
            await Assert.That(reopened.Data.DeviceKeysUploaded).IsTrue();
            await Assert.That(reopened.Data.Account.IdentityKey.SequenceEqual(identityKey)).IsTrue();
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(Path.Combine(directory, "rsmatrix-encryption.json"));
                await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task OtherDevice_IsRejected()
    {
        var directory = NewDirectory();
        try
        {
            EncryptionStore.Open(directory, "@bot:example.org", "BOT").Dispose();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Task.FromResult(EncryptionStore.Open(directory, "@bot:example.org", "OTHER")));
            await Assert.That(error!.Message.Contains("BOT")).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task SecondOpen_WhileInUse_IsRejected()
    {
        var directory = NewDirectory();
        try
        {
            using var store = EncryptionStore.Open(directory, "@bot:example.org", "BOT");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Task.FromResult(EncryptionStore.Open(directory, "@bot:example.org", "BOT")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
