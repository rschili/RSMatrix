using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Models;
using RSMatrix.Http;
using System.Reflection.Metadata;
using System.Collections.Concurrent;
using System.Text.Json;

namespace RSMatrix;

/// <summary>
/// The low level class for interacting with the Matrix server.
/// </summary>
public sealed class MatrixTextClient
{
    internal MatrixClientCore Core { get; private init; }
    private ILogger Logger => Core.Logger;
    public MatrixId CurrentUser => Core.User;

    private ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    private ConcurrentDictionary<string, User> _users = new(StringComparer.OrdinalIgnoreCase);

    public delegate Task MessageHandler(ReceivedTextMessage message);

    private MessageHandler? _messageHandler = null; // We use this to track if the sync has been started

    public bool DebugMode = false;

    private MatrixTextClient(MatrixClientCore core)
    {
        Core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public static async Task<MatrixTextClient> ConnectAsync(string userId, string password, string deviceId, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken, ILogger? logger = null)
    {
        var core = await MatrixClientCore.ConnectAsync(userId, password, deviceId, httpClientFactory, cancellationToken, logger).ConfigureAwait(false);
        var client = new MatrixTextClient(core);
        return client;
    }

    public async Task SyncAsync(MessageHandler handler)
    {
        if(_messageHandler != null)
            throw new InvalidOperationException("Sync can only be started once.");

        _messageHandler = handler ?? throw new ArgumentNullException(nameof(handler));

        // We only want to receive text messages, filter out spam we're not interested in
        Filter filter = new()
        {
            Room = new()
            {
                Ephemeral = new()
                {
                    NotTypes = new() { "m.typing", "m.receipt" },
                    //LazyLoadMembers = true
                },
                Timeline = new()
                {
                    //LazyLoadMembers = true,
                },
                State = new()
                {
                    NotTypes = new() { "m.room.join_rules", "m.room.guest_access", "m.room.avatar", "m.room.history_visibility", "m.room.power_levels", "im.vector.modular.widgets" },
                    LazyLoadMembers = true
                },
            }
        };
        filter = await Core.SetFilterAsync(filter).ConfigureAwait(false);
        if(filter.FilterId == null)
            Logger.LogWarning("No filter ID was returned after setting a filter. This should not happen. It won't break the client, but unnecessary events will be received.");
        
        await Core.SyncAsync(HandleSyncResponseAsync).ConfigureAwait(false);
    }

    private async Task WriteSyncResponseToFileAsync(SyncResponse response)
    {
        if(response == null)
            return;

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "matrix");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"sync_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
            using var stream = File.OpenWrite(path);
            await JsonSerializer.SerializeAsync(stream, response).ConfigureAwait(false);
            Logger.LogInformation("Sync response written to {Path}", path);
        }
        catch(Exception ex)
        {
            Logger.LogError(ex, "Error writing sync response to file.");
        }
    }


    private LeakyBucketRateLimiter _receiptRateLimiter = new LeakyBucketRateLimiter(5, 120);

    private async Task HandleSyncResponseAsync(SyncResponse response)
    {
        if (response == null)
            return;

        if(DebugMode)
            await WriteSyncResponseToFileAsync(response).ConfigureAwait(false);

        List<ReceivedTextMessage> messages = new();

        try
        {
            if (response.AccountData != null && response.AccountData.Events != null)
            {
                HandleAccountDataReceived(null, response.AccountData.Events);
            }

            if (response.Presence != null && response.Presence.Events != null)
            {
                HandlePresenceReceived(response.Presence.Events);
            }

            if (response.Rooms != null)
            {
                if (response.Rooms.Joined != null)
                {
                    foreach (var pair in response.Rooms.Joined)
                    {
                        var roomIdString = pair.Key;
                        if (!RoomId.TryParse(roomIdString, out var roomId) || roomId == null)
                        {
                            Logger.LogWarning("Received joined room event with invalid room ID: {roomId}", roomIdString);
                            continue;
                        }

                        if(pair.Value.Summary != null)
                            HandleRoomSummaryReceived(roomId, pair.Value.Summary);

                        if(pair.Value.AccountData != null && pair.Value.AccountData.Events != null)
                            HandleAccountDataReceived(roomId, pair.Value.AccountData.Events);

                        if(pair.Value.Ephemeral != null && pair.Value.Ephemeral.Events != null)
                            HandleEphemeralReceived(roomId, pair.Value.Ephemeral.Events);

                        if(pair.Value.State != null && pair.Value.State.Events != null)
                            HandleStateReceived(roomId, pair.Value.State.Events);

                        if(pair.Value.Timeline != null && pair.Value.Timeline.Events != null)
                        {
                            HandleTimelineReceived(roomId, pair.Value.Timeline.Events, messages);
                        }
                    } // foreach joined room
                } // if joined
            } // if rooms

            if(messages.Count > 0)
            {
                foreach(var message in messages)
                {
                    try
                    {
                        await _messageHandler!(message).ConfigureAwait(false);
                    }
                    catch(TaskCanceledException)
                    {
                        throw;
                    }
                    catch(Exception ex)
                    {
                        Logger.LogError(ex, "Error during handling of message {MessageId}.", message.Body);
                    }
                }
            }

            // process receipts
            foreach(var room in _rooms.Values)
            {
                if(room.LastMessage == null)
                    continue;

                if(room.LastMessage.EventId != room.LastReceiptEventId)
                {
                    if(_receiptRateLimiter.Leak())
                    { // This is not thread safe, we may mix receipts, but that's not a big deal
                        await room.LastMessage.SendReceiptAsync().ConfigureAwait(false);
                        room.LastReceiptEventId = room.LastMessage.EventId;
                    }
                }
            }
        }
        catch(TaskCanceledException)
        {
            Logger.LogInformation("Sync was cancelled.");
            throw;
        }
        catch(Exception ex)
        { // we only allow TaskCanceledException to bubble up
            Logger.LogError(ex, "Error during handling of message.");
        }
    }

    private void HandleRoomSummaryReceived(MatrixId roomId, RoomSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(summary, nameof(summary));

        var users = summary?.Heroes?.Select(s => UserId.TryParse(s, out MatrixId? userId) ? userId : null)
            ?.Where(id => id != null)?.Select(id => id!)
            ?.Select(GetOrAddUser)?.ToList();

        var room = GetOrAddRoom(roomId);

        if(users == null || users.Count == 0)
            return;

        foreach (var user in users)
        {
            var roomUser = GetOrAddUser(user, room);
        }
    }

    private void HandlePresenceReceived(List<MatrixEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        foreach(var ev in events)
        {
            if(ev.Type != "m.presence")
            {
                Logger.LogWarning("Received event of type {Type} in presence events.", ev.Type);
                continue;
            }

            if(ev.Content == null)
            {
                Logger.LogWarning("Received presence event with no content.");
                continue;
            }

            var userId = UserId.TryParse(ev.Sender, out MatrixId? id) ? id : null;
            if(userId == null)
            {
                Logger.LogWarning("Received presence event with invalid sender ID: {Sender}", ev.Sender);
                continue;
            }
            var user = GetOrAddUser(userId);

            var parsedPresence = JsonSerializer.Deserialize<PresenceEvent>((JsonElement)ev.Content);
            if(parsedPresence == null)
            {
                Logger.LogWarning("Received presence event with no valid content.");
                continue;
            }

            lock(user)
            {
                if(parsedPresence.CurrentlyActive != null)
                    user.CurrentlyActive = parsedPresence.CurrentlyActive;

                if(parsedPresence.AvatarUrl != null)
                    user.AvatarUrl = parsedPresence.AvatarUrl;

                if(parsedPresence.DisplayName != null)
                    user.DisplayName = parsedPresence.DisplayName;

                if(parsedPresence.CurrentlyActive != null)
                    user.CurrentlyActive = parsedPresence.CurrentlyActive;

                user.Presence = parsedPresence.Presence;

                if(parsedPresence.StatusMsg != null)
                    user.StatusMessage = parsedPresence.StatusMsg;
            }
        }
    }

    
    private void HandleEphemeralReceived(MatrixId roomId, List<MatrixEvent> events)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        foreach(var e in events.Where(ev => ev.Type != "m.typing" && ev.Type != "m.receipt"))
        {
            // Just track these for now. We are not interested in typing and receipt events
            Logger.LogWarning("Received unknown Ephemeral event type in room {RoomId}: {Type}.", roomId.Full, e.Type);
        }
    }

    
    private void HandleStateReceived(MatrixId roomId, List<ClientEventWithoutRoomID> events)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        var room = GetOrAddRoom(roomId);

        foreach(var e in events)
        {
            if(e.Content == null)
            {
                Logger.LogWarning("Received state event with no content in room {RoomId}. Type {Type}", roomId.Full, e.Type);
                continue;
            }
            switch(e.Type)
            {
                case "m.room.member":
                    var roomMember = JsonSerializer.Deserialize<RoomMemberEvent>((JsonElement)e.Content);
                    if(roomMember == null)
                    {
                        Logger.LogWarning("Received m.room.member event deserialize returned null in room {RoomId}.", roomId.Full);
                        break;
                    }
                    var userIdStr = e.StateKey ?? e.Sender;
                    if(!UserId.TryParse(userIdStr, out MatrixId? userId) || userId == null)
                    {
                        Logger.LogWarning("Received m.room.member event with invalid user ID: {UserId} in room {RoomId}.", userIdStr, roomId.Full);
                        break;
                    }
                    var user = GetOrAddUser(userId);
                    RoomUser? roomUser = GetOrAddUser(user, room);

                    if(roomUser.DisplayName != roomMember.DisplayName || roomUser.Membership != roomMember.Membership)
                    { //TODO we may want to expose more properties of the room member
                        lock(roomUser)
                        {
                            roomUser.DisplayName = roomMember.DisplayName;
                            roomUser.Membership = roomMember.Membership;
                        }
                    }
                    break;
                case "m.room.name":
                    var nameEvent = JsonSerializer.Deserialize<RoomNameEvent>((JsonElement)e.Content);
                    if(nameEvent == null)
                    {
                        Logger.LogWarning("Received m.room.name event deserialize returned null in room {RoomId}.", roomId.Full);
                        break;
                    }
                    
                    lock(room)
                    {
                        room.DisplayName = nameEvent.Name;
                    }
                    break;
                case "m.room.canonical_alias":
                    var parsed = JsonSerializer.Deserialize<CanonicalAliasEvent>((JsonElement)e.Content);
                    RoomAlias.TryParse(parsed?.Alias, out MatrixId? alias);
                    var altAliases = parsed?.AltAliases?.Select(a => RoomAlias.TryParse(a, out MatrixId? id) ? id : null).Where(id => id != null).Select(id => id!).ToList();
                    lock(room)
                    {
                        room.CanonicalAlias = alias;
                        if(altAliases != null)
                            room.AltAliases = room.AltAliases?.Union(altAliases).ToList() ?? altAliases;
                    }
                    break;
                case "m.room.power_levels":
                case "m.room.join_rules":
                case "m.room.topic":
                case "m.room.avatar":
                    // We don't care about these
                    break;
                default:
                    Logger.LogWarning("Received unknown state event type in room {RoomId}: {Type}.", roomId.Full, e.Type);
                    break;
            }
        }
    }

    private void HandleTimelineReceived(MatrixId roomId, List<ClientEventWithoutRoomID> events, List<ReceivedTextMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        ArgumentNullException.ThrowIfNull(messages, nameof(messages));
        var room = GetOrAddRoom(roomId);

        foreach(var e in events)
        {
            if(e.Type == "m.room.message")
                HandleMessageReceived(roomId, messages, room, e);
            else if(e.Type == "m.room.encryption")
                HandleRoomEncryptionEvent(room, e);
            else if(e.Type == "m.room.encrypted")
                HandleEncryptedEvent(room, e);
            else
            {
                Logger.LogWarning("Received unknown timeline event type in room {RoomId}: {Type}.", roomId.Full, e.Type);
            }
        }
    }

    private void HandleEncryptedEvent(Room room, ClientEventWithoutRoomID e)
    {
        ArgumentNullException.ThrowIfNull(room, nameof(room));
        ArgumentNullException.ThrowIfNull(e, nameof(e));

        if(e.Content == null)
        {
            Logger.LogWarning("Received m.room.encrypted event with no content in room {RoomId}.", room.RoomId.Full);
            return;
        }

        var encryptedEvent = JsonSerializer.Deserialize<RoomEncryptedEvent>((JsonElement)e.Content);
        if(encryptedEvent == null)
        {
            Logger.LogWarning("Received m.room.encrypted event deserialize returned null in room {RoomId}.", room.RoomId.Full);
            return;
        }

        if(encryptedEvent.Algorithm != room.Encryption?.Algorithm)
        {
            Logger.LogWarning("Received m.room.encrypted event with mismatching algorithm for room {RoomId}. Expected: {ExpectedAlgorithm}, Received: {ReceivedAlgorithm}", room.RoomId.Full, room.Encryption?.Algorithm, encryptedEvent.Algorithm);
            return;
        }

        // TODO handle ciphertext
    }

    private void HandleRoomEncryptionEvent(Room room, ClientEventWithoutRoomID e)
    {
        if(e.Content == null)
        {
            Logger.LogWarning("Received m.room.encryption event with no content in room {RoomId}.", room.RoomId.Full);
            return;
        }

        var encryptionEvent = JsonSerializer.Deserialize<RoomEncryptionEvent>((JsonElement)e.Content);
        if(encryptionEvent == null)
        {
            Logger.LogWarning("Received m.room.encryption event deserialize returned null in room {RoomId}.", room.RoomId.Full);
            return;
        }

        if(encryptionEvent.Algorithm != "m.megolm.v1.aes-sha2")
        {
            Logger.LogWarning("Received m.room.encryption event with unknown algorithm {Algorithm} in room {RoomId}.", encryptionEvent.Algorithm, room.RoomId.Full);
            return;
        }

        lock(room)
        {
            room.Encryption = new RoomEncryption(encryptionEvent.Algorithm);
        }
    }

    private void HandleMessageReceived(MatrixId roomId, List<ReceivedTextMessage> messages, Room room, ClientEventWithoutRoomID e)
    {
        if (e.Content == null)
        {
            Logger.LogWarning("Received timeline event with no content in room {RoomId}. Type {Type}", roomId.Full, e.Type);
            return;
        }

        var messageEvent = JsonSerializer.Deserialize<RoomMessageEvent>((JsonElement)e.Content);
        if (messageEvent == null)
        {
            Logger.LogWarning("Received m.room.message event deserialize returned null in room {RoomId}.", roomId.Full);
            return;
        }
        var userIdStr = e.Sender;
        if (!UserId.TryParse(userIdStr, out MatrixId? userId) || userId == null)
        {
            Logger.LogWarning("Received m.room.message event with invalid user ID: {UserId} in room {RoomId}.", userIdStr, roomId.Full);
            return;
        }
        if (messageEvent.MsgType != "m.text")
        {
            Logger.LogInformation("Ignoring m.room.message event with non-text message type {MsgType} in room {RoomId}.", messageEvent.MsgType, roomId.Full);
            return;
        }

        var serverTs = DateTimeOffset.FromUnixTimeMilliseconds(e.OriginServerTs);

        var user = GetOrAddUser(userId);
        var roomUser = GetOrAddUser(user, room);
        var message = new ReceivedTextMessage(messageEvent.Body, room, roomUser, e.EventId, serverTs, this);
        if (messageEvent.Mentions != null && messageEvent.Mentions.UserIds != null && messageEvent.Mentions.UserIds.Count > 0)
        {
            message.Mentions = messageEvent.Mentions.UserIds
                .Select(id => UserId.TryParse(id, out MatrixId? mentionId) ? mentionId : null)
                .Where(id => id != null)
                .Select(id => id!)
                .Select(id => GetOrAddUser(id))
                .Select(u => GetOrAddUser(u, room)).ToList();
        }

        messages.Add(message);
        room.LastMessage = message;
    }

    private User GetOrAddUser(MatrixId id)
    {
        return _users.GetOrAdd(id.Full, (_) => new User(id));
    }

    private Room GetOrAddRoom(MatrixId id)
    {
        return _rooms.GetOrAdd(id.Full, (_) => new Room(id, this));
    }

    private RoomUser GetOrAddUser(User user, Room room)
    {
        if(room.Users.TryGetValue(user.UserId.Full, out var roomUser) && roomUser != null)
            {
                return roomUser;
            }

        lock(room)
        {
            if(room.Users.TryGetValue(user.UserId.Full, out roomUser) && roomUser != null)
            {
                return roomUser;
            }
            roomUser = new RoomUser(user);
            room.Users = room.Users.Add(user.UserId.Full, roomUser);
            return roomUser;
        }
    }

    private void HandleAccountDataReceived(MatrixId? roomId, List<MatrixEvent> accountData)
    {
        // Sadly, we don't have any account data events we're interested in
        foreach(var ev in accountData)
        {
            Logger.LogDebug("Received account data event: {Event} in Room {Room}", ev.Type, roomId?.Full ?? "(global)");
        }
    }
}

