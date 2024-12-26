using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Narrensicher.Matrix.Models;
using Narrensicher.Matrix.Http;
using System.Reflection.Metadata;
using System.Collections.Concurrent;

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
                await HandleAccountDataReceivedAsync(null, response.AccountData.Events).ConfigureAwait(false);
            }

            if (response.Presence != null && response.Presence.Events != null)
            {
                await HandlePresenceReceivedAsync(response.Presence.Events).ConfigureAwait(false);
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
                            await HandleRoomSummaryReceivedAsync(roomId, pair.Value.Summary).ConfigureAwait(false);

                        if(pair.Value.AccountData != null && pair.Value.AccountData.Events != null)
                            await HandleAccountDataReceivedAsync(roomId, pair.Value.AccountData.Events).ConfigureAwait(false);

                        if(pair.Value.Ephemeral != null && pair.Value.Ephemeral.Events != null)
                            await HandleEphemeralReceivedAsync(roomId, pair.Value.Ephemeral.Events).ConfigureAwait(false);

                        if(pair.Value.State != null && pair.Value.State.Events != null)
                            await HandleStateReceivedAsync(roomId, pair.Value.State.Events).ConfigureAwait(false);

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

    private ValueTask HandleRoomSummaryReceivedAsync(MatrixId roomId, RoomSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(summary, nameof(summary));

        var users = summary?.Heroes?.Select(s => UserId.TryParse(s, out MatrixId? userId) ? userId : null)
            ?.Where(id => id != null)?.Select(id => id!)
            ?.Select(id => GetOrAddUser(id))?.ToList();

        var room = _rooms.GetOrAdd(roomId.Full,
            (_) => new Room(roomId));

        if(users == null || users.Count == 0)
        {
            Logger.LogWarning("Received room summary for room {RoomId} with no users.", roomId.Full);
            return ValueTask.CompletedTask;
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

        return ValueTask.CompletedTask;
    }

    private async Task HandlePresenceReceivedAsync(List<MatrixEvent> events)
    {
        throw new NotImplementedException();
    }

    
    private async Task HandleEphemeralReceivedAsync(MatrixId roomId, List<MatrixEvent> events)
    {
        throw new NotImplementedException();
    }

    
    private async Task HandleStateReceivedAsync(MatrixId roomId, List<ClientEventWithoutRoomID> events)
    {
        throw new NotImplementedException();
    }

    private async Task HandleTimelineReceivedAsync(MatrixId roomId, List<ClientEventWithoutRoomID> events, List<MatrixTextMessage> messages)
    {
        throw new NotImplementedException();
    }

    private User GetOrAddUser(MatrixId id)
    {
        return _users.GetOrAdd(id.Full, (_) => new User(id));
    }

    private ValueTask HandleAccountDataReceivedAsync(MatrixId? roomId, List<MatrixEvent> accountData)
    {
        // Sadly, we don't have any account data events we're interested in
        foreach(var ev in accountData)
        {
            Logger.LogDebug("Received account data event: {Event} in Room {Room}", ev.Type, roomId?.Full ?? "(global)");
        }
        return ValueTask.CompletedTask;
    }
}

