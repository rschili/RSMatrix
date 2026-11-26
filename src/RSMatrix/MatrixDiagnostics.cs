using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace RSMatrix;

internal enum DiagnosticsMode
{
    Off,
    /// <summary>Warns once per run about everything received that the client neither handles nor ignores on purpose.</summary>
    Unhandled,
    /// <summary>Like <see cref="Unhandled"/>, and logs every request and response body at debug level.</summary>
    Verbose,
}

internal enum UnhandledKind
{
    Field,
    SyncSection,
    TimelineEvent,
    StateEvent,
    InviteStateEvent,
    AccountData,
    RoomAccountData,
    EphemeralEvent,
    PresenceEvent,
    ToDeviceEvent,
    EncryptedToDeviceEvent,
    MsgType,
    Relation,
}

/// <summary>
/// Troubleshooting output for spikes and gaps in our API, switched on with the environment variable RSMATRIX_DIAGNOSTICS
/// (Off, Unhandled, Verbose). Everything goes to the client's logger.
/// </summary>
internal sealed class MatrixDiagnostics
{
    internal const string EnvironmentVariable = "RSMATRIX_DIAGNOSTICS";

    public static MatrixDiagnostics Off { get; } = new(DiagnosticsMode.Off);

    public static MatrixDiagnostics FromEnvironment { get; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Everything we ignore on purpose, with the reason. A name ending in '*' matches by prefix.
    /// </summary>
    private static readonly Dictionary<(UnhandledKind Kind, string Name), string> Ignored = new()
    {
        [(UnhandledKind.AccountData, "m.cross_signing.*")] = "The encryption engine reads these from the server when it needs them.",
        [(UnhandledKind.AccountData, "m.secret_storage.*")] = "The encryption engine reads these from the server when it needs them.",
        [(UnhandledKind.SyncSection, "rooms.knock")] = "Knocking is not planned.",
        [(UnhandledKind.StateEvent, "m.space.child")] = "Spaces are not planned.",
        [(UnhandledKind.StateEvent, "m.space.parent")] = "Spaces are not planned.",
        [(UnhandledKind.StateEvent, "m.room.pinned_events")] = "Pinned messages are not planned.",
        [(UnhandledKind.StateEvent, "im.vector.modular.widgets")] = "Widgets are not planned.",
        [(UnhandledKind.StateEvent, "m.call.member")] = "Calls are not planned.",
        [(UnhandledKind.StateEvent, "org.matrix.msc3401.call.member")] = "Calls are not planned.",
        [(UnhandledKind.TimelineEvent, "m.call.*")] = "Calls are not planned.",
        [(UnhandledKind.TimelineEvent, "m.poll.*")] = "Polls are not planned.",
        [(UnhandledKind.TimelineEvent, "org.matrix.msc3381.poll.*")] = "Polls are not planned.",
        [(UnhandledKind.MsgType, "m.location")] = "Location messages are not planned.",
        [(UnhandledKind.ToDeviceEvent, "m.room_key_request")] = "Room key requests are not planned.",
        [(UnhandledKind.Field, "RoomEvent.age")] = SynapseLegacyField,
        [(UnhandledKind.Field, "RoomEvent.user_id")] = SynapseLegacyField,
        [(UnhandledKind.Field, "RoomEvent.prev_content")] = SynapseLegacyField,
        [(UnhandledKind.Field, "RoomEvent.replaces_state")] = SynapseLegacyField,
        [(UnhandledKind.Field, "LoginResponse.home_server")] = "Deprecated in the spec.",
    };

    private const string SynapseLegacyField = "Synapse still sends this old copy of a field that now lives in unsigned.";

    private static readonly HashSet<string> SecretFields = new(StringComparer.Ordinal)
    {
        "password", "access_token", "refresh_token", "token", "session_key",
    };

    private readonly ConcurrentDictionary<(UnhandledKind, string), byte> _reported = new();
    private readonly string? _unknownSetting;

    public MatrixDiagnostics(DiagnosticsMode mode) => Mode = mode;

    private MatrixDiagnostics(DiagnosticsMode mode, string? unknownSetting) : this(mode) => _unknownSetting = unknownSetting;

    public DiagnosticsMode Mode { get; }

    internal static MatrixDiagnostics Parse(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return new(DiagnosticsMode.Off);
        return Enum.TryParse<DiagnosticsMode>(setting.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? new(mode)
            : new(DiagnosticsMode.Off, setting);
    }

    public void LogSetting(ILogger logger)
    {
        if (_unknownSetting != null)
            logger.LogWarning("{Variable} has the unknown value '{Value}'. Use Off, Unhandled or Verbose.", EnvironmentVariable, _unknownSetting);
        else if (Mode != DiagnosticsMode.Off)
            logger.LogInformation("Diagnostics mode {Mode} is on.", Mode);
    }

    /// <summary>
    /// Warns about something the client received but doesn't handle, once per run. The example is logged at debug
    /// level; leave it out for decrypted content.
    /// </summary>
    public void ReportUnhandled(ILogger logger, UnhandledKind kind, string? name, object? example = null)
    {
        name ??= "(none)";
        if (Mode == DiagnosticsMode.Off || IsIgnored(kind, name) || !_reported.TryAdd((kind, name), 0))
            return;
        logger.LogWarning("Unhandled {Kind}: {Name}", kind, name);
        if (example != null && logger.IsEnabled(LogLevel.Debug))
        {
            var json = example is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(example);
            logger.LogDebug("First {Kind} {Name}: {Json}", kind, name, RemoveSecrets(json));
        }
    }

    /// <summary>
    /// Reports fields in <paramref name="json"/> that the model <typeparamref name="T"/> doesn't know. Leave out the
    /// examples for decrypted content.
    /// </summary>
    public void ReportUnknownFields<T>(ILogger logger, JsonElement json, bool withExamples = true)
    {
        if (Mode == DiagnosticsMode.Off)
            return;
        foreach (var (owner, field, value) in FindUnknownFields(json, typeof(T)))
            ReportUnhandled(logger, UnhandledKind.Field, $"{owner}.{field}", withExamples ? value : (object?)null);
    }

    public void LogBody(ILogger logger, string direction, string request, byte[] body)
    {
        if (Mode != DiagnosticsMode.Verbose || !logger.IsEnabled(LogLevel.Debug))
            return;
        logger.LogDebug("{Direction} body of {Request}: {Body}", direction, request, RemoveSecrets(Encoding.UTF8.GetString(body)));
    }

    internal static bool IsIgnored(UnhandledKind kind, string name)
    {
        if (Ignored.ContainsKey((kind, name)))
            return true;
        foreach (var entry in Ignored.Keys)
        {
            if (entry.Kind == kind && entry.Name.EndsWith('*') && name.StartsWith(entry.Name[..^1], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static List<(string Owner, string Field, JsonElement Value)> FindUnknownFields(JsonElement json, Type type)
    {
        List<(string, string, JsonElement)> found = [];
        CollectUnknownFields(json, type, found);
        return found;
    }

    private static void CollectUnknownFields(JsonElement json, Type type, List<(string, string, JsonElement)> found)
    {
        var info = JsonSerializerOptions.Default.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object when json.ValueKind == JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                {
                    var known = info.Properties.FirstOrDefault(p => !p.IsExtensionData && p.Name == property.Name);
                    if (known != null)
                        CollectUnknownFields(property.Value, known.PropertyType, found);
                    else
                        found.Add((type.Name, property.Name, property.Value));
                }
                break;
            case JsonTypeInfoKind.Enumerable when json.ValueKind == JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                    CollectUnknownFields(item, info.ElementType!, found);
                break;
            case JsonTypeInfoKind.Dictionary when json.ValueKind == JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                    CollectUnknownFields(property.Value, info.ElementType!, found);
                break;
        }
    }

    internal static string RemoveSecrets(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return $"({json.Length} characters, not JSON)";
        }
        RemoveSecrets(node);
        return node?.ToJsonString() ?? "null";
    }

    private static void RemoveSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (SecretFields.Contains(name))
                        obj[name] = "(removed)";
                    else
                        RemoveSecrets(obj[name]);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    RemoveSecrets(item);
                break;
        }
    }
}
