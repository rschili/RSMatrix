namespace RSMatrix.Models;

public enum ClientIssueKind
{
    /// <summary>An encrypted event could not be decrypted, so it is not delivered.</summary>
    DecryptionFailed,

    /// <summary>A device in the room did not receive the key for our encrypted messages and cannot read them.</summary>
    RoomKeyNotShared,

    /// <summary>Joining a room after an invite failed.</summary>
    AutoJoinFailed,

    /// <summary>A sync request failed and the client keeps retrying. Reported once per interruption.</summary>
    SyncInterrupted,

    /// <summary>
    /// The account has a cross-signing identity, but this device is not signed by it, for example because no recovery key
    /// was given. Clients that follow the spec won't share keys with it. See <see cref="MatrixClient.EncryptionState"/>.
    /// </summary>
    DeviceNotCrossSigned,

    /// <summary>A user's cross-signing identity changed. Only devices signed by the new identity are trusted.</summary>
    IdentityChanged
}

/// <summary>Something went wrong that callers may want to know about. The client keeps running.</summary>
public sealed class ClientIssue
{
    public ClientIssueKind Kind { get; }

    /// <summary>A readable description, the same text that goes to the log.</summary>
    public string Message { get; }

    public Room? Room { get; }

    /// <summary>The user involved, like the sender of an undecryptable event or the inviter.</summary>
    public RoomUser? User { get; }

    public string? EventId { get; }

    public Exception? Exception { get; }

    internal ClientIssue(ClientIssueKind kind, string message, Room? room, RoomUser? user, string? eventId, Exception? exception)
    {
        Kind = kind;
        Message = message;
        Room = room;
        User = user;
        EventId = eventId;
        Exception = exception;
    }

    public override string ToString() => $"{Kind}: {Message}";
}
