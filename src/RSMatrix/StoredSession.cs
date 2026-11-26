namespace RSMatrix;

/// <summary>
/// A session kept in a store directory. Get it with <see cref="MatrixClient.GetStoredSession"/> and continue it with
/// <see cref="MatrixClient.ResumeAsync"/>.
/// </summary>
public sealed class StoredSession
{
    internal StoredSession(string userId, string deviceId, Uri homeserver, string storeDirectory)
    {
        UserId = userId;
        DeviceId = deviceId;
        Homeserver = homeserver;
        StoreDirectory = storeDirectory;
    }

    public string UserId { get; }

    public string DeviceId { get; }

    public Uri Homeserver { get; }

    public string StoreDirectory { get; }

    public override string ToString() => $"Session of {UserId} with device {DeviceId} at {Homeserver}";
}
