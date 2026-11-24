using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix;

internal sealed class StoredSession
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }
    [JsonPropertyName("device_id")]
    public required string DeviceId { get; init; }
    [JsonPropertyName("homeserver")]
    public required string Homeserver { get; init; }
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }
    [JsonPropertyName("sync_token")]
    public string? SyncToken { get; init; }

    public override string ToString() => $"Session of {UserId} with device {DeviceId}";
}

/// <summary>
/// Keeps one session per user and device ID in a directory, so a restarted client can resume
/// instead of logging in and syncing from scratch.
/// </summary>
internal sealed class SessionStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly FileStream _lock;

    /// <summary>The stored session, or null if there is none for this user and device.</summary>
    public StoredSession? Session { get; private set; }

    private SessionStore(string path, FileStream fileLock, StoredSession? session)
    {
        _path = path;
        _lock = fileLock;
        Session = session;
    }

    public static SessionStore Open(string directory, string userId, string deviceId)
    {
        PrivateFiles.CreateDirectory(directory);
        // User and device IDs may contain characters that file systems reject.
        var name = "rsmatrix-session-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\n{deviceId}")))[..32];
        var fileLock = PrivateFiles.Lock(Path.Combine(directory, name + ".lock"),
            $"The session of {userId} with device {deviceId} in {directory} is already in use by another client.");
        try
        {
            var path = Path.Combine(directory, name + ".json");
            if (!File.Exists(path))
                return new SessionStore(path, fileLock, null);

            var session = JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"The session file {path} is empty.");
            if (session.Version != 1)
                throw new InvalidOperationException($"The session file {path} has unsupported version {session.Version}.");
            if (session.UserId != userId || session.DeviceId != deviceId)
                throw new InvalidOperationException($"The session file {path} belongs to {session.UserId} with device {session.DeviceId}.");
            return new SessionStore(path, fileLock, session);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    public void Save(StoredSession session)
    {
        PrivateFiles.WriteAtomically(_path, stream => JsonSerializer.Serialize(stream, session, JsonOptions));
        Session = session;
    }

    public void Dispose() => _lock.Dispose();
}
