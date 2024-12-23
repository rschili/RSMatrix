using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatrixTextClient.Models;
public class MatrixErrorResponse
{
    [JsonPropertyName("errcode")]
    public required string ErrorCode { get; set; }

    [JsonPropertyName("error")]
    public required string ErrorMessage { get; set; }
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProps { get; set; }
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
    /// Information on end-to-end device updates, as specified in End-to-end encryption.
    /// </summary>
    [JsonPropertyName("device_lists")]
    public JsonElement? DeviceLists { get; set; }

    /// <summary>
    /// Information on end-to-end encryption keys, as specified in End-to-end encryption.
    /// </summary>
    [JsonPropertyName("device_one_time_keys")]
    public JsonElement? DeviceOneTimeKeys { get; set; }

    /// <summary>
    /// Information on the send-to-device messages for the client device, as defined in Send-to-Device messaging.
    /// </summary>
    [JsonPropertyName("to_device")]
    public JsonElement? ToDevice { get; set; }
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
    public Dictionary<string, JsonElement>? Invites { get; set; }

    [JsonPropertyName("join")]
    public Dictionary<string, JoinedRoomEvents>? Joined { get; set; }

    [JsonPropertyName("knock")]
    public Dictionary<string, JsonElement>? Knocks { get; set; }

    [JsonPropertyName("leave")]
    public Dictionary<string, JsonElement>? Leaves { get; set; }
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