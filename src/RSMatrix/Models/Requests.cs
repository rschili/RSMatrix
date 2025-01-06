using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix.Models;

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
        /*if (SetPresence != Presence.Online) */yield return KeyValuePair.Create("set_presence", SetPresence.ToString().ToLower());
        if (Timeout > 0) yield return KeyValuePair.Create("timeout", Timeout.ToString());
        if (!string.IsNullOrWhiteSpace(Filter)) yield return KeyValuePair.Create("filter", Filter);
        if (!string.IsNullOrWhiteSpace(Since)) yield return KeyValuePair.Create("since", Since);
    }
}

public class ReceiptRequest
{
    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; set; }
}

public class ReadMarkerRequest
{
    [JsonPropertyName("m.read")]
    public string? read { get; set; }

    [JsonPropertyName("m.fully_read")]
    public string? FullyRead { get; set; }

    [JsonPropertyName("m.read.private")]
    public string? ReadPrivate { get; set; }
}

internal class TypingRequest
{
    [JsonPropertyName("typing")]
    public required bool Typing { get; set; }

    [JsonPropertyName("timeout")]
    public uint Timeout { get; set; }
}

internal class MessageRequest
{
    [JsonPropertyName("msgtype")]
    public required string MsgType { get; set; }

    [JsonPropertyName("body")]
    public required string Body { get; set; }

    [JsonPropertyName("m.mentions")]
    public RoomMessageMention? Mentions { get; set; }
}

internal class QueryKeysRequest
{
    /// <summary>
    /// Required: The keys to be downloaded. A map from user ID,
    /// to a list of device IDs, or to an empty list to indicate all devices for the corresponding user.
    /// </summary>
    [JsonPropertyName("device_keys")]
    public required Dictionary<string, List<string>> DeviceKeys { get; set; }

    /// <summary>
    /// The time (in milliseconds) to wait when downloading keys from remote servers. 10 seconds is the recommended default.
    /// </summary>
    [JsonPropertyName("timeout")]
    public int? Timeout { get; set; }
}

internal class UploadKeysRequest
{
    /// <summary>
    /// Identity keys for the device. May be absent if no new identity keys are required.
    /// </summary>
    [JsonPropertyName("device_keys")]
    public DeviceInformation? DeviceKeys { get; set; }

    [JsonPropertyName("fallback_keys")]
    public Dictionary<string, JsonElement>? FallbackKeys { get; set; }

    [JsonPropertyName("one_time_keys")]
    public Dictionary<string, JsonElement>? OneTimeKeys { get; set; }
}

// may be used instead of a string in Fallback Keys and OneTimeKeys above
internal class KeyObject
{
    [JsonPropertyName("key")]
    public required string Key { get; set; }

    [JsonPropertyName("signatures")]
    public required Dictionary<string, Dictionary<string, string>>? Signatures { get; set; }
}