using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix.Crypto;

/// <summary>Everything the device needs to keep across restarts. Contains private keys.</summary>
internal sealed class EncryptionStoreData
{
    public int Version { get; init; } = 1;
    public required string UserId { get; init; }
    public required string DeviceId { get; init; }
    public required OlmAccount Account { get; init; }
    public bool DeviceKeysUploaded { get; set; }
    /// <summary>Olm sessions, keyed by the other device's Curve25519 identity key.</summary>
    public Dictionary<string, List<OlmSession>> OlmSessions { get; init; } = [];
    /// <summary>Megolm sessions for decrypting room messages, keyed by session ID.</summary>
    public ConcurrentDictionary<string, InboundGroupSession> InboundGroupSessions { get; init; } = new();
}

/// <summary>A received Megolm session and who it came from.</summary>
internal sealed class InboundGroupSession
{
    public required string RoomId { get; init; }
    public required string SenderUserId { get; init; }
    /// <summary>Curve25519 key of the device that shared the session with us over Olm.</summary>
    public required string SenderKey { get; init; }
    /// <summary>The unsigned Megolm export, starting at the earliest index we can decrypt.</summary>
    public required string Export { get; init; }

    [JsonIgnore]
    public MegolmInboundSession Session => _session ??= MegolmInboundSession.FromExport(Export);
    private MegolmInboundSession? _session;
}

/// <summary>
/// One JSON file in a caller-chosen directory. A lock file prevents two processes
/// from using the same device keys at once.
/// </summary>
internal sealed class EncryptionStore : IDisposable
{
    private const string StoreFileName = "rsmatrix-encryption.json";
    private const string LockFileName = "rsmatrix-encryption.lock";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly FileStream _lock;

    public EncryptionStoreData Data { get; }
    /// <summary>True if the store was created now, rather than loaded from disk.</summary>
    public bool IsNew { get; }

    private EncryptionStore(string path, FileStream fileLock, EncryptionStoreData data, bool isNew)
    {
        _path = path;
        _lock = fileLock;
        Data = data;
        IsNew = isNew;
    }

    /// <summary>Opens or creates the store. Throws if it belongs to another user or device, or is in use.</summary>
    public static EncryptionStore Open(string directory, string userId, string deviceId)
    {
        PrivateFiles.CreateDirectory(directory);
        var fileLock = PrivateFiles.Lock(Path.Combine(directory, LockFileName),
            $"The encryption store in {directory} is already in use by another client.");

        try
        {
            var path = Path.Combine(directory, StoreFileName);
            if (!File.Exists(path))
            {
                var created = new EncryptionStoreData { UserId = userId, DeviceId = deviceId, Account = OlmAccount.Create() };
                var store = new EncryptionStore(path, fileLock, created, isNew: true);
                store.Save();
                return store;
            }

            var data = JsonSerializer.Deserialize<EncryptionStoreData>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"The encryption store {path} is empty.");
            if (data.Version != 1)
                throw new InvalidOperationException($"The encryption store {path} has unsupported version {data.Version}.");
            if (data.UserId != userId || data.DeviceId != deviceId)
                throw new InvalidOperationException(
                    $"The encryption store {path} belongs to {data.UserId} with device {data.DeviceId}. " +
                    "Use that device ID, or a different directory for a new device.");
            return new EncryptionStore(path, fileLock, data, isNew: false);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    public void Save() => PrivateFiles.WriteAtomically(_path, stream => JsonSerializer.Serialize(stream, Data, JsonOptions));

    public void Dispose() => _lock.Dispose();
}
