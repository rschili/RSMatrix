using MatrixTextClient.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace MatrixTextClient;

public class EventDispatcher
{
    public ILogger Logger { get; }

    public EventDispatcher(ILogger logger)
    {
        Logger = logger ?? NullLogger<EventDispatcher>.Instance;
    }

    public async Task HandleSyncReceivedAsync(MatrixClientCore client, SyncResponse syncResponse)
    {
        try
        {
            await HandleSyncReceivedInternalAsync(client, syncResponse).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling sync response");
        }
    }

    private async Task HandleSyncReceivedInternalAsync(MatrixClientCore client, SyncResponse syncResponse)
    {
        if (syncResponse == null)
            return;

        if (syncResponse.AccountData != null)
        {
            await HandleAccountDataReceivedAsync(client, null, syncResponse.AccountData).ConfigureAwait(false);
        }

        if (syncResponse.Presence != null)
        {
            await HandlePresenceReceivedAsync(client, syncResponse.Presence).ConfigureAwait(false);
        }

        if (syncResponse.Rooms != null)
        {
            if (syncResponse.Rooms.Joined != null)
            {
                foreach (var pair in syncResponse.Rooms.Joined)
                {
                    var roomIdString = pair.Key;
                    if (!RoomId.TryParse(roomIdString, out var roomId) || roomId == null)
                    {
                        Logger.LogWarning("Received joined room event with invalid room ID: {roomId}", roomIdString);
                        continue;
                    }

                    await HandleRoomSummaryReceivedAsync(client, roomId, pair.Value.Summary).ConfigureAwait(false);
                    await HandleAccountDataReceivedAsync(client, roomId, pair.Value.AccountData).ConfigureAwait(false);
                    await HandleEphemeralReceivedAsync(client, roomId, pair.Value.Ephemeral).ConfigureAwait(false);
                    await HandleStateReceivedAsync(client, roomId, pair.Value.State).ConfigureAwait(false);
                    await HandleTimelineReceivedAsync(client, roomId, pair.Value.Timeline).ConfigureAwait(false);
                } // foreach joined room
            } // if joined
        } // if rooms
    }

    private async Task HandleRoomSummaryReceivedAsync(MatrixClientCore client, MatrixId roomId, RoomSummary? summary)
    {
        if (summary == null || summary.Heroes == null || RoomSummaryReceived == null)
            return;

        var users = summary.Heroes.Select(s => UserId.TryParse(s, out MatrixId? userId) ? userId : null)
            .Where(id => id != null).Select(id => id!).ToList();
        await RoomSummaryReceived(client, roomId, users!).ConfigureAwait(false);
    }

    private async Task HandleStateReceivedAsync(MatrixClientCore client, MatrixId roomId, ClientEventWithoutRoomIdResponse? state)
    {
        if (state == null || state.Events == null)
            return;

        foreach (var e in state.Events)
        {
            if (e.Content == null)
            {
                Logger.LogWarning("Received state event with missing content of type {type}", e.Type);
                continue;
            }
            if (e.Type == "m.room.member")
            {
                if (RoomMemberReceived == null)
                    continue;

                if (!e.Content.Value.TryGetProperty("displayname", out var displayNameElement) || displayNameElement.ValueKind == JsonValueKind.String)
                {
                    Logger.LogWarning("Received membership event with missing displayname");
                    continue;
                }
                var displayName = displayNameElement.GetString();
                if (!e.Content.Value.TryGetProperty("membership", out var membershipElement) || membershipElement.ValueKind != JsonValueKind.String)
                {
                    Logger.LogWarning("Received membership event with missing membership");
                    continue;
                }
                var membership = membershipElement.GetString();

                if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(membership))
                {
                    Logger.LogWarning("Received membership event with missing displayname or membership");
                    continue;
                }

                if (!UserId.TryParse(e.StateKey, out var userId) || userId == null)
                {
                    Logger.LogWarning("Received membership event with invalid user ID: {userId}", e.StateKey);
                    continue;
                }

                await RoomMemberReceived(client, roomId, userId, displayName, membership).ConfigureAwait(false);
            }
            else if (e.Type == "m.space.child")
            { } // ignore for now. Room hierarchy is not implemented
            else if (e.Type == "m.room.avatar")
            { } // ignore for now. Room avatar is not implemented
            else if (e.Type == "m.room.history_visibility")
            { } // ignore for now.
            else if (e.Type == "m.room.canonical_alias")
            {
                if (CanonicalAliasReceived == null)
                    continue;

                if (!e.Content.Value.TryGetProperty("alias", out var aliasElement) || aliasElement.ValueKind != JsonValueKind.String)
                {
                    Logger.LogWarning("Received canonical alias event with missing alias");
                    continue;
                }
                var alias = aliasElement.GetString();
                if (string.IsNullOrEmpty(alias))
                {
                    Logger.LogWarning("Received canonical alias event with empty alias");
                    continue;
                }

                await CanonicalAliasReceived(client, roomId, alias).ConfigureAwait(false);
            }
            else if (e.Type == "m.room.name")
            {
                if (RoomNameReceived == null)
                    continue;

                if (!e.Content.Value.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                {
                    Logger.LogWarning("Received room name event with missing name");
                    continue;
                }
                var name = nameElement.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    Logger.LogWarning("Received room name event with empty name");
                    continue;
                }

                await RoomNameReceived(client, roomId, name).ConfigureAwait(false);
            } // ignore for now.
            else if (e.Type == "m.room.topic")
            { } // ignore for now.
            else if (e.Type == "m.room.create")
            { } // ignore for now.
            else if (e.Type == "m.room.join_rules")
            { } // ignore for now.
            else if (e.Type == "m.room.power_levels")
            { } // ignore for now.
            else
            {
                Logger.LogWarning("Received state event with unsupported type: {type}", e.Type);
            }
        }
    }

    private async Task HandleTimelineReceivedAsync(MatrixClientCore client, MatrixId roomId, TimelineEventResponse? timeline)
    {
        if (timeline == null || timeline.Events == null)
            return;

        foreach (var e in timeline.Events)
        {
            if (e.Content == null)
            {
                Logger.LogWarning("Received timeline event with missing content of type {type}", e.Type);
                continue;
            }

            if (e.Type == "m.room.member")
                continue; // ignore for now. Updates to member events

            if (e.Type == "m.room.message")
            {
                if (MessageReceived == null)
                    continue;
                if (!e.Content.Value.TryGetProperty("msgtype", out var msgTypeElement) || msgTypeElement.ValueKind != JsonValueKind.String)
                {
                    Logger.LogWarning("Received message event with missing msgtype");
                    continue;
                }
                var msgType = msgTypeElement.GetString();
                if (string.IsNullOrEmpty(msgType))
                {
                    Logger.LogWarning("Received message event with empty msgtype");
                    continue;
                }

                if (msgType != "m.text")
                {
                    Logger.LogWarning("Received message event with unsupported msgtype: {msgType}", msgType);
                    continue;
                }

                if (!UserId.TryParse(e.Sender, out var sender) || sender == null)
                {
                    Logger.LogWarning("Received message event with invalid sender: {sender}", e.Sender);
                    continue;
                }

                List<MatrixId>? mentions = null;
                if (e.Content.Value.TryGetProperty("m.mentions", out var mentionsElement) && mentionsElement.ValueKind == JsonValueKind.Object
                    && mentionsElement.TryGetProperty("user_ids", out var userIdsElement) && userIdsElement.ValueKind == JsonValueKind.Array)
                {
                    mentions = userIdsElement.EnumerateArray()
                        .Select(v => v.GetString() ?? null)
                        .Where(userId => userId != null)
                        .Select(s => UserId.TryParse(s, out MatrixId? userId) ? userId : null)
                        .Where(id => id != null).Select(id => id!).ToList();
                }

                if (e.Content.Value.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String)
                {
                    var body = bodyElement.GetString();
                    if (body != null)
                    {
                        await MessageReceived(roomId, sender, e.EventId, body, mentions).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                Logger.LogWarning("Received timeline event with unsupported type: {type}", e.Type);
            }
        }
    }

    private async Task HandlePresenceReceivedAsync(MatrixClientCore client, EventResponse presenceEvents)
    {
        if (PresenceReceived == null || presenceEvents.Events == null)
            return;

        foreach (var presence in presenceEvents.Events)
        {
            if (presence.Type != "m.presence")
            {
                Logger.LogWarning("Received presence event with unexpected type: {type}", presence.Type);
                continue;
            }

            UserId.TryParse(presence.Sender, out var userId);
            if (userId == null)
            {
                Logger.LogWarning("Received presence event with invalid sender: {sender}", presence.Sender);
                continue;
            }

            if (presence.Content == null)
            {
                Logger.LogWarning("Received presence event with missing content");
                continue;
            }
            var content = JsonSerializer.Deserialize<Presence>((JsonElement)presence.Content);
            await PresenceReceived(client, userId, content).ConfigureAwait(false);
        }
    }

    private async Task HandleAccountDataReceivedAsync(MatrixClientCore client, MatrixId? roomId, EventResponse? accountData)
    {
        if (accountData != null && accountData.Events != null)
        {
            foreach (var e in accountData.Events)
            {
                if (roomId != null && RoomAccountDataReceived != null)
                    await RoomAccountDataReceived(client, roomId, e.Type, e.Content).ConfigureAwait(false);
                else if (GlobalAccountDataReceived != null)
                    await GlobalAccountDataReceived(client, e.Type, e.Content).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleEphemeralReceivedAsync(MatrixClientCore client, MatrixId roomId, EventResponse? eph)
    {
        if (eph != null && eph.Events != null)
        {
            foreach (var e in eph.Events)
            {
                if (e.Content == null)
                    continue;
                if (e.Type == "m.typing")
                {
                    if (TypingReceived == null)
                        continue;

                    if (e.Content.Value.TryGetProperty("user_ids", out var userIdsElement) && userIdsElement.ValueKind == JsonValueKind.Array)
                    {
                        var typingUsers = userIdsElement.EnumerateArray()
                            .Select(v => v.GetString() ?? null)
                            .Where(userId => userId != null)
                            .Select(s => UserId.TryParse(s, out MatrixId? userId) ? userId : null)
                            .Where(id => id != null).Select(id => id!).ToList();

                        await TypingReceived(client, roomId, typingUsers).ConfigureAwait(false);
                    }
                    else
                        Logger.LogWarning("Received typing event with invalid user_ids structure");
                }
                else if (e.Type == "m.receipt")
                {
                    if (ReceiptReceived == null)
                        continue;

                    foreach (var receipt in e.Content.Value.EnumerateObject())
                    {
                        var eventIdStr = receipt.Name;
                        if (!Models.EventId.TryParse(eventIdStr, out var eventId) || eventId == null)
                        {
                            Logger.LogWarning("Received receipt event with invalid event ID: {eventId}", eventIdStr);
                            continue;
                        }

                        if (!receipt.Value.TryGetProperty("m.read", out var readReceipts) || readReceipts.ValueKind != JsonValueKind.Object)
                        {
                            Logger.LogWarning("Received receipt event with invalid m.read structure");
                            continue;
                        }

                        foreach (var readReceipt in readReceipts.EnumerateObject())
                        {
                            var userIdStr = readReceipt.Name;
                            if (!UserId.TryParse(userIdStr, out var userId) || userId == null)
                            {
                                Logger.LogWarning("Received receipt event with invalid user ID: {userId}", userIdStr);
                                continue;
                            }

                            string? threadId = null;
                            if (readReceipt.Value.TryGetProperty("thread_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                            {
                                threadId = tid.GetString();
                            }

                            await ReceiptReceived(client, roomId, eventId, userId, threadId).ConfigureAwait(false);
                        }
                    }
                } // else if receipt
                else
                    Logger.LogWarning("Received ephemeral event with unexpected type: {type}", e.Type);
            } // foreach ephemeral event
        } // if ephemeral
    }

    public delegate Task GlobalAccountDataHandler(MatrixClientCore client, string type, JsonElement? content);
    public GlobalAccountDataHandler? GlobalAccountDataReceived;

    public delegate Task RoomAccountDataHandler(MatrixClientCore client, MatrixId room, string type, JsonElement? content);
    public RoomAccountDataHandler? RoomAccountDataReceived;

    public delegate Task PresenceHandler(MatrixClientCore client, MatrixId sender, Presence presence);
    public PresenceHandler? PresenceReceived;

    public delegate Task TypingHandler(MatrixClientCore client, MatrixId room, List<MatrixId> typingUsers);
    public TypingHandler? TypingReceived;

    public delegate Task ReceiptHandler(MatrixClientCore client, MatrixId room, MatrixId eventId, MatrixId userId, string? threadId);

    public ReceiptHandler? ReceiptReceived;

    public delegate Task RoomSummaryHandler(MatrixClientCore client, MatrixId room, List<MatrixId> users);
    public RoomSummaryHandler? RoomSummaryReceived;

    public delegate Task StateHandler(MatrixClientCore client, MatrixId room, MatrixId userId, string displayName, string membership);
    public StateHandler? RoomMemberReceived;

    public delegate Task CanonicalAliasHandler(MatrixClientCore client, MatrixId room, string alias);
    public CanonicalAliasHandler? CanonicalAliasReceived;

    public delegate Task RoomNameHandler(MatrixClientCore client, MatrixId room, string name);
    public RoomNameHandler? RoomNameReceived;

    public delegate Task MessageHandler(MatrixId room, MatrixId sender, string eventId, string body, List<MatrixId>? mentions);
    public MessageHandler? MessageReceived;
}
