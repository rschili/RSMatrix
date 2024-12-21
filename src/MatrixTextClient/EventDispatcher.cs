using MatrixTextClient.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MatrixTextClient
{
    public class EventDispatcher
    {
        public ILogger Logger { get; }

        public EventDispatcher(ILogger logger)
        {
            Logger = logger ?? NullLogger<EventDispatcher>.Instance;
        }


        public async Task HandleSyncReceived(MatrixClient client, SyncResponse syncResponse)
        {
            try
            {
                await HandleSyncReceivedInternal(client, syncResponse);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling sync response");
            }
        }

        private async Task HandleSyncReceivedInternal(MatrixClient client, SyncResponse syncResponse)
        {
            if (syncResponse == null)
                return;

            if (GlobalAccountDataReceived != null && syncResponse.AccountData != null && syncResponse.AccountData.Events != null)
            {
                foreach (var e in syncResponse.AccountData.Events)
                {
                    await GlobalAccountDataReceived(client, e.Type, e.Content);
                }
            }

            if (PresenceReceived != null && syncResponse.Presence != null && syncResponse.Presence.Events != null)
            {
                foreach (var presence in syncResponse.Presence.Events)
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
                    await PresenceReceived(client, userId, content);
                }
            }

            if (syncResponse.Rooms != null)
            {
                if (syncResponse.Rooms.Joined != null)
                {
                    foreach(var pair in syncResponse.Rooms.Joined)
                    {
                        var roomIdString = pair.Key;
                        if (!RoomId.TryParse(roomIdString, out var roomId) || roomId == null)
                        {
                            Logger.LogWarning("Received joined room event with invalid room ID: {roomId}", roomIdString);
                            continue;
                        }

                        var ae = pair.Value.AccountData;
                        if (ae != null && ae.Events != null && RoomAccountDataReceived != null)
                        {
                            foreach (var e in ae.Events)
                            {
                                await RoomAccountDataReceived(client, roomId, e.Type, e.Content);
                            }
                        }

                        var eph = pair.Value.Ephemeral;
                        if (eph != null && eph.Events != null)
                        {
                            foreach (var e in eph.Events)
                            {
                                if(e.Content == null)
                                    continue;
                                if (e.Type == "m.typing")
                                {
                                    if(TypingReceived == null)
                                        continue;

                                    if (e.Content.Value.TryGetProperty("user_ids", out var userIdsElement) && userIdsElement.ValueKind == JsonValueKind.Array)
                                    {
                                        var typingUsers = userIdsElement.EnumerateArray()
                                            .Select(v => v.GetString() ?? null)
                                            .Where(userId => userId != null)
                                            .Select(s => UserId.TryParse(s, out MatrixId? userId) ? userId : null)
                                            .Where(id => id != null).Select(id => id!).ToList();
                                        
                                        await TypingReceived(client, roomId, typingUsers);
                                    }
                                    else
                                        Logger.LogWarning("Received typing event with invalid user_ids structure");
                                }
                                else if (e.Type == "m.receipt")
                                {
                                    if (ReceiptReceived == null)
                                        continue;

                                    foreach(var receipt in e.Content.Value.EnumerateObject())
                                    {
                                        var eventIdStr = receipt.Name;
                                        if (!EventId.TryParse(eventIdStr, out var eventId) || eventId == null)
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
                                            if(readReceipt.Value.TryGetProperty("thread_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                                            {
                                                threadId = tid.GetString();
                                            }

                                            await ReceiptReceived(client, roomId, eventId, userId, threadId);
                                        }
                                    }
                                } // else if receipt
                                else
                                    Logger.LogWarning("Received ephemeral event with unexpected type: {type}", e.Type);
                            } // foreach ephemeral event
                        } // if ephemeral
                    } // foreach joined room
                } // if joined
            } // if rooms

        }
        public delegate Task GlobalAccountDataHandler(MatrixClient client, string type, JsonElement? content);
        public GlobalAccountDataHandler? GlobalAccountDataReceived;

        public delegate Task RoomAccountDataHandler(MatrixClient client, MatrixId room, string type, JsonElement? content);
        public RoomAccountDataHandler? RoomAccountDataReceived;

        public delegate Task PresenceHandler(MatrixClient client, MatrixId sender, Presence presence);
        public PresenceHandler? PresenceReceived;

        public delegate Task TypingHandler(MatrixClient client, MatrixId room, List<MatrixId> typingUsers);
        public TypingHandler? TypingReceived;

        public delegate Task ReceiptHandler(MatrixClient client, MatrixId room, MatrixId eventId, MatrixId userId, string? threadId);

        public ReceiptHandler? ReceiptReceived;

    }
}
