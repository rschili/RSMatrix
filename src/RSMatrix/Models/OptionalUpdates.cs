namespace RSMatrix.Models;

/// <summary>
/// Kinds of updates the server sends only when asked. Bots usually need none of them; leaving them out saves traffic,
/// since in busy rooms receipts and typing arrive far more often than messages.
/// </summary>
[Flags]
public enum OptionalUpdates
{
    None = 0,
    /// <summary>Read receipts. Without them, <see cref="Room.HasUnread"/> doesn't see reading done on other devices.</summary>
    Receipts = 1,
    /// <summary>Who is typing, see <see cref="Room.TypingUsers"/>.</summary>
    Typing = 2,
    /// <summary>Presence and status texts of users, including the current one, see <see cref="PresenceChanged"/>.</summary>
    Presence = 4,
    All = Receipts | Typing | Presence,
}
