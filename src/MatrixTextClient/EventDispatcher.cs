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
                    foreach(var joinedRoomEvents in syncResponse.Rooms.Joined)
                    {
                        var roomIdString = joinedRoomEvents.Key;

                    }
                }
            }

        }
        public delegate Task GlobalAccountDataHandler(MatrixClient client, string type, JsonElement? content);
        public GlobalAccountDataHandler? GlobalAccountDataReceived;

        public delegate Task PresenceHandler(MatrixClient client, UserId sender, Presence presence);
        public PresenceHandler? PresenceReceived;

    }
}
