using System;
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
}
