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
    /// <summary>The master cross-signing key we last saw for each user, so we notice when it changes.</summary>
    public Dictionary<string, string> PinnedMasterKeys { get; init; } = [];
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
    /// <summary>
    /// Whether the owner had cross-signed the device that shared the session. Null for sessions stored
    /// before we checked; those are still accepted.
    /// </summary>
    public bool? SenderCrossSigned { get; set; }

    [JsonIgnore]
    public MegolmInboundSession Session => _session ??= MegolmInboundSession.FromExport(Export);
    private MegolmInboundSession? _session;
}

/// <summary>
/// One JSON file in the user's folder of the store directory. The session store's lock keeps other clients away.
/// </summary>
internal sealed class EncryptionStore
{
    private const string StoreFileName = "encryption.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public EncryptionStoreData Data { get; }

    private EncryptionStore(string path, EncryptionStoreData data)
    {
        _path = path;
        Data = data;
    }

    /// <summary>Creates a store with a new account, replacing what the directory held before.</summary>
    public static EncryptionStore Create(string directory, string userId, string deviceId)
    {
        PrivateFiles.CreateDirectory(directory);
        var store = new EncryptionStore(Path.Combine(directory, StoreFileName),
            new EncryptionStoreData { UserId = userId, DeviceId = deviceId, Account = OlmAccount.Create() });
        store.Save();
        return store;
    }

    /// <summary>Loads the store, or creates it if there is none. Throws if it belongs to another user or device.</summary>
    public static EncryptionStore Open(string directory, string userId, string deviceId)
    {
        var path = Path.Combine(directory, StoreFileName);
        if (!File.Exists(path))
            return Create(directory, userId, deviceId);

        var data = JsonSerializer.Deserialize<EncryptionStoreData>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"The encryption store {path} is empty.");
        if (data.Version != 1)
            throw new InvalidOperationException($"The encryption store {path} has unsupported version {data.Version}.");
        if (data.UserId != userId || data.DeviceId != deviceId)
            throw new InvalidOperationException($"The encryption store {path} belongs to {data.UserId} with device {data.DeviceId}.");
        return new EncryptionStore(path, data);
    }

    public static void Delete(string directory) => File.Delete(Path.Combine(directory, StoreFileName));

    public void Save() => PrivateFiles.WriteAtomically(_path, stream => JsonSerializer.Serialize(stream, Data, JsonOptions));
}
