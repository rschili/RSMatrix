namespace RSMatrix.Models;

/// <summary>A room event the cache keeps, already decrypted and read.</summary>
internal abstract record TimelineEntry(string EventId);

/// <summary>A message as the server delivered it, before edits from the timeline are applied.</summary>
/// <param name="IsDeleted">The server delivered it deleted already.</param>
/// <param name="BundledEdit">The latest edit the server bundled into the message, checked against it already.</param>
internal sealed record MessageEntry(string EventId, RoomUser Sender, DateTimeOffset Timestamp, bool IsOwn, bool IsEncrypted,
    string? ReplyToId, string? ThreadRootId, MessageContent Content, bool IsDeleted, MessageEdit? BundledEdit,
    BundledThreadSummary? BundledThread) : TimelineEntry(EventId);

internal sealed record EditEntry(string EventId, string TargetId, string SenderId, bool IsEncrypted, MessageEdit Edit) : TimelineEntry(EventId);

internal sealed record ReactionEntry(string EventId, string TargetId, string Key, RoomUser User) : TimelineEntry(EventId);

internal sealed record RedactionEntry(string EventId, string TargetId, RoomUser RedactedBy) : TimelineEntry(EventId);

/// <summary>The thread summary the server bundles into a thread's first message.</summary>
internal sealed record BundledThreadSummary(int Count, MessageEntry? LatestReply, bool CurrentUserParticipated);

/// <summary>
/// Events of one room in server order, and the messages built from them. The room's own cache holds the events from
/// the newest one back without gaps: the sync adds at the newer end, history walks at the older end. Every edit,
/// reaction and deletion of a message is newer than the message, so for a message in here they are in here too.
/// Cutting removes events from the older end only, which keeps them for the messages loaded again later.
/// A history walk that can't continue the room's cache gets its own cache, whose messages may miss reactions.
/// Callers hold <see cref="Room.TimelineLock"/>.
/// </summary>
internal sealed class TimelineCache(Room room, bool holdsNewest)
{
    // While nobody reads a room's history, its cache is cut back to the newest messages from time to time.
    // A room being read grows up to the ceiling, so a walk back in progress doesn't lose what it loaded.
    internal const int MaxMessages = 200;
    internal const int MessagesAfterCut = 100;
    internal const int CeilingMessages = 2000;
    internal const int MessagesAfterCeilingCut = 1000;
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(5);

    private readonly List<TimelineEntry> _entries = []; // oldest first
    private readonly Dictionary<string, TimelineEntry> _entriesById = new(StringComparer.Ordinal);
    // Edits, reactions, thread replies and deletions by the event they point at, oldest first.
    // They stay when their target is cut, so the target has them again when it loads again.
    private readonly Dictionary<string, List<TimelineEntry>> _relatedByTarget = new(StringComparer.Ordinal);
    private int _messageCount;
    private long? _lastReadAt;

    /// <summary>Whether this cache holds the room's events from the newest one back, so its messages have all their reactions.</summary>
    public bool HoldsNewest => holdsNewest;

    internal TimeProvider Time { get; set; } = TimeProvider.System;

    public int MessageCount => _messageCount;

    public bool IsEmpty => _entries.Count == 0;

    public string? OldestEventId => _entries.Count > 0 ? _entries[0].EventId : null;

    /// <summary>A /messages token for the events before the oldest one, if known.</summary>
    public string? OlderToken { get; set; }

    /// <summary>Whether the oldest event is the first one of the room's visible history.</summary>
    public bool ReachedStart { get; private set; }

    /// <summary>Changes when events leave the cache, so a history walk can tell that its page no longer continues it.</summary>
    public int Generation { get; private set; }

    public bool Contains(string eventId) => _entriesById.ContainsKey(eventId);

    public TimelineEntry? Find(string eventId) => _entriesById.GetValueOrDefault(eventId);

    public MessageEntry? FindMessage(string eventId) => _entriesById.GetValueOrDefault(eventId) as MessageEntry;

    public MessageEntry? FindLatestInThread(string threadRootId)
        => _relatedByTarget.GetValueOrDefault(threadRootId)?.OfType<MessageEntry>().LastOrDefault(m => m.ThreadRootId == threadRootId);

    /// <summary>Marks the room as being read, which postpones cutting.</summary>
    public void MarkRead() => _lastReadAt = Time.GetTimestamp();

    /// <summary>Adds an event from the sync at the newer end.</summary>
    public void Append(TimelineEntry entry)
    {
        if (!_entriesById.TryAdd(entry.EventId, entry))
            return;
        _entries.Add(entry);
        if (TargetOf(entry) is { } targetId)
            GetRelated(targetId).Add(entry);
        if (entry is MessageEntry)
            _messageCount++;
    }

    /// <summary>Adds events from history at the older end. They must directly precede the oldest event.</summary>
    /// <param name="newestFirst">The events of a page, newest first, as history pages come.</param>
    public void Prepend(IReadOnlyList<TimelineEntry> newestFirst, string? olderToken, bool reachedStart)
    {
        List<TimelineEntry> added = [];
        foreach (var entry in newestFirst)
        {
            if (!_entriesById.TryAdd(entry.EventId, entry))
                continue;
            added.Add(entry);
            if (TargetOf(entry) is { } targetId)
                GetRelated(targetId).Insert(0, entry);
            if (entry is MessageEntry)
                _messageCount++;
        }
        added.Reverse();
        _entries.InsertRange(0, added);
        OlderToken = olderToken;
        ReachedStart = reachedStart;
    }

    /// <summary>Removes all events, for a gap after the newest one.</summary>
    public void Clear() => RemoveOldest(_entries.Count);

    /// <summary>Cuts the cache back if it grew too large. Called after the sync added its events.</summary>
    public void Trim()
    {
        if (_messageCount <= MaxMessages)
            return;
        if (_lastReadAt is not { } lastReadAt || Time.GetElapsedTime(lastReadAt) >= ReadTimeout)
            KeepNewestMessages(MessagesAfterCut);
        else if (_messageCount > CeilingMessages)
            KeepNewestMessages(MessagesAfterCeilingCut);
    }

    private void KeepNewestMessages(int count)
    {
        var kept = 0;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i] is MessageEntry && ++kept == count)
            {
                RemoveOldest(i);
                return;
            }
        }
    }

    private void RemoveOldest(int count)
    {
        foreach (var entry in _entries.Take(count))
        {
            _entriesById.Remove(entry.EventId);
            if (TargetOf(entry) is { } targetId && _relatedByTarget.TryGetValue(targetId, out var related))
            {
                related.Remove(entry);
                if (related.Count == 0)
                    _relatedByTarget.Remove(targetId);
            }
            if (entry is MessageEntry)
                _messageCount--;
        }
        _entries.RemoveRange(0, count);
        if (_entries.Count == 0)
            _relatedByTarget.Clear();
        OlderToken = null;
        ReachedStart = false;
        Generation++;
    }

    /// <summary>Up to <paramref name="limit"/> messages older than <paramref name="beforeId"/>, or the newest ones if it is null. Newest first.</summary>
    public List<Message> TakeOlder(string? beforeId, int limit)
    {
        var end = beforeId == null ? _entries.Count : _entries.FindIndex(e => e.EventId == beforeId);
        List<Message> result = [];
        for (var i = end - 1; i >= 0 && result.Count < limit; i--)
        {
            if (_entries[i] is MessageEntry message)
                result.Add(Build(message));
        }
        return result;
    }

    /// <summary>Up to <paramref name="limit"/> replies of a thread older than <paramref name="beforeId"/>, or the newest ones if it is null. Newest first.</summary>
    public List<Message> TakeOlderInThread(string threadRootId, string? beforeId, int limit)
    {
        var replies = _relatedByTarget.GetValueOrDefault(threadRootId)?.OfType<MessageEntry>().Where(m => m.ThreadRootId == threadRootId).ToList() ?? [];
        var end = beforeId == null ? replies.Count : replies.FindIndex(m => m.EventId == beforeId);
        List<Message> result = [];
        for (var i = end - 1; i >= 0 && result.Count < limit; i--)
            result.Add(Build(replies[i]));
        return result;
    }

    /// <summary>The message with everything in the cache applied to it.</summary>
    public Message Build(MessageEntry message)
    {
        var related = (IReadOnlyList<TimelineEntry>?)_relatedByTarget.GetValueOrDefault(message.EventId) ?? [];
        if (message.IsDeleted || related.Any(r => r is RedactionEntry))
        {
            return new Message(message.EventId, room, message.Sender, message.Timestamp, message.IsOwn, message.IsEncrypted, null, null,
                new MessageContent(MessageKind.Unknown, "", null, []), isDeleted: true, hasAllReactions: holdsNewest);
        }

        var edits = related.OfType<EditEntry>().Where(e => IsValidEdit(message, e) && !IsDeleted(e.EventId)).Select(e => e.Edit);
        if (message.BundledEdit is { } bundled && !IsDeleted(bundled.EventId))
            edits = edits.Append(bundled);
        // The newest edit wins, by timestamp, then by the larger event ID.
        var currentEdit = edits.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.EventId, StringComparer.Ordinal).FirstOrDefault();

        var reactions = related.OfType<ReactionEntry>().Where(r => !IsDeleted(r.EventId))
            .Select(r => new ReactionEvent(r.EventId, r.Key, r.User)).ToList();

        return new Message(message.EventId, room, message.Sender, message.Timestamp, message.IsOwn, message.IsEncrypted,
            message.ReplyToId, message.ThreadRootId, message.Content, currentEdit, reactions, BuildThread(message, related),
            hasAllReactions: holdsNewest);
    }

    private ThreadSummary? BuildThread(MessageEntry root, IReadOnlyList<TimelineEntry> related)
    {
        var replies = related.OfType<MessageEntry>().Where(m => m.ThreadRootId == root.EventId && !IsDeleted(m.EventId)).ToList();
        // Replies are newer than their thread's first message, so a cache that holds the newest events has all of them.
        if (root.BundledThread is { } bundled && (!holdsNewest || replies.Count == 0))
        {
            var latest = bundled.LatestReply is { } reply ? Build(FindMessage(reply.EventId) ?? reply) : null;
            return new ThreadSummary(bundled.Count, latest, bundled.CurrentUserParticipated);
        }
        if (replies.Count == 0)
            return null;
        return new ThreadSummary(replies.Count, Build(replies[^1]), root.IsOwn || replies.Any(r => r.IsOwn));
    }

    /// <summary>Only the sender can edit a message, and an encrypted message is only edited by an encrypted edit.</summary>
    public static bool IsValidEdit(MessageEntry message, EditEntry edit)
        => message.Sender.User.UserId.Full == edit.SenderId && (!message.IsEncrypted || edit.IsEncrypted);

    private bool IsDeleted(string eventId)
        => _relatedByTarget.TryGetValue(eventId, out var related) && related.Any(r => r is RedactionEntry);

    private List<TimelineEntry> GetRelated(string targetId)
    {
        if (!_relatedByTarget.TryGetValue(targetId, out var related))
            _relatedByTarget[targetId] = related = [];
        return related;
    }

    private static string? TargetOf(TimelineEntry entry) => entry switch
    {
        EditEntry edit => edit.TargetId,
        ReactionEntry reaction => reaction.TargetId,
        RedactionEntry redaction => redaction.TargetId,
        MessageEntry message => message.ThreadRootId,
        _ => null,
    };
}
