namespace RSMatrix.Models;

/// <summary>
/// The current user's read position in a room: the newest timeline events from the sync, the user's own read receipt
/// and the server's notification counts. Guarded by <see cref="Room.TimelineLock"/>.
/// </summary>
internal sealed class ReadState
{
    // A receipt further back than this many events counts as older than all of them.
    internal const int MaxEvents = 100;

    private readonly List<(string EventId, bool IsOwn, bool MakesUnread)> _events = []; // oldest first

    /// <summary>The event the user's newest read receipt points at, from any of their devices.</summary>
    public string? ReceiptEventId { get; private set; }

    public int NotificationCount { get; set; }

    public int MentionCount { get; set; }

    /// <param name="makesUnread">Whether the event is a message from someone else.</param>
    public void Add(string eventId, bool isOwn, bool makesUnread)
    {
        if (IndexOf(eventId) >= 0)
            return;
        _events.Add((eventId, isOwn, makesUnread));
        if (_events.Count > MaxEvents)
            _events.RemoveAt(0);
    }

    /// <summary>Forgets the events after a gap in the sync, since the events of the gap are unknown.</summary>
    public void Clear() => _events.Clear();

    /// <summary>Takes a receipt of the user, unless it points before the one they have.</summary>
    public void SetReceipt(string eventId)
    {
        if (!IsBefore(eventId, ReceiptEventId))
            ReceiptEventId = eventId;
    }

    /// <summary>
    /// Whether someone else wrote a message after the user's read receipt and after the user's own newest event,
    /// or the server counts notifications.
    /// </summary>
    public bool HasUnread
    {
        get
        {
            if (NotificationCount > 0)
                return true;
            var readUpTo = Math.Max(IndexOf(ReceiptEventId), _events.FindLastIndex(e => e.IsOwn));
            for (var i = readUpTo + 1; i < _events.Count; i++)
            {
                if (_events[i].MakesUnread)
                    return true;
            }
            return false;
        }
    }

    /// <summary>The newest event of any type from someone else. Read receipts point there, never at our own events.</summary>
    public string? NewestFromOthers => _events.FindLast(e => !e.IsOwn).EventId;

    /// <summary>The newest event from someone else at or before the given one, or null if none is known.</summary>
    public string? FromOthersUpTo(string eventId)
    {
        var index = IndexOf(eventId);
        var found = index < 0 ? -1 : _events.FindLastIndex(index, index + 1, e => !e.IsOwn);
        return found < 0 ? null : _events[found].EventId;
    }

    /// <summary>
    /// Whether <paramref name="eventId"/> comes before <paramref name="other"/>. An unknown event is older than all
    /// known ones; two unknown events aren't ordered.
    /// </summary>
    public bool IsBefore(string eventId, string? other) => other != null && IndexOf(eventId) < IndexOf(other);

    /// <summary>Records a receipt the server accepted. Returns whether the room's unread state changed.</summary>
    public bool MarkAsRead(string eventId)
    {
        var before = (NotificationCount, MentionCount, HasUnread);
        SetReceipt(eventId);
        if (eventId == NewestFromOthers)
        {
            NotificationCount = 0;
            MentionCount = 0;
        }
        return before != (NotificationCount, MentionCount, HasUnread);
    }

    private int IndexOf(string? eventId) => eventId == null ? -1 : _events.FindIndex(e => e.EventId == eventId);
}
