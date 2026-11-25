using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Crypto;

/// <summary>A device's published keys, after checking its self-signature.</summary>
/// <param name="IsCrossSigned">Whether the owner's self-signing key signed the device.</param>
internal sealed record DeviceKeys(string UserId, string DeviceId, string Curve25519, string Ed25519, bool IsCrossSigned = false)
{
    public string Id => $"{UserId}|{DeviceId}";
}

/// <summary>Our Megolm session for one room and the devices that received its key.</summary>
internal sealed class OutboundGroupSession
{
    public MegolmOutboundSession Session { get; } = new();
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    /// <summary>Device ID (see <see cref="DeviceKeys.Id"/>) to the Curve25519 key it had when we shared.</summary>
    public Dictionary<string, string> SharedWith { get; } = [];
    /// <summary>Devices that could not receive the key and were already reported as an issue.</summary>
    public HashSet<string> NotSharedReported { get; } = [];
}

/// <summary>
/// End-to-end encryption for one device: key upload, cross-signing, receiving room keys over Olm,
/// decrypting and encrypting room events with Megolm.
/// </summary>
/// <remarks>
/// Follows the spec's recommendation to only trust devices that their owner cross-signed:
/// other devices get no room keys, and messages from them are not decrypted.
/// </remarks>
internal sealed class EncryptionEngine : IDisposable
{
    internal const string OlmAlgorithm = "m.olm.v1.curve25519-aes-sha2";
    internal const string MegolmAlgorithm = "m.megolm.v1.aes-sha2";
    private const string SignedCurve25519 = "signed_curve25519";
    private const int OneTimeKeyTarget = 50;
    private const int MaxStoredOneTimeKeys = 100;
    private const int MaxOlmSessionsPerDevice = 10;
    private const int ToDeviceBatchSize = 100;
    private const int MaxWithheldSessions = 1000;
    private const string UnverifiedCode = "m.unverified";

    private readonly EncryptionStore _store;
    private readonly MatrixClient _client;
    private readonly EncryptionOptions _options;
    private readonly string _password;
    private readonly HttpClientParameters _http;
    private readonly string _userId;
    private readonly string _deviceId;
    private ILogger Logger => _http.Logger;

    // Olm sessions, the account, device lists and outbound sessions change under this lock.
    // Decryption of room events only reads inbound sessions and does not need it.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, DeviceKeys>> _devices = [];
    private readonly Dictionary<string, UserIdentity?> _identities = [];
    private readonly Dictionary<string, string> _pinnedSigningKeys = [];
    private readonly ConcurrentDictionary<string, OutboundGroupSession> _outboundSessions = new();
    private readonly ConcurrentDictionary<string, List<string>> _roomMembers = new();
    private int _membershipVersion;
    private readonly ConcurrentDictionary<(string SessionId, uint Index), string> _decryptedEventIds = new();
    /// <summary>Session ID to the code another device gave for not sharing its key with us.</summary>
    private readonly ConcurrentDictionary<string, string> _withheldSessions = new();
    /// <summary>Our published device keys including the cross-signature, once the device is cross-signed.</summary>
    private JsonObject? _crossSignedDeviceKeys;

    private EncryptionEngine(EncryptionStore store, MatrixClient client, EncryptionOptions options, string password)
    {
        _store = store;
        _client = client;
        _options = options;
        _password = password;
        _http = client.HttpClientParameters;
        _userId = store.Data.UserId;
        _deviceId = store.Data.DeviceId;
    }

    private OlmAccount Account => _store.Data.Account;
    private string IdentityKey => CryptoPrimitives.ToBase64(Account.IdentityKey);
    private string SigningKey => CryptoPrimitives.ToBase64(Account.SigningKey);

    /// <summary>Opens the store before logging in, so a wrong directory fails before creating a session.</summary>
    public static EncryptionStore OpenStore(string directory, string userId, string deviceId)
        => EncryptionStore.Open(directory, userId, deviceId);

    /// <summary>
    /// Publishes our device keys on first use, checks that the server agrees with the store,
    /// and makes sure the device is cross-signed, as <see cref="EncryptionOptions"/> describes.
    /// </summary>
    /// <param name="password">The account password, for servers that ask for it before accepting new signing keys.</param>
    public static async Task<EncryptionEngine> StartAsync(EncryptionStore store, MatrixClient client, EncryptionOptions options, string password)
    {
        var engine = new EncryptionEngine(store, client, options, password);
        await engine.PublishDeviceKeysAsync().ConfigureAwait(false);
        await engine.SetUpCrossSigningAsync().ConfigureAwait(false);
        return engine;
    }

    public void Dispose() => _store.Dispose();

    private async Task PublishDeviceKeysAsync()
    {
        var query = new JsonObject { ["device_keys"] = new JsonObject { [_userId] = new JsonArray(_deviceId) } };
        var response = await RequestAsync("Querying own device keys", "/_matrix/client/v3/keys/query", HttpMethod.Post, query, retry: true).ConfigureAwait(false);
        if (response.TryGetProperty("device_keys", out var users) && users.TryGetProperty(_userId, out var devices) &&
            devices.TryGetProperty(_deviceId, out var published))
        {
            var keys = ParseDeviceKeys(published, _userId, _deviceId);
            if (keys?.Ed25519 != SigningKey || keys.Curve25519 != IdentityKey)
                throw new InvalidOperationException(
                    $"The server already has different encryption keys for device {_deviceId}. The encryption store " +
                    "does not belong to that device. Use a new device ID, or delete the device from the account.");
            if (!_store.Data.DeviceKeysUploaded)
            {
                _store.Data.DeviceKeysUploaded = true;
                _store.Save();
            }
            return;
        }

        if (_store.Data.DeviceKeysUploaded)
            throw new InvalidOperationException(
                $"The server has no encryption keys for device {_deviceId}, although this store published them. " +
                "The device was probably deleted. Use a new device ID and a new store directory.");

        Account.GenerateOneTimeKeys(OneTimeKeyTarget);
        Account.GenerateFallbackKey();
        await UploadKeysAsync(includeDeviceKeys: true).ConfigureAwait(false);
        _store.Data.DeviceKeysUploaded = true;
        _store.Save();
        Logger.LogInformation("Published encryption keys for device {DeviceId}.", _deviceId);
    }

    /// <summary>Makes sure the account's cross-signing keys signed this device. See <see cref="EncryptionOptions"/> for the cases.</summary>
    private async Task SetUpCrossSigningAsync()
    {
        var (identity, ownDevice, hasInvalidIdentity) = await QueryOwnIdentityAsync().ConfigureAwait(false);
        if (hasInvalidIdentity)
        {
            ReportNotCrossSigned("the account's published master key is invalid. MatrixClient.ResetCrossSigningAsync replaces it");
            return;
        }
        if (identity == null)
        {
            await CreateIdentityAsync(ownDevice).ConfigureAwait(false);
            return;
        }

        PinMasterKey(_userId, identity.MasterKey);
        if (ownDevice is { } published && CrossSigning.IsDeviceCrossSigned(published, _userId, _deviceId, identity))
        {
            Logger.LogDebug("Device {DeviceId} is cross-signed.", _deviceId);
            RememberCrossSignedDeviceKeys(published);
            return;
        }

        if (await ReadStoredSelfSigningKeyAsync().ConfigureAwait(false) is not { } stored)
        {
            ReportNotCrossSigned("the account's signing keys are not in secret storage, so a recovery key can't unlock them. " +
                "MatrixClient.ResetCrossSigningAsync creates new ones");
            return;
        }
        var recoveryKeyText = await _options.GetRecoveryKeyAsync(_http.CancellationToken).ConfigureAwait(false);
        if (recoveryKeyText == null)
        {
            ReportNotCrossSigned("signing it needs the account's recovery key, and none was given");
            return;
        }
        byte[] recoveryKey;
        try
        {
            recoveryKey = SecretStorage.DecodeRecoveryKey(recoveryKeyText);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"The recovery key from {nameof(EncryptionOptions.GetRecoveryKeyAsync)} is invalid: {ex.Message}", ex);
        }
        if (!SecretStorage.MatchesKeyDescription(recoveryKey, stored.KeyDescription))
            throw new InvalidOperationException("The recovery key does not match the account's secret storage.");

        Ed25519KeyPair selfSigningKey;
        try
        {
            selfSigningKey = new Ed25519KeyPair(CryptoPrimitives.FromBase64(SecretStorage.Decrypt(recoveryKey, stored.SecretName, stored.Secret)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("The account's self-signing key can't be read with this recovery key.", ex);
        }
        if (CryptoPrimitives.ToBase64(selfSigningKey.PublicKey) != identity.SelfSigningKey)
        {
            ReportNotCrossSigned("the signing keys in secret storage don't match the published ones. " +
                "MatrixClient.ResetCrossSigningAsync creates new ones");
            return;
        }
        await SignOwnDeviceAsync(selfSigningKey, ownDevice).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the account's signing keys and recovery key, and signs this device. Other devices of the account
    /// lose their cross-signature.
    /// </summary>
    public async Task ResetCrossSigningAsync()
    {
        await _lock.WaitAsync(_http.CancellationToken).ConfigureAwait(false);
        try
        {
            var (_, ownDevice, _) = await QueryOwnIdentityAsync().ConfigureAwait(false);
            await CreateIdentityAsync(ownDevice).ConfigureAwait(false);
            ForgetDevices(_userId);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(UserIdentity? Identity, JsonElement? OwnDevice, bool HasInvalidIdentity)> QueryOwnIdentityAsync()
    {
        var query = new JsonObject { ["device_keys"] = new JsonObject { [_userId] = new JsonArray(_deviceId) } };
        var response = await RequestAsync("Querying own cross-signing keys", "/_matrix/client/v3/keys/query", HttpMethod.Post, query, retry: true).ConfigureAwait(false);
        JsonElement? ownDevice = response.TryGetProperty("device_keys", out var users) && users.TryGetProperty(_userId, out var devices) &&
            devices.TryGetProperty(_deviceId, out var device) ? device : null;
        var identity = CrossSigning.ReadIdentity(response, _userId);
        var hasMasterKey = response.TryGetProperty("master_keys", out var masterKeys) && masterKeys.ValueKind == JsonValueKind.Object &&
            masterKeys.TryGetProperty(_userId, out _);
        return (identity, ownDevice, identity == null && hasMasterKey);
    }

    /// <summary>
    /// Creates new signing keys and a new recovery key, stores the private keys in secret storage under it,
    /// publishes the keys and signs this device. The caller saves the recovery key before anything changes on the server.
    /// </summary>
    private async Task CreateIdentityAsync(JsonElement? ownDevice)
    {
        var recoveryKey = RandomNumberGenerator.GetBytes(32);
        await _options.SaveRecoveryKeyAsync(SecretStorage.EncodeRecoveryKey(recoveryKey), _http.CancellationToken).ConfigureAwait(false);

        var master = Ed25519KeyPair.Generate();
        var selfSigning = Ed25519KeyPair.Generate();
        var userSigning = Ed25519KeyPair.Generate();
        // Other secrets under an earlier default key, like a key backup from another client, stay readable only with the earlier key.
        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await PutAccountDataAsync(SecretStorage.KeyType(keyId), SecretStorage.CreateKeyDescription(recoveryKey)).ConfigureAwait(false);
        await PutAccountDataAsync(SecretStorage.DefaultKeyType, new JsonObject { ["key"] = keyId }).ConfigureAwait(false);
        foreach (var (usage, key) in new[] { (CrossSigning.Master, master), (CrossSigning.SelfSigning, selfSigning), (CrossSigning.UserSigning, userSigning) })
        {
            var name = CrossSigning.SecretName(usage);
            var secret = SecretStorage.Encrypt(recoveryKey, name, CryptoPrimitives.ToBase64(key.Seed));
            await PutAccountDataAsync(name, new JsonObject { ["encrypted"] = new JsonObject { [keyId] = secret } }).ConfigureAwait(false);
        }

        var body = new JsonObject
        {
            ["master_key"] = CrossSigning.CreateKey(_userId, CrossSigning.Master, master, null),
            ["self_signing_key"] = CrossSigning.CreateKey(_userId, CrossSigning.SelfSigning, selfSigning, master),
            ["user_signing_key"] = CrossSigning.CreateKey(_userId, CrossSigning.UserSigning, userSigning, master)
        };
        await UploadCrossSigningKeysAsync(body).ConfigureAwait(false);
        // We created this identity, so a change of the pinned key is expected and not reported.
        _store.Data.PinnedMasterKeys[_userId] = CryptoPrimitives.ToBase64(master.PublicKey);
        _store.Save();
        Logger.LogInformation("Created new cross-signing keys for {UserId}.", _userId);
        await SignOwnDeviceAsync(selfSigning, ownDevice).ConfigureAwait(false);
    }

    /// <summary>
    /// Since v1.11 servers accept the first upload without authentication. Replacing existing keys needs the password,
    /// through user-interactive authentication.
    /// </summary>
    private async Task UploadCrossSigningKeysAsync(JsonObject body)
    {
        const string path = "/_matrix/client/v3/keys/device_signing/upload";
        string? session;
        try
        {
            await RequestAsync("Uploading cross-signing keys", path, HttpMethod.Post, body, retry: true).ConfigureAwait(false);
            return;
        }
        catch (MatrixResponseException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && ex.ResponseBody != null)
        {
            var challenge = JsonSerializer.Deserialize<JsonElement>(ex.ResponseBody);
            var acceptsPassword = challenge.TryGetProperty("flows", out var flows) && flows.ValueKind == JsonValueKind.Array &&
                flows.EnumerateArray().Any(flow => flow.TryGetProperty("stages", out var stages) && stages.ValueKind == JsonValueKind.Array &&
                    stages.GetArrayLength() == 1 && stages[0].GetString() == "m.login.password");
            if (!acceptsPassword)
                throw new InvalidOperationException(
                    "The server wants a confirmation other than the password to change the account's signing keys, " +
                    $"which RSMatrix doesn't support. Its options: {(flows.ValueKind == JsonValueKind.Array ? flows.GetRawText() : "none")}.", ex);
            session = GetString(challenge, "session");
        }

        body["auth"] = new JsonObject
        {
            ["type"] = "m.login.password",
            ["identifier"] = new JsonObject { ["type"] = "m.id.user", ["user"] = _userId },
            ["password"] = _password,
            ["session"] = session
        };
        await RequestAsync("Uploading cross-signing keys with the password", path, HttpMethod.Post, body, retry: true).ConfigureAwait(false);
    }

    private sealed record StoredSecret(JsonElement KeyDescription, string SecretName, JsonElement Secret);

    /// <summary>The encrypted self-signing key under the default secret storage key, or null if there is none.</summary>
    private async Task<StoredSecret?> ReadStoredSelfSigningKeyAsync()
    {
        var name = CrossSigning.SecretName(CrossSigning.SelfSigning);
        if (await GetAccountDataAsync(SecretStorage.DefaultKeyType).ConfigureAwait(false) is not { } defaultKey ||
            GetString(defaultKey, "key") is not { } keyId ||
            await GetAccountDataAsync(SecretStorage.KeyType(keyId)).ConfigureAwait(false) is not { } description ||
            await GetAccountDataAsync(name).ConfigureAwait(false) is not { } secret ||
            !secret.TryGetProperty("encrypted", out var encrypted) || encrypted.ValueKind != JsonValueKind.Object ||
            !encrypted.TryGetProperty(keyId, out var encryptedSecret))
            return null;
        return new StoredSecret(description, name, encryptedSecret);
    }

    private async Task SignOwnDeviceAsync(Ed25519KeyPair selfSigningKey, JsonElement? ownDevice)
    {
        // The signed object must match the published device keys, so sign the server's copy.
        var deviceKeys = ownDevice is { } published ? JsonNode.Parse(published.GetRawText())!.AsObject() : CreateDeviceKeys();
        deviceKeys.Remove("unsigned");
        CanonicalJson.Sign(deviceKeys, _userId, $"ed25519:{CryptoPrimitives.ToBase64(selfSigningKey.PublicKey)}", data => selfSigningKey.Sign(data));
        var response = await RequestAsync("Uploading the device's cross-signing signature", "/_matrix/client/v3/keys/signatures/upload",
            HttpMethod.Post, new JsonObject { [_userId] = new JsonObject { [_deviceId] = deviceKeys } }, retry: true).ConfigureAwait(false);
        if (response.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Object && failures.EnumerateObject().Any())
        {
            ReportNotCrossSigned($"the server rejected its signature: {failures.GetRawText()}");
            return;
        }
        _crossSignedDeviceKeys = deviceKeys;
        Logger.LogInformation("Cross-signed device {DeviceId}.", _deviceId);
    }

    private void RememberCrossSignedDeviceKeys(JsonElement published)
    {
        _crossSignedDeviceKeys = JsonNode.Parse(published.GetRawText())!.AsObject();
        _crossSignedDeviceKeys.Remove("unsigned");
    }

    private void ReportNotCrossSigned(string reason)
        => _client.ReportIssue(ClientIssueKind.DeviceNotCrossSigned,
            $"Device {_deviceId} is not cross-signed: {reason}. Clients that follow the spec's recommendation " +
            "won't share keys with it or show its encrypted messages.");

    /// <summary>Remembers the user's master key and reports when it differs from the one seen before.</summary>
    private void PinMasterKey(string userId, string masterKey)
    {
        var pinned = _store.Data.PinnedMasterKeys.GetValueOrDefault(userId);
        if (pinned == masterKey)
            return;
        _store.Data.PinnedMasterKeys[userId] = masterKey;
        _store.Save();
        if (pinned != null)
        {
            _client.ReportIssue(ClientIssueKind.IdentityChanged,
                $"The cross-signing identity of {userId} changed. Only devices signed by the new identity are trusted now.", userId: userId);
        }
    }

    private async Task<JsonElement?> GetAccountDataAsync(string type)
    {
        try
        {
            return await RequestAsync($"Reading account data {type}", AccountDataPath(type), HttpMethod.Get, null, retry: true).ConfigureAwait(false);
        }
        catch (MatrixResponseException ex) when (ex.ErrorCode == "M_NOT_FOUND")
        {
            return null;
        }
    }

    private Task PutAccountDataAsync(string type, JsonObject content)
        => RequestAsync($"Writing account data {type}", AccountDataPath(type), HttpMethod.Put, content, retry: true);

    private string AccountDataPath(string type)
        => $"/_matrix/client/v3/user/{Uri.EscapeDataString(_userId)}/account_data/{Uri.EscapeDataString(type)}";

    /// <summary>Processes the encryption parts of a sync response. Runs before room events are handled.</summary>
    public async Task HandleSyncAsync(SyncResponse response)
    {
        await _lock.WaitAsync(_http.CancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var user in response.DeviceLists?.Changed ?? [])
                ForgetDevices(user);
            foreach (var user in response.DeviceLists?.Left ?? [])
                ForgetDevices(user);

            var changed = false;
            foreach (var ev in response.ToDevice?.Events ?? [])
            {
                if (ev?.Content is not { ValueKind: JsonValueKind.Object } content)
                    continue;
                if (ev.Type == "m.room.encrypted")
                    changed |= await HandleEncryptedToDeviceAsync(ev.Sender, content).ConfigureAwait(false);
                else if (ev.Type == "m.room_key.withheld")
                    HandleWithheld(ev.Sender, content);
            }
            if (response.DeviceLists?.Changed is { Count: > 0 } changedUsers)
                changed |= await RecheckUntrustedSessionsAsync(changedUsers).ConfigureAwait(false);
            changed |= await ReplenishKeysAsync(response.DeviceOneTimeKeysCount, response.DeviceUnusedFallbackKeyTypes).ConfigureAwait(false);
            if (changed)
                _store.Save();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ForgetDevices(string userId)
    {
        _devices.Remove(userId);
        _identities.Remove(userId);
    }

    /// <summary>Remembers why a device didn't share a key with us, to explain the undecryptable messages.</summary>
    private void HandleWithheld(string? sender, JsonElement content)
    {
        if (GetString(content, "session_id") is not { } sessionId || GetString(content, "code") is not { } code)
            return;
        if (_withheldSessions.Count >= MaxWithheldSessions)
            _withheldSessions.Clear();
        _withheldSessions[sessionId] = code;
        Logger.LogDebug("{Sender} withheld room key {SessionId} from this device: {Code}.", sender, sessionId, code);
    }

    /// <summary>Sessions from a device that its owner cross-signed later become readable.</summary>
    private async Task<bool> RecheckUntrustedSessionsAsync(List<string> changedUsers)
    {
        var users = changedUsers.ToHashSet(StringComparer.Ordinal);
        var sessions = _store.Data.InboundGroupSessions.Values
            .Where(s => s.SenderCrossSigned == false && users.Contains(s.SenderUserId)).ToList();
        if (sessions.Count == 0)
            return false;

        List<DeviceKeys> devices;
        try
        {
            devices = await GetDevicesAsync(sessions.Select(s => s.SenderUserId), retry: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MatrixResponseException or HttpRequestException)
        {
            Logger.LogWarning(ex, "Could not check whether devices were cross-signed in the meantime.");
            return false;
        }

        var changed = false;
        foreach (var session in sessions)
        {
            if (devices.Any(d => d.IsCrossSigned && d.UserId == session.SenderUserId && d.Curve25519 == session.SenderKey))
            {
                session.SenderCrossSigned = true;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Call for every membership change, so key sharing uses current members and rotates after a leave.</summary>
    public void OnMembershipChanged(string roomId, Membership membership)
    {
        Interlocked.Increment(ref _membershipVersion);
        _roomMembers.TryRemove(roomId, out _);
        if (membership is Membership.Leave or Membership.Ban && _outboundSessions.TryRemove(roomId, out _))
            Logger.LogDebug("Discarded the outbound Megolm session for {RoomId} after a member left.", roomId);
    }

    private async Task<bool> ReplenishKeysAsync(Dictionary<string, int>? counts, List<string>? unusedFallbackKeyTypes)
    {
        var count = counts?.GetValueOrDefault(SignedCurve25519) ?? 0;
        var fallbackUsed = unusedFallbackKeyTypes != null && !unusedFallbackKeyTypes.Contains(SignedCurve25519);
        if (count >= OneTimeKeyTarget / 2 && !fallbackUsed)
            return false;

        var missing = OneTimeKeyTarget - count - Account.UnpublishedOneTimeKeys().Count;
        if (missing > 0)
            Account.GenerateOneTimeKeys(missing);
        if (fallbackUsed && Account.UnpublishedFallbackKey() == null)
            Account.GenerateFallbackKey();
        Account.ForgetOldestOneTimeKeys(MaxStoredOneTimeKeys);
        try
        {
            await UploadKeysAsync(includeDeviceKeys: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MatrixResponseException or HttpRequestException)
        {
            Logger.LogWarning(ex, "Uploading one-time keys failed. Retrying after the next sync.");
        }
        return true;
    }

    /// <summary>Saves the private keys first, then uploads everything not yet published.</summary>
    private async Task UploadKeysAsync(bool includeDeviceKeys)
    {
        _store.Save();
        var body = new JsonObject();
        if (includeDeviceKeys)
            body["device_keys"] = CreateDeviceKeys();

        var oneTimeKeys = new JsonObject();
        foreach (var (keyId, publicKey) in Account.UnpublishedOneTimeKeys())
            oneTimeKeys[$"{SignedCurve25519}:{keyId}"] = CreateSignedKey(publicKey, fallback: false);
        body["one_time_keys"] = oneTimeKeys;
        if (Account.UnpublishedFallbackKey() is { } fallback)
            body["fallback_keys"] = new JsonObject { [$"{SignedCurve25519}:{fallback.KeyId}"] = CreateSignedKey(fallback.PublicKey, fallback: true) };

        await RequestAsync("Uploading encryption keys", "/_matrix/client/v3/keys/upload", HttpMethod.Post, body, retry: true).ConfigureAwait(false);
        Account.MarkKeysAsPublished();
        _store.Save();
    }

    private JsonObject CreateDeviceKeys()
    {
        var keys = new JsonObject
        {
            ["user_id"] = _userId,
            ["device_id"] = _deviceId,
            ["algorithms"] = new JsonArray(OlmAlgorithm, MegolmAlgorithm),
            ["keys"] = new JsonObject { [$"curve25519:{_deviceId}"] = IdentityKey, [$"ed25519:{_deviceId}"] = SigningKey }
        };
        CanonicalJson.Sign(keys, _userId, $"ed25519:{_deviceId}", data => Account.Sign(data));
        return keys;
    }

    private JsonObject CreateSignedKey(byte[] publicKey, bool fallback)
    {
        var key = new JsonObject { ["key"] = CryptoPrimitives.ToBase64(publicKey) };
        if (fallback)
            key["fallback"] = true;
        CanonicalJson.Sign(key, _userId, $"ed25519:{_deviceId}", data => Account.Sign(data));
        return key;
    }

    /// <summary>Decrypts and validates an Olm to-device event. Returns true if stored state changed.</summary>
    private async Task<bool> HandleEncryptedToDeviceAsync(string? sender, JsonElement content)
    {
        if (sender == null || GetString(content, "algorithm") != OlmAlgorithm || GetString(content, "sender_key") is not { } senderKey ||
            !content.TryGetProperty("ciphertext", out var ciphertexts) || ciphertexts.ValueKind != JsonValueKind.Object ||
            !ciphertexts.TryGetProperty(IdentityKey, out var ciphertext) || GetString(ciphertext, "body") is not { } body ||
            !ciphertext.TryGetProperty("type", out var typeElement) || !typeElement.TryGetInt32(out var type))
        {
            Logger.LogWarning("Ignoring an encrypted to-device event from {Sender} that is not Olm or not for this device.", sender);
            return false;
        }

        var plaintext = DecryptOlm(senderKey, type, body);
        if (plaintext == null)
        {
            Logger.LogWarning("Could not decrypt an Olm message from {Sender} ({SenderKey}).", sender, senderKey);
            return false;
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(plaintext);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Olm message from {Sender} contains invalid JSON.", sender);
            return true;
        }

        if (await IsValidOlmPayloadAsync(payload, sender, senderKey).ConfigureAwait(false) is not { } device)
            return true;

        var eventType = GetString(payload, "type");
        if (eventType == "m.room_key" && payload.TryGetProperty("content", out var roomKey))
            HandleRoomKey(sender, senderKey, roomKey, device.IsCrossSigned);
        else if (eventType != "m.dummy")
            Logger.LogDebug("Ignoring encrypted to-device event {Type} from {Sender}.", eventType, sender);
        return true;
    }

    private byte[]? DecryptOlm(string senderKey, int type, string body)
    {
        var sessions = _store.Data.OlmSessions.GetValueOrDefault(senderKey) ?? [];
        foreach (var session in sessions.OrderByDescending(s => s.LastUsedAt))
        {
            if (type == 0 && !session.Matches(body))
                continue;
            try
            {
                return session.Decrypt(type, body);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Logger.LogDebug(ex, "Olm session {SessionId} could not decrypt a message.", session.SessionId);
                if (type == 0)
                    return null; // The matching session failed; a new session would fail the same way.
            }
        }
        if (type != 0)
            return null;

        try
        {
            var (newSession, plaintext) = Account.CreateInboundSession(CryptoPrimitives.FromBase64(senderKey), body);
            AddOlmSession(senderKey, newSession);
            return plaintext;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Logger.LogDebug(ex, "Could not create an Olm session from a pre-key message.");
            return null;
        }
    }

    private void AddOlmSession(string theirIdentityKey, OlmSession session)
    {
        if (!_store.Data.OlmSessions.TryGetValue(theirIdentityKey, out var sessions))
            _store.Data.OlmSessions[theirIdentityKey] = sessions = [];
        sessions.Add(session);
        if (sessions.Count > MaxOlmSessionsPerDevice)
            sessions.Remove(sessions.MinBy(s => s.LastUsedAt)!);
    }

    /// <summary>
    /// The checks from "Validation of incoming decrypted events", plus proof that the sender owns the device.
    /// Returns the sending device, or null if the message must be discarded.
    /// </summary>
    private async Task<DeviceKeys?> IsValidOlmPayloadAsync(JsonElement payload, string sender, string senderKey)
    {
        var senderSigningKey = payload.TryGetProperty("keys", out var keys) ? GetString(keys, "ed25519") : null;
        var recipientKey = payload.TryGetProperty("recipient_keys", out var recipientKeys) ? GetString(recipientKeys, "ed25519") : null;
        if (GetString(payload, "sender") != sender || GetString(payload, "recipient") != _userId ||
            recipientKey != SigningKey || senderSigningKey == null)
        {
            Logger.LogWarning("Discarding an Olm message from {Sender}: sender or recipient do not match.", sender);
            return null;
        }

        DeviceKeys? device;
        if (payload.TryGetProperty("sender_device_keys", out var senderDeviceKeys) && senderDeviceKeys.ValueKind != JsonValueKind.Null)
        {
            device = ParseDeviceKeys(senderDeviceKeys, sender, null);
            if (device == null)
            {
                Logger.LogWarning("Discarding an Olm message from {Sender}: invalid sender_device_keys.", sender);
                return null;
            }
            // The published copy may carry the cross-signature even if the sender's copy doesn't.
            var published = (await GetDevicesAsync([sender], retry: true).ConfigureAwait(false))
                .FirstOrDefault(d => d.DeviceId == device.DeviceId && d.Ed25519 == device.Ed25519);
            device = device with
            {
                IsCrossSigned = published?.IsCrossSigned == true ||
                    CrossSigning.IsDeviceCrossSigned(senderDeviceKeys, sender, device.DeviceId, _identities.GetValueOrDefault(sender))
            };
        }
        else
        {
            var devices = await GetDevicesAsync([sender], retry: true).ConfigureAwait(false);
            device = devices.FirstOrDefault(d => d.Curve25519 == senderKey);
        }

        if (device?.Curve25519 != senderKey || device.Ed25519 != senderSigningKey)
        {
            Logger.LogWarning("Discarding an Olm message from {Sender}: the sending device's keys do not match.", sender);
            return null;
        }
        return device;
    }

    private void HandleRoomKey(string sender, string senderKey, JsonElement content, bool senderCrossSigned)
    {
        if (GetString(content, "algorithm") != MegolmAlgorithm || GetString(content, "room_id") is not { } roomId ||
            GetString(content, "session_id") is not { } sessionId || GetString(content, "session_key") is not { } sessionKey)
        {
            Logger.LogWarning("Ignoring an unsupported or incomplete room key from {Sender}.", sender);
            return;
        }

        MegolmInboundSession session;
        try
        {
            session = MegolmInboundSession.FromSessionKey(sessionKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Logger.LogWarning(ex, "Ignoring an invalid room key from {Sender}.", sender);
            return;
        }
        if (session.SessionId != sessionId)
        {
            Logger.LogWarning("Ignoring a room key from {Sender} whose session ID does not match its key.", sender);
            return;
        }

        if (_store.Data.InboundGroupSessions.TryGetValue(sessionId, out var existing))
        {
            if (existing.RoomId != roomId || existing.SenderUserId != sender || existing.SenderKey != senderKey)
            {
                Logger.LogWarning("Ignoring a room key from {Sender} for a session that belongs to someone else.", sender);
                return;
            }
            if (senderCrossSigned && existing.SenderCrossSigned == false)
                existing.SenderCrossSigned = true;
            if (session.FirstKnownIndex >= existing.Session.FirstKnownIndex)
                return;
        }

        _store.Data.InboundGroupSessions[sessionId] = new InboundGroupSession
        {
            RoomId = roomId,
            SenderUserId = sender,
            SenderKey = senderKey,
            Export = session.Export(),
            // A better copy of a known session keeps the trust it had, also when it was stored before we checked.
            SenderCrossSigned = senderCrossSigned ? true : existing != null ? existing.SenderCrossSigned : false
        };
        Logger.LogDebug("Received room key {SessionId} for {RoomId} from {Sender}.", sessionId, roomId, sender);
    }

    /// <summary>
    /// Decrypts a Megolm room event. Returns null, logs a warning and sets <paramref name="failure"/>
    /// if it cannot be decrypted or fails validation.
    /// </summary>
    public ClientEventWithoutRoomID? DecryptRoomEvent(string roomId, ClientEventWithoutRoomID encrypted, out string? failure)
    {
        failure = null;
        if (encrypted.Content is not { ValueKind: JsonValueKind.Object } content ||
            GetString(content, "algorithm") != MegolmAlgorithm ||
            GetString(content, "session_id") is not { } sessionId || GetString(content, "ciphertext") is not { } ciphertext)
        {
            failure = "unsupported algorithm or missing fields";
            Logger.LogWarning("Cannot decrypt event {EventId} in {RoomId}: unsupported algorithm or missing fields.", encrypted.EventId, roomId);
            return null;
        }
        if (!_store.Data.InboundGroupSessions.TryGetValue(sessionId, out var record))
        {
            failure = _withheldSessions.TryGetValue(sessionId, out var code)
                ? $"the sender withheld the key from this device ({DescribeWithheldCode(code)})"
                : "the sender has not shared the key for it with this device";
            Logger.LogWarning("Cannot decrypt event {EventId} in {RoomId}: no key for session {SessionId}.", encrypted.EventId, roomId, sessionId);
            return null;
        }
        if (record.RoomId != roomId || record.SenderUserId != encrypted.Sender)
        {
            failure = "its key belongs to another room or sender";
            Logger.LogWarning("Discarding event {EventId} in {RoomId}: the session belongs to another room or sender.", encrypted.EventId, roomId);
            return null;
        }
        if (record.SenderCrossSigned == false)
        {
            failure = "it was sent from a device that its owner has not cross-signed";
            Logger.LogWarning("Not showing event {EventId} in {RoomId}: the sending device is not cross-signed.", encrypted.EventId, roomId);
            return null;
        }

        try
        {
            var (plaintext, index) = record.Session.Decrypt(ciphertext);
            var firstEventId = _decryptedEventIds.GetOrAdd((sessionId, index), encrypted.EventId);
            if (firstEventId != encrypted.EventId)
            {
                failure = $"it replays the content of event {firstEventId}";
                Logger.LogWarning("Discarding event {EventId} in {RoomId}: it replays message index {Index} of event {FirstEventId}.",
                    encrypted.EventId, roomId, index, firstEventId);
                return null;
            }

            var payload = JsonNode.Parse(plaintext)?.AsObject();
            if (payload?["room_id"]?.GetValue<string>() != roomId || payload["type"]?.GetValue<string>() is not { } type ||
                payload["content"] is not JsonObject decryptedContent)
            {
                failure = "the decrypted content is invalid";
                Logger.LogWarning("Discarding event {EventId} in {RoomId}: invalid decrypted payload.", encrypted.EventId, roomId);
                return null;
            }

            // Relations live in the cleartext part, so the server can aggregate them.
            decryptedContent.Remove("m.relates_to");
            if (content.TryGetProperty("m.relates_to", out var relatesTo))
                decryptedContent["m.relates_to"] = JsonNode.Parse(relatesTo.GetRawText());

            return new ClientEventWithoutRoomID
            {
                Type = type,
                Content = JsonSerializer.SerializeToElement(decryptedContent),
                Sender = encrypted.Sender,
                EventId = encrypted.EventId,
                OriginServerTs = encrypted.OriginServerTs,
                Unsigned = encrypted.Unsigned
            };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or InvalidOperationException)
        {
            failure = ex is CryptographicException ? $"decryption failed: {ex.Message.TrimEnd('.')}" : "the decrypted content is invalid";
            Logger.LogWarning(ex, "Cannot decrypt event {EventId} in {RoomId}.", encrypted.EventId, roomId);
            return null;
        }
    }

    /// <summary>
    /// Encrypts event content for a room, sharing the room key with every device of
    /// joined and invited members first. Returns the content of the m.room.encrypted event.
    /// </summary>
    public async Task<JsonObject> EncryptRoomEventAsync(Room room, string eventType, JsonObject content)
    {
        if (room.Encryption?.Algorithm != MegolmAlgorithm)
            throw new NotSupportedException(
                $"Cannot send to room {room.RoomId.Full}: its encryption algorithm '{room.Encryption?.Algorithm}' is not supported.");

        await _lock.WaitAsync(_http.CancellationToken).ConfigureAwait(false);
        try
        {
            var roomId = room.RoomId.Full;
            var members = await GetRoomMembersAsync(roomId).ConfigureAwait(false);
            var devices = await GetDevicesAsync(members, retry: false).ConfigureAwait(false);
            var trusted = devices.Where(d => d.IsCrossSigned).ToList();

            var outbound = _outboundSessions.GetValueOrDefault(roomId);
            if (outbound == null || ShouldRotate(outbound, room, trusted))
            {
                outbound = new OutboundGroupSession();
                _outboundSessions[roomId] = outbound;
                // Keep our own copy, so we can read our messages in the timeline and history.
                _store.Data.InboundGroupSessions[outbound.Session.SessionId] = new InboundGroupSession
                {
                    RoomId = roomId,
                    SenderUserId = _userId,
                    SenderKey = IdentityKey,
                    Export = MegolmInboundSession.FromSessionKey(outbound.Session.ExportSessionKey()).Export(),
                    SenderCrossSigned = true
                };
                _store.Save();
            }

            var recipients = trusted.Where(d => !outbound.SharedWith.ContainsKey(d.Id)).ToList();
            if (recipients.Count > 0)
                await ShareRoomKeyAsync(room, outbound, recipients).ConfigureAwait(false);
            var notCrossSigned = devices.Where(d => !d.IsCrossSigned && outbound.NotSharedReported.Add(d.Id)).ToList();
            if (notCrossSigned.Count > 0)
                await WithholdRoomKeyAsync(room, outbound, notCrossSigned).ConfigureAwait(false);

            var relatesTo = content["m.relates_to"]?.DeepClone();
            var payloadContent = content.DeepClone().AsObject();
            payloadContent.Remove("m.relates_to");
            var payload = new JsonObject { ["type"] = eventType, ["content"] = payloadContent, ["room_id"] = roomId };
            var encrypted = new JsonObject
            {
                ["algorithm"] = MegolmAlgorithm,
                ["sender_key"] = IdentityKey,
                ["device_id"] = _deviceId,
                ["session_id"] = outbound.Session.SessionId,
                ["ciphertext"] = outbound.Session.Encrypt(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            };
            if (relatesTo != null)
                encrypted["m.relates_to"] = relatesTo;
            return encrypted;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool ShouldRotate(OutboundGroupSession outbound, Room room, List<DeviceKeys> devices)
    {
        long periodMs = 604_800_000;
        long periodMessages = 100;
        if (room.StateEvents.TryGetValue(("m.room.encryption", ""), out var state) && state.Content.ValueKind == JsonValueKind.Object)
        {
            if (state.Content.TryGetProperty("rotation_period_ms", out var ms) && ms.TryGetInt64(out var msValue) && msValue > 0)
                periodMs = msValue;
            if (state.Content.TryGetProperty("rotation_period_msgs", out var msgs) && msgs.TryGetInt64(out var msgsValue) && msgsValue > 0)
                periodMessages = msgsValue;
        }
        if (outbound.Session.MessageIndex >= periodMessages || DateTimeOffset.UtcNow - outbound.CreatedAt >= TimeSpan.FromMilliseconds(periodMs))
            return true;

        // A device that received the key but is gone, has new keys or lost its cross-signature must not read future messages.
        var current = devices.ToDictionary(d => d.Id, d => d.Curve25519);
        return outbound.SharedWith.Any(shared => current.GetValueOrDefault(shared.Key) != shared.Value);
    }

    private async Task ShareRoomKeyAsync(Room room, OutboundGroupSession outbound, List<DeviceKeys> recipients)
    {
        var roomId = room.RoomId.Full;
        var withoutSession = recipients.Where(d => !_store.Data.OlmSessions.TryGetValue(d.Curve25519, out var s) || s.Count == 0).ToList();
        if (withoutSession.Count > 0)
            await CreateOlmSessionsAsync(withoutSession).ConfigureAwait(false);

        var roomKey = new JsonObject
        {
            ["algorithm"] = MegolmAlgorithm,
            ["room_id"] = roomId,
            ["session_id"] = outbound.Session.SessionId,
            ["session_key"] = outbound.Session.ExportSessionKey()
        };
        var ownDeviceKeys = _crossSignedDeviceKeys ?? CreateDeviceKeys();
        var messages = new List<(DeviceKeys Device, JsonObject Content)>();
        foreach (var device in recipients)
        {
            if (!_store.Data.OlmSessions.TryGetValue(device.Curve25519, out var sessions) || sessions.Count == 0)
            {
                Logger.LogWarning("Cannot share the room key for {RoomId} with device {DeviceId} of {UserId}: no one-time key available.",
                    roomId, device.DeviceId, device.UserId);
                // Sharing is retried on every send; report each device once per session.
                if (outbound.NotSharedReported.Add(device.Id))
                {
                    room.Client.ReportIssue(ClientIssueKind.RoomKeyNotShared,
                        $"Device {device.DeviceId} of {device.UserId} cannot read our messages in room {roomId}: " +
                        "could not set up an encrypted channel to it.", room, device.UserId);
                }
                continue;
            }
            var payload = new JsonObject
            {
                ["type"] = "m.room_key",
                ["content"] = roomKey.DeepClone(),
                ["sender"] = _userId,
                ["sender_device"] = _deviceId,
                ["keys"] = new JsonObject { ["ed25519"] = SigningKey },
                ["recipient"] = device.UserId,
                ["recipient_keys"] = new JsonObject { ["ed25519"] = device.Ed25519 },
                ["sender_device_keys"] = ownDeviceKeys.DeepClone()
            };
            var (type, body) = sessions.MaxBy(s => s.LastUsedAt)!.Encrypt(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            messages.Add((device, new JsonObject
            {
                ["algorithm"] = OlmAlgorithm,
                ["sender_key"] = IdentityKey,
                ["ciphertext"] = new JsonObject { [device.Curve25519] = new JsonObject { ["type"] = type, ["body"] = body } }
            }));
        }
        // Advanced Olm sessions must be on disk before their messages leave.
        _store.Save();

        foreach (var batch in messages.Chunk(ToDeviceBatchSize))
        {
            await RequestAsync("Sharing a room key", $"/_matrix/client/v3/sendToDevice/m.room.encrypted/{_http.NextTransactionId()}",
                HttpMethod.Put, CreateToDeviceBody(batch), retry: false).ConfigureAwait(false);
            foreach (var (device, _) in batch)
                outbound.SharedWith[device.Id] = device.Curve25519;
        }
    }

    /// <summary>
    /// Reports devices that don't get the room key because their owner hasn't cross-signed them,
    /// and tells them why with <c>m.room_key.withheld</c>.
    /// </summary>
    private async Task WithholdRoomKeyAsync(Room room, OutboundGroupSession outbound, List<DeviceKeys> devices)
    {
        var roomId = room.RoomId.Full;
        var content = new JsonObject
        {
            ["algorithm"] = MegolmAlgorithm,
            ["room_id"] = roomId,
            ["session_id"] = outbound.Session.SessionId,
            ["sender_key"] = IdentityKey,
            ["code"] = UnverifiedCode,
            ["reason"] = "The sender only shares keys with devices that their owner cross-signed."
        };
        foreach (var device in devices)
        {
            Logger.LogInformation("Not sharing the room key for {RoomId} with device {DeviceId} of {UserId}: not cross-signed.",
                roomId, device.DeviceId, device.UserId);
            room.Client.ReportIssue(ClientIssueKind.RoomKeyNotShared,
                $"Device {device.DeviceId} of {device.UserId} cannot read our messages in room {roomId}: " +
                "its owner has not cross-signed it.", room, device.UserId);
        }

        foreach (var batch in devices.Chunk(ToDeviceBatchSize))
        {
            try
            {
                await RequestAsync("Withholding a room key", $"/_matrix/client/v3/sendToDevice/m.room_key.withheld/{_http.NextTransactionId()}",
                    HttpMethod.Put, CreateToDeviceBody(batch.Select(d => (d, content))), retry: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MatrixResponseException or HttpRequestException)
            {
                // Only informational for the other side; the message itself can still be sent.
                Logger.LogWarning(ex, "Could not tell devices in {RoomId} why they don't get the room key.", roomId);
            }
        }
    }

    private static JsonObject CreateToDeviceBody(IEnumerable<(DeviceKeys Device, JsonObject Content)> messages)
    {
        var body = new JsonObject();
        foreach (var (device, content) in messages)
        {
            if (body[device.UserId] is not JsonObject userMessages)
                body[device.UserId] = userMessages = [];
            userMessages[device.DeviceId] = content.DeepClone();
        }
        return new JsonObject { ["messages"] = body };
    }

    private async Task CreateOlmSessionsAsync(List<DeviceKeys> devices)
    {
        var request = new JsonObject();
        foreach (var device in devices)
        {
            if (request[device.UserId] is not JsonObject userRequest)
                request[device.UserId] = userRequest = [];
            userRequest[device.DeviceId] = SignedCurve25519;
        }
        var response = await RequestAsync("Claiming one-time keys", "/_matrix/client/v3/keys/claim", HttpMethod.Post,
            new JsonObject { ["one_time_keys"] = request }, retry: false).ConfigureAwait(false);
        if (!response.TryGetProperty("one_time_keys", out var claimed) || claimed.ValueKind != JsonValueKind.Object)
            return;

        foreach (var device in devices)
        {
            if (!claimed.TryGetProperty(device.UserId, out var userKeys) || userKeys.ValueKind != JsonValueKind.Object ||
                !userKeys.TryGetProperty(device.DeviceId, out var deviceKeys) || deviceKeys.ValueKind != JsonValueKind.Object)
                continue;
            var key = deviceKeys.EnumerateObject().FirstOrDefault(p => p.Name.StartsWith(SignedCurve25519 + ":", StringComparison.Ordinal));
            if (key.Value.ValueKind != JsonValueKind.Object || GetString(key.Value, "key") is not { } oneTimeKey ||
                !CanonicalJson.Verify(key.Value, device.UserId, $"ed25519:{device.DeviceId}", CryptoPrimitives.FromBase64(device.Ed25519)))
            {
                Logger.LogWarning("Ignoring an invalid one-time key for device {DeviceId} of {UserId}.", device.DeviceId, device.UserId);
                continue;
            }
            try
            {
                var session = Account.CreateOutboundSession(CryptoPrimitives.FromBase64(device.Curve25519), CryptoPrimitives.FromBase64(oneTimeKey));
                AddOlmSession(device.Curve25519, session);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Logger.LogWarning(ex, "Could not create an Olm session with device {DeviceId} of {UserId}.", device.DeviceId, device.UserId);
            }
        }
    }

    private async Task<List<string>> GetRoomMembersAsync(string roomId)
    {
        if (_roomMembers.TryGetValue(roomId, out var cached))
            return cached;

        var version = Volatile.Read(ref _membershipVersion);
        var response = await RequestAsync("Fetching room members",
            $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/members?not_membership=leave", HttpMethod.Get, null, retry: false).ConfigureAwait(false);
        var members = new List<string>();
        if (response.TryGetProperty("chunk", out var chunk) && chunk.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in chunk.EnumerateArray())
            {
                var membership = member.TryGetProperty("content", out var memberContent) ? GetString(memberContent, "membership") : null;
                if (membership is "join" or "invite" && GetString(member, "state_key") is { } userId)
                    members.Add(userId);
            }
        }
        // A membership change during the request makes this list stale; use it once, but don't cache it.
        if (Volatile.Read(ref _membershipVersion) == version)
            _roomMembers[roomId] = members;
        return members;
    }

    /// <summary>All devices of the given users with valid keys, except this device. Also loads the users' cross-signing keys.</summary>
    private async Task<List<DeviceKeys>> GetDevicesAsync(IEnumerable<string> users, bool retry)
    {
        var userList = users.Distinct().ToList();
        var missing = userList.Where(u => !_devices.ContainsKey(u)).ToList();
        if (missing.Count > 0)
        {
            var query = new JsonObject();
            foreach (var user in missing)
                query[user] = new JsonArray();
            var response = await RequestAsync("Querying device keys", "/_matrix/client/v3/keys/query", HttpMethod.Post,
                new JsonObject { ["device_keys"] = query, ["timeout"] = 10000 }, retry).ConfigureAwait(false);
            if (response.TryGetProperty("device_keys", out var deviceKeys) && deviceKeys.ValueKind == JsonValueKind.Object)
            {
                foreach (var user in missing)
                {
                    if (!deviceKeys.TryGetProperty(user, out var userDevices) || userDevices.ValueKind != JsonValueKind.Object)
                        continue; // Unreachable server: query again next time.
                    var identity = CrossSigning.ReadIdentity(response, user);
                    if (identity != null)
                        PinMasterKey(user, identity.MasterKey);
                    _identities[user] = identity;
                    var devices = new Dictionary<string, DeviceKeys>();
                    foreach (var property in userDevices.EnumerateObject())
                    {
                        if (ParseDeviceKeys(property.Value, user, property.Name) is not { } device)
                        {
                            Logger.LogWarning("Ignoring device {DeviceId} of {UserId}: invalid or unsigned device keys.", property.Name, user);
                            continue;
                        }
                        // Device keys never change. A different key for a known device is suspicious.
                        if (_pinnedSigningKeys.TryGetValue(device.Id, out var pinned) && pinned != device.Ed25519)
                        {
                            Logger.LogWarning("Ignoring device {DeviceId} of {UserId}: its signing key changed.", device.DeviceId, user);
                            continue;
                        }
                        _pinnedSigningKeys[device.Id] = device.Ed25519;
                        devices[device.DeviceId] = device with
                        {
                            IsCrossSigned = CrossSigning.IsDeviceCrossSigned(property.Value, user, device.DeviceId, identity)
                        };
                    }
                    _devices[user] = devices;
                }
            }
        }

        return userList.SelectMany(u => _devices.GetValueOrDefault(u)?.Values ?? Enumerable.Empty<DeviceKeys>())
            .Where(d => !(d.UserId == _userId && d.DeviceId == _deviceId))
            .ToList();
    }

    private static DeviceKeys? ParseDeviceKeys(JsonElement value, string userId, string? deviceId)
    {
        if (value.ValueKind != JsonValueKind.Object || GetString(value, "user_id") != userId ||
            GetString(value, "device_id") is not { } actualDeviceId || (deviceId != null && actualDeviceId != deviceId) ||
            !value.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object ||
            GetString(keys, $"ed25519:{actualDeviceId}") is not { } ed25519 ||
            GetString(keys, $"curve25519:{actualDeviceId}") is not { } curve25519)
            return null;
        try
        {
            if (CryptoPrimitives.FromBase64(curve25519).Length != 32 ||
                !CanonicalJson.Verify(value, userId, $"ed25519:{actualDeviceId}", CryptoPrimitives.FromBase64(ed25519)))
                return null;
        }
        catch (FormatException)
        {
            return null;
        }
        return new DeviceKeys(userId, actualDeviceId, curve25519, ed25519);
    }

    private Task<JsonElement> RequestAsync(string operation, string path, HttpMethod method, JsonNode? body, bool retry)
    {
        // Key requests only happen alongside syncs and sends, which are limited already.
        Task<JsonElement> Send() => HttpClientHelper.SendAsync<JsonElement>(_http, path, method,
            body == null ? null : JsonContent.Create(body), ignoreRateLimit: true);
        return retry ? MatrixRetry.ExecuteAsync(_http, operation, Send) : Send();
    }

    private static string DescribeWithheldCode(string code) => code switch
    {
        UnverifiedCode => "m.unverified: the sender only shares keys with devices that their owner cross-signed",
        _ => code
    };

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
