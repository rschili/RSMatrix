﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MatrixTextClient.Requests
{
    public class Identifier
    {
        [JsonPropertyName("type")]
        public required string Type { get; set; }

        [JsonPropertyName("user")]
        public string? User { get; set; }
    }

    public class LoginRequest
    {
        [JsonPropertyName("identifier")]
        public required Identifier Identifier { get; set; }

        [JsonPropertyName("initial_device_display_name")]
        public string? InitialDeviceDisplayName { get; set; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        [JsonPropertyName("type")]
        public required string Type { get; set; }
    }

    public class SyncParameters
    {
        [JsonPropertyName("filter")]
        public string? Filter { get; set; }

        /// <summary>
        /// A point in time to continue a sync from.
        /// This should be the next_batch token returned by an earlier call to this endpoint.
        /// </summary>
        [JsonPropertyName("since")]
        public string? Since { get; set; }

        [JsonPropertyName("full_state")]
        public bool FullState { get; set; } = false;

        [JsonPropertyName("set_presence")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public Presence SetPresence { get; set; } = Presence.Online;

        /// <summary>
        /// The maximum time to wait, in milliseconds, before returning this request.
        /// </summary>
        [JsonPropertyName("timeout")]
        public int Timeout { get; set; }
        
        public IEnumerable<KeyValuePair<string, string>> GetAsParameters()
        {
            if (FullState) yield return KeyValuePair.Create("full_state", "true");
            if (SetPresence != Presence.Online) yield return KeyValuePair.Create("set_presence", SetPresence.ToString().ToLower());
            if (Timeout > 0) yield return KeyValuePair.Create("timeout", Timeout.ToString());
            if (!string.IsNullOrWhiteSpace(Filter)) yield return KeyValuePair.Create("filter", Filter);
            if (!string.IsNullOrWhiteSpace(Since)) yield return KeyValuePair.Create("since", Since);
        }
    }
}
