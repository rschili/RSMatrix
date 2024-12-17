using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatrixTextClient.Responses
{
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
        public Dictionary<string, JsonElement>? AdditionalCapabilities { get; set; }
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

    /// <summary>
    /// Consolt spec for help https://spec.matrix.org/v1.12/client-server-api/#get_matrixclientv3useruseridfilterfilterid
    /// This class is used for both, requests and responses as they match in structure
    /// </summary>
    public class Filter
    {
        /// <summary>
        /// This is not part of the request or response, it is used to store the filter id
        /// It's sent with sync requests to the server
        /// </summary>
        [JsonIgnore()]
        public string? FilterId { get; set; }

        [JsonPropertyName("account_data")]
        public EventFilter? AccountData { get; set; }

        [JsonPropertyName("event_fields")]
        public List<string>? EventFields { get; set; }

        [JsonPropertyName("event_format")]
        public string? EventFormat { get; set; } = "client";

        [JsonPropertyName("presence")]
        public EventFilter? Presence { get; set; }

        [JsonPropertyName("room")]
        public RoomFilter? Room { get; set; }
    }

    public class EventFilter
    {
        [JsonPropertyName("limit")]
        public int? Limit { get; set; }

        [JsonPropertyName("not_senders")]
        public List<string>? NotSenders { get; set; }

        [JsonPropertyName("not_types")]
        public List<string>? NotTypes { get; set; }

        [JsonPropertyName("senders")]
        public List<string>? Senders { get; set; }

        [JsonPropertyName("types")]
        public List<string>? Types { get; set; }
    }

    public class RoomFilter
    {
        [JsonPropertyName("account_data")]
        public RoomEventFilter? AccountData { get; set; }

        [JsonPropertyName("ephemeral")]
        public RoomEventFilter? Ephemeral { get; set; }

        [JsonPropertyName("include_leave")]
        public bool? IncludeLeave { get; set; } = false;

        [JsonPropertyName("not_rooms")]
        public List<string>? NotRooms { get; set; }

        [JsonPropertyName("rooms")]
        public List<string>? Rooms { get; set; }

        [JsonPropertyName("state")]
        public StateFilter? State { get; set; }

        [JsonPropertyName("timeline")]
        public RoomEventFilter? Timeline { get; set; }
    }

    public class RoomEventFilter
    {
        [JsonPropertyName("contains_url")]
        public bool? ContainsUrl { get; set; }

        [JsonPropertyName("include_redundant_members")]
        public bool? IncludeRedundantMembers { get; set; } = false;

        [JsonPropertyName("lazy_load_members")]
        public bool? LazyLoadMembers { get; set; } = false;

        [JsonPropertyName("limit")]
        public int? Limit { get; set; }

        [JsonPropertyName("not_rooms")]
        public List<string>? NotRooms { get; set; }

        [JsonPropertyName("not_senders")]
        public List<string>? NotSenders { get; set; }

        [JsonPropertyName("not_types")]
        public List<string>? NotTypes { get; set; }

        [JsonPropertyName("rooms")]
        public List<string>? Rooms { get; set; }

        [JsonPropertyName("senders")]
        public List<string>? Senders { get; set; }

        [JsonPropertyName("types")]
        public List<string>? Types { get; set; }

        [JsonPropertyName("unread_thread_notifications")]
        public bool? UnreadThreadNotifications { get; set; } = false;
    }

    public class StateFilter
    {
        [JsonPropertyName("contains_url")]
        public bool? ContainsUrl { get; set; }

        [JsonPropertyName("include_redundant_members")]
        public bool? IncludeRedundantMembers { get; set; } = false;

        [JsonPropertyName("lazy_load_members")]
        public bool? LazyLoadMembers { get; set; } = false;

        [JsonPropertyName("limit")]
        public int? Limit { get; set; }

        [JsonPropertyName("not_rooms")]
        public List<string>? NotRooms { get; set; }

        [JsonPropertyName("not_senders")]
        public List<string>? NotSenders { get; set; }

        [JsonPropertyName("not_types")]
        public List<string>? NotTypes { get; set; }

        [JsonPropertyName("rooms")]
        public List<string>? Rooms { get; set; }

        [JsonPropertyName("senders")]
        public List<string>? Senders { get; set; }

        [JsonPropertyName("types")]
        public List<string>? Types { get; set; }

        [JsonPropertyName("unread_thread_notifications")]
        public bool? UnreadThreadNotifications { get; set; } = false;
    }

    public class SyncResponse
    {
        [JsonPropertyName("next_batch")]
        public required string NextBatch { get; set; }

        [JsonPropertyName("account_data")]
        public List<EventData>? AccountData { get; set; }

        /// <summary>
        /// The updates to the presence status of other users.
        /// </summary>
        [JsonPropertyName("presence")]
        public List<EventData>? Presence { get; set; }

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

    public class EventData
    {
        [JsonPropertyName("events")]
        public List<MatrixEvent>? Events { get; set; }
    }

    public class RoomEvents
    {
        [JsonPropertyName("invite")]
        public Dictionary<string, JsonElement>? Invites { get; set; }

        [JsonPropertyName("join")]
        public Dictionary<string, JsonElement>? Joins { get; set; }

        [JsonPropertyName("knock")]
        public Dictionary<string, JsonElement>? Knocks { get; set; }

        [JsonPropertyName("leave")]
        public Dictionary<string, JsonElement>? Leaves { get; set; }
    }
}