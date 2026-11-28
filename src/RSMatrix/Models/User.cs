namespace RSMatrix.Models;

public class User
{
    internal User(MatrixId userId)
    {
        UserId = userId;
    }

    public MatrixId UserId { get; }
    public string? AvatarUrl { get; internal set; }
    /// <summary>The name from the user's profile, or null when the client hasn't received one.</summary>
    public string? ProfileName { get; internal set; }

    /// <summary>The name to show: the profile name, or the user ID without the server.</summary>
    public string DisplayName => ProfileName ?? UserId.Localpart.ToString();

    /// <summary>
    /// Whether the user is online, away or offline, as the server reported last. Null until the client received it, and
    /// always null without <see cref="OptionalUpdates.Presence"/>. Changes come as <see cref="PresenceChanged"/>.
    /// </summary>
    public Presence? Presence { get; internal set; }
    /// <summary>A text the user set to go with their presence, like "lunch".</summary>
    public string? StatusMessage { get; internal set; }
    /// <summary>The server considers the user active right now; treat them as active regardless of <see cref="LastActiveAt"/>.</summary>
    public bool? CurrentlyActive { get; internal set; }
    /// <summary>When the user was last active, as of the last presence the client received. Not updated in between.</summary>
    public DateTimeOffset? LastActiveAt { get; internal set; }

    public override string ToString() => DisplayName;
}
