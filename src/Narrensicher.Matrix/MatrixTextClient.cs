using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Narrensicher.Matrix.Models;
using Narrensicher.Matrix.Http;
using System.Reflection.Metadata;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Narrensicher.Matrix;

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

    public delegate Task MessageHandler(MatrixTextMessage message);

    private MessageHandler? _messageHandler = null; // We use this to track if the sync has been started

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
            AccountData = new()
            {
                NotTypes = new() { "*" }
            },
            Room = new()
            {
                AccountData = new()
                {
                    NotTypes = new() { "*" }
                },
                Ephemeral = new()
                {
                    NotTypes = new() { "m.typing", "m.receipt" },
                    LazyLoadMembers = true
                },
                Timeline = new()
                {
                    LazyLoadMembers = true,
                },
                State = new()
                {
                    NotTypes = new() { "m.room.join_rules", "m.room.guest_access", "m.room.avatar", "m.room.history_visibility", "m.room.power_levels" },
                    LazyLoadMembers = true
                },
            }
        };
        filter = await Core.SetFilterAsync(filter).ConfigureAwait(false);
        if(filter.FilterId == null)
            Logger.LogWarning("No filter ID was returned after setting a filter. This should not happen. It won't break the client, but unnecessary events will be received.");
        
        await Core.SyncAsync(HandleSyncResponseAsync).ConfigureAwait(false);
    }

    private async Task HandleSyncResponseAsync(SyncResponse response)
    {
        if (response == null)
            return;

        List<MatrixTextMessage> messages = new();

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
                            await HandleTimelineReceivedAsync(roomId, pair.Value.Timeline.Events, messages).ConfigureAwait(false);
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
                    catch(Exception ex)
                    {
                        Logger.LogError(ex, "Error during handling of message {MessageId}.", message.Body);
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
        {
            Logger.LogWarning("Received room summary for room {RoomId} with no users.", roomId.Full);
            return;
        }

        lock(room)
        {
            foreach (var user in users)
            {
                if(!room.Users.ContainsKey(user.UserId.Full))
                {
                    room.Users = room.Users.Add(user.UserId.Full, new RoomUser(user));
                }
            }
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
                    JsonSerializer.Deserialize<RoomMemberEvent>((JsonElement)e.Content);
                    // TODO
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

    private async Task HandleTimelineReceivedAsync(MatrixId roomId, List<ClientEventWithoutRoomID> events, List<MatrixTextMessage> messages)
    {
        throw new NotImplementedException();
    }

    private User GetOrAddUser(MatrixId id)
    {
        return _users.GetOrAdd(id.Full, (_) => new User(id));
    }

    private Room GetOrAddRoom(MatrixId id)
    {
        return _rooms.GetOrAdd(id.Full, (_) => new Room(id));
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

