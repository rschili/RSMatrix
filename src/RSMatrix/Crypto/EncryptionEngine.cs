using System.Collections.Concurrent;
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
internal sealed record DeviceKeys(string UserId, string DeviceId, string Curve25519, string Ed25519)
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
/// End-to-end encryption for one device: key upload, receiving room keys over Olm,
/// decrypting and encrypting room events with Megolm.
/// </summary>
internal sealed class EncryptionEngine : IDisposable
{
    internal const string OlmAlgorithm = "m.olm.v1.curve25519-aes-sha2";
    internal const string MegolmAlgorithm = "m.megolm.v1.aes-sha2";
    private const string SignedCurve25519 = "signed_curve25519";
    private const int OneTimeKeyTarget = 50;
    private const int MaxStoredOneTimeKeys = 100;
    private const int MaxOlmSessionsPerDevice = 10;
    private const int ToDeviceBatchSize = 100;

    private readonly EncryptionStore _store;
    private readonly HttpClientParameters _http;
    private readonly string _userId;
    private readonly string _deviceId;
    private ILogger Logger => _http.Logger;

    // Olm sessions, the account, device lists and outbound sessions change under this lock.
    // Decryption of room events only reads inbound sessions and does not need it.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, DeviceKeys>> _devices = [];
    private readonly Dictionary<string, string> _pinnedSigningKeys = [];
    private readonly ConcurrentDictionary<string, OutboundGroupSession> _outboundSessions = new();
    private readonly ConcurrentDictionary<string, List<string>> _roomMembers = new();
    private int _membershipVersion;
    private readonly ConcurrentDictionary<(string SessionId, uint Index), string> _decryptedEventIds = new();

    private EncryptionEngine(EncryptionStore store, HttpClientParameters http)
    {
        _store = store;
        _http = http;
        _userId = store.Data.UserId;
        _deviceId = store.Data.DeviceId;
    }

    private OlmAccount Account => _store.Data.Account;
    private string IdentityKey => CryptoPrimitives.ToBase64(Account.IdentityKey);
    private string SigningKey => CryptoPrimitives.ToBase64(Account.SigningKey);

    /// <summary>Opens the store before logging in, so a wrong directory fails before creating a session.</summary>
    public static EncryptionStore OpenStore(string directory, string userId, string deviceId)
        => EncryptionStore.Open(directory, userId, deviceId);

    /// <summary>Publishes our device keys on first use and checks that the server agrees with the store.</summary>
    public static async Task<EncryptionEngine> StartAsync(EncryptionStore store, HttpClientParameters http)
    {
        var engine = new EncryptionEngine(store, http);
        await engine.PublishDeviceKeysAsync().ConfigureAwait(false);
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

    /// <summary>Processes the encryption parts of a sync response. Runs before room events are handled.</summary>
    public async Task HandleSyncAsync(SyncResponse response)
    {
        await _lock.WaitAsync(_http.CancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var user in response.DeviceLists?.Changed ?? [])
                _devices.Remove(user);
            foreach (var user in response.DeviceLists?.Left ?? [])
                _devices.Remove(user);

            var changed = false;
            foreach (var ev in response.ToDevice?.Events ?? [])
            {
                if (ev?.Type == "m.room.encrypted" && ev.Content is { ValueKind: JsonValueKind.Object } content)
                    changed |= await HandleEncryptedToDeviceAsync(ev.Sender, content).ConfigureAwait(false);
            }
            changed |= await ReplenishKeysAsync(response.DeviceOneTimeKeysCount, response.DeviceUnusedFallbackKeyTypes).ConfigureAwait(false);
            if (changed)
                _store.Save();
        }
        finally
        {
            _lock.Release();
        }
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

        if (!await IsValidOlmPayloadAsync(payload, sender, senderKey).ConfigureAwait(false))
            return true;

        var eventType = GetString(payload, "type");
        if (eventType == "m.room_key" && payload.TryGetProperty("content", out var roomKey))
            HandleRoomKey(sender, senderKey, roomKey);
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

    /// <summary>The checks from "Validation of incoming decrypted events", plus proof that the sender owns the device.</summary>
    private async Task<bool> IsValidOlmPayloadAsync(JsonElement payload, string sender, string senderKey)
    {
        var senderSigningKey = payload.TryGetProperty("keys", out var keys) ? GetString(keys, "ed25519") : null;
        var recipientKey = payload.TryGetProperty("recipient_keys", out var recipientKeys) ? GetString(recipientKeys, "ed25519") : null;
        if (GetString(payload, "sender") != sender || GetString(payload, "recipient") != _userId ||
            recipientKey != SigningKey || senderSigningKey == null)
        {
            Logger.LogWarning("Discarding an Olm message from {Sender}: sender or recipient do not match.", sender);
            return false;
        }

        DeviceKeys? device;
        if (payload.TryGetProperty("sender_device_keys", out var senderDeviceKeys) && senderDeviceKeys.ValueKind != JsonValueKind.Null)
        {
            device = ParseDeviceKeys(senderDeviceKeys, sender, null);
            if (device == null)
            {
                Logger.LogWarning("Discarding an Olm message from {Sender}: invalid sender_device_keys.", sender);
                return false;
            }
        }
        else
        {
            var devices = await GetDevicesAsync([sender], retry: true).ConfigureAwait(false);
            device = devices.FirstOrDefault(d => d.Curve25519 == senderKey);
        }

        if (device?.Curve25519 != senderKey || device.Ed25519 != senderSigningKey)
        {
            Logger.LogWarning("Discarding an Olm message from {Sender}: the sending device's keys do not match.", sender);
            return false;
        }
        return true;
    }

    private void HandleRoomKey(string sender, string senderKey, JsonElement content)
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
            if (session.FirstKnownIndex >= existing.Session.FirstKnownIndex)
                return;
        }

        _store.Data.InboundGroupSessions[sessionId] = new InboundGroupSession
        {
            RoomId = roomId,
            SenderUserId = sender,
            SenderKey = senderKey,
            Export = session.Export()
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
            failure = "the sender has not shared the key for it with this device";
            Logger.LogWarning("Cannot decrypt event {EventId} in {RoomId}: no key for session {SessionId}.", encrypted.EventId, roomId, sessionId);
            return null;
        }
        if (record.RoomId != roomId || record.SenderUserId != encrypted.Sender)
        {
            failure = "its key belongs to another room or sender";
            Logger.LogWarning("Discarding event {EventId} in {RoomId}: the session belongs to another room or sender.", encrypted.EventId, roomId);
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

            var outbound = _outboundSessions.GetValueOrDefault(roomId);
            if (outbound == null || ShouldRotate(outbound, room, devices))
            {
                outbound = new OutboundGroupSession();
                _outboundSessions[roomId] = outbound;
                // Keep our own copy, so we can read our messages in the timeline and history.
                _store.Data.InboundGroupSessions[outbound.Session.SessionId] = new InboundGroupSession
                {
                    RoomId = roomId,
                    SenderUserId = _userId,
                    SenderKey = IdentityKey,
                    Export = MegolmInboundSession.FromSessionKey(outbound.Session.ExportSessionKey()).Export()
                };
                _store.Save();
            }

            var recipients = devices.Where(d => !outbound.SharedWith.ContainsKey(d.Id)).ToList();
            if (recipients.Count > 0)
                await ShareRoomKeyAsync(room, outbound, recipients).ConfigureAwait(false);

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

        // A device that received the key but is gone or has new keys must not read future messages.
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
        var ownDeviceKeys = CreateDeviceKeys();
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
            var body = new JsonObject();
            foreach (var (device, message) in batch)
            {
                if (body[device.UserId] is not JsonObject userMessages)
                    body[device.UserId] = userMessages = [];
                userMessages[device.DeviceId] = message;
            }
            await RequestAsync("Sharing a room key", $"/_matrix/client/v3/sendToDevice/m.room.encrypted/{_http.NextTransactionId()}",
                HttpMethod.Put, new JsonObject { ["messages"] = body }, retry: false).ConfigureAwait(false);
            foreach (var (device, _) in batch)
                outbound.SharedWith[device.Id] = device.Curve25519;
        }
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

    /// <summary>All verified devices of the given users, except this device.</summary>
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
                        devices[device.DeviceId] = device;
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

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
