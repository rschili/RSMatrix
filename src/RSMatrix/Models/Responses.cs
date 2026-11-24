using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix.Models;
public class MatrixErrorResponse
{
    [JsonPropertyName("errcode")]
    public required string ErrorCode { get; set; }

    [JsonPropertyName("error")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("retry_after_ms")]
    public long? RetryAfterMs { get; set; }

    [JsonPropertyName("soft_logout")]
    public bool SoftLogout { get; set; }
}


public class WellKnownUriResponse
{
    [JsonPropertyName("m.homeserver")]
    public required HomeServer HomeServer { get; set; }

    [JsonPropertyName("m.identity_server")]
    public IdentityServer? IdentityServer { get; set; }
}

public class HomeServer
{
    [JsonPropertyName("base_url")]
    public required string BaseUrl { get; set; }
}
public class IdentityServer
{
    [JsonPropertyName("base_url")]
    public required string BaseUrl { get; set; }
}

public class SpecVersionsResponse
{
    [JsonPropertyName("unstable_features")]
    public Dictionary<string, bool>? UnstableFeatures { get; set; }

    [JsonPropertyName("versions")]
    public required List<string> Versions { get; set; }
}

public class AuthFlowsResponse
{
    [JsonPropertyName("flows")]
    public required List<AuthFlow> Flows { get; set; }

    [JsonPropertyName("session")]
    public string? Session { get; set; }
}

public class AuthFlow
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }
}

public class LoginResponse
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; set; }
    [JsonPropertyName("device_id")]
    public required string DeviceId { get; set; }
}

public class CapabilitiesResponse
{
    [JsonPropertyName("capabilities")]
    public required Capabilities Capabilities { get; set; }
}

public class Capabilities
{
    [JsonPropertyName("m.change_password")]
    public BooleanCapability? ChangePassword { get; set; }

    [JsonPropertyName("m.set_displayname")]
    public BooleanCapability? SetDisplayName { get; set; }

    [JsonPropertyName("m.room_versions")]
    public RoomVersionsCapability? RoomVersions { get; set; }

    [JsonPropertyName("com.example.custom.ratelimit")]
    public RateLimit? RateLimit { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProps { get; set; }
}

public class RateLimit
{
    [JsonPropertyName("max_requests_per_hour")]
    public required int MaxRequestsPerHour { get; set; }
}

public class BooleanCapability
{
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; set; }
}

public class RoomVersionsCapability
{
    [JsonPropertyName("available")]
    public required Dictionary<string, string> Available { get; set; }

    [JsonPropertyName("default")]
    public required string Default { get; set; }
}

/// <summary>
/// This is the response after posting a new filter
/// </summary>
public class FilterResponse
{
    [JsonPropertyName("filter_id")]
    public required string FilterId { get; set; }
}

public class SyncResponse
{
    [JsonPropertyName("next_batch")]
    public required string NextBatch { get; set; }

    [JsonPropertyName("account_data")]
    public EventResponse? AccountData { get; set; }

    /// <summary>
    /// The updates to the presence status of other users.
    /// </summary>
    [JsonPropertyName("presence")]
    public EventResponse? Presence { get; set; }

    /// <summary>
    /// Updates to rooms.
    /// </summary>
    [JsonPropertyName("rooms")]
    public RoomEvents? Rooms { get; set; }

    /// <summary>
    /// Users whose devices changed, or with whom we no longer share an encrypted room.
    /// Only present on incremental syncs.
    /// </summary>
    [JsonPropertyName("device_lists")]
    public DeviceLists? DeviceLists { get; set; }

    /// <summary>
    /// For each key algorithm, the number of unclaimed one-time keys the server holds for this device.
    /// </summary>
    [JsonPropertyName("device_one_time_keys_count")]
    public Dictionary<string, int>? DeviceOneTimeKeysCount { get; set; }

    /// <summary>
    /// Key algorithms for which this device has an unused fallback key on the server.
    /// </summary>
    [JsonPropertyName("device_unused_fallback_key_types")]
    public List<string>? DeviceUnusedFallbackKeyTypes { get; set; }

    /// <summary>
    /// Send-to-device messages for this device.
    /// </summary>
    [JsonPropertyName("to_device")]
    public EventResponse? ToDevice { get; set; }
}

public class DeviceLists
{
    [JsonPropertyName("changed")]
    public List<string>? Changed { get; set; }

    [JsonPropertyName("left")]
    public List<string>? Left { get; set; }
}

public class EventResponse
{
    [JsonPropertyName("events")]
    public List<MatrixEvent>? Events { get; set; }
}


public class ClientEventWithoutRoomIdResponse
{
    [JsonPropertyName("events")]
    public List<ClientEventWithoutRoomID>? Events { get; set; }
}

public class TimelineEventResponse
{
    [JsonPropertyName("events")]
    public List<ClientEventWithoutRoomID>? Events { get; set; }

    [JsonPropertyName("limited")]
    public bool? Limited { get; set; }

    [JsonPropertyName("prev_batch")]
    public string? PrevBatch { get; set; }
}

public class RoomEvents
{
    [JsonPropertyName("invite")]
    public Dictionary<string, InvitedRoomEvents>? Invites { get; set; }

    [JsonPropertyName("join")]
    public Dictionary<string, JoinedRoomEvents>? Joined { get; set; }

    [JsonPropertyName("knock")]
    public Dictionary<string, JsonElement>? Knocks { get; set; }

    [JsonPropertyName("leave")]
    public Dictionary<string, JsonElement>? Leaves { get; set; }
}

public class InvitedRoomEvents
{
    [JsonPropertyName("invite_state")]
    public InviteState? InviteState { get; set; }
}

public class InviteState
{
    [JsonPropertyName("events")]
    public List<StrippedStateEvent>? Events { get; set; }
}

public class StrippedStateEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("state_key")]
    public required string StateKey { get; set; }

    [JsonPropertyName("content")]
    public required JsonElement Content { get; set; }

    [JsonPropertyName("sender")]
    public required string Sender { get; set; }
}

public class JoinRoomResponse
{
    [JsonPropertyName("room_id")]
    public required string RoomId { get; set; }
}

public class MessagesResponse
{
    [JsonPropertyName("start")]
    public required string Start { get; set; }

    [JsonPropertyName("end")]
    public string? End { get; set; }

    [JsonPropertyName("chunk")]
    public required List<ClientEvent> Chunk { get; set; }

    [JsonPropertyName("state")]
    public List<ClientEvent>? State { get; set; }
}

public class JoinedRoomEvents
{
    [JsonPropertyName("summary")]
    public RoomSummary? Summary { get; set; }

    [JsonPropertyName("account_data")]
    public EventResponse? AccountData { get; set; }

    [JsonPropertyName("ephemeral")]
    public EventResponse? Ephemeral { get; set; }

    [JsonPropertyName("state")]
    public ClientEventWithoutRoomIdResponse? State { get; set; }

    [JsonPropertyName("timeline")]
    public TimelineEventResponse? Timeline { get; set; }

    [JsonPropertyName("unread_notifications")]
    public UnreadNotifications? UnreadNotifications { get; set; }

    [JsonPropertyName("unread_thread_notifications")]
    public UnreadNotifications? UnreadThreadNotifications { get; set; }
}

public class RoomSummary
{
    [JsonPropertyName("m.heroes")]
    public List<string>? Heroes { get; set; }

    [JsonPropertyName("m.invited_member_count")]
    public int? InvitedMemberCount { get; set; }

    [JsonPropertyName("m.joined_member_count")]
    public int? JoinedMemberCount { get; set; }
}

public class UnreadNotifications
{
    [JsonPropertyName("highlight_count")]
    public int? HighlightCount { get; set; }
    [JsonPropertyName("notification_count")]
    public int? NotificationCount { get; set; }
}

public class PresenceResponse
{
    [JsonPropertyName("currently_active")]
    public bool? CurrentlyActive { get; set; }

    [JsonPropertyName("last_active_ago")]
    public int? LastActiveAgo { get; set; }

    [JsonPropertyName("presence")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Presence Presence { get; set; }

    [JsonPropertyName("status_msg")]
    public string? StatusMessage { get; set; }
}

public class MessageResponse
{
    [JsonPropertyName("event_id")]
    public required string EventId { get; set; }
}
