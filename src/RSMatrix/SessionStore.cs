using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix;

/// <summary>The session file. Contains the access token.</summary>
internal sealed class SessionData
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
/// One user's folder in a store directory: the session file, the encryption store and a lock file
/// that keeps a second client away while one uses the folder.
/// </summary>
internal sealed class SessionStore : IDisposable
{
    private const string SessionFileName = "session.json";
    private const string LockFileName = "lock";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly FileStream _lock;
    private string SessionPath => Path.Combine(Directory, SessionFileName);

    public string UserId { get; }
    /// <summary>The user's folder, which also holds the encryption store.</summary>
    public string Directory { get; }
    /// <summary>The stored session, or null if there is none for this user.</summary>
    public SessionData? Session { get; private set; }

    private SessionStore(string userId, string directory, FileStream fileLock, SessionData? session)
    {
        UserId = userId;
        Directory = directory;
        _lock = fileLock;
        Session = session;
    }

    /// <summary>Reads the stored session without taking the lock.</summary>
    public static SessionData? Read(string storeDirectory, string userId)
        => ReadSession(Path.Combine(GetUserDirectory(storeDirectory, userId), SessionFileName), userId);

    public static SessionStore Open(string storeDirectory, string userId)
    {
        var directory = GetUserDirectory(storeDirectory, userId);
        PrivateFiles.CreateDirectory(directory);
        var fileLock = PrivateFiles.Lock(Path.Combine(directory, LockFileName),
            $"The session of {userId} in {storeDirectory} is already in use by another client.");
        try
        {
            return new SessionStore(userId, directory, fileLock, ReadSession(Path.Combine(directory, SessionFileName), userId));
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    public void Save(SessionData session)
    {
        PrivateFiles.WriteAtomically(SessionPath, stream => JsonSerializer.Serialize(stream, session, JsonOptions));
        Session = session;
    }

    /// <summary>Deletes the session and the encryption keys, after the server deleted the device.</summary>
    public void Delete()
    {
        File.Delete(SessionPath);
        Crypto.EncryptionStore.Delete(Directory);
        Session = null;
    }

    public void Dispose() => _lock.Dispose();

    // User IDs may contain characters that file systems reject.
    private static string GetUserDirectory(string storeDirectory, string userId)
        => Path.Combine(storeDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..32]);

    private static SessionData? ReadSession(string path, string userId)
    {
        if (!File.Exists(path))
            return null;
        var session = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"The session file {path} is empty.");
        if (session.Version != 1)
            throw new InvalidOperationException($"The session file {path} has unsupported version {session.Version}.");
        if (session.UserId != userId)
            throw new InvalidOperationException($"The session file {path} belongs to {session.UserId}.");
        return session;
    }
}
