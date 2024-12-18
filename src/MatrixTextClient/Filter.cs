using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MatrixTextClient
{
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
}
