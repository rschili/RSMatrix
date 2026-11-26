namespace RSMatrix.Models;

/// <summary>
/// The messages of one room from the newest one back, in server order, without gaps. The sync adds at the newer end,
/// history walks at the older end. Edits, reactions and deletions from the sync apply to messages in here.
/// Reactions whose message isn't loaded are kept as pending and applied when it loads. Walking back, a reaction
/// always comes before its message, and the server doesn't bundle reactions into messages like it does with edits.
/// </summary>
internal sealed class MessageWindow
{
    // The window only grows while a room is in use and is cut back rarely, in one go.
    internal const int MaxCount = 200;
    internal const int CountAfterCut = 100;
    internal const int MaxPendingReactions = 1000;

    private readonly Lock _lock = new();
    private readonly List<Message> _messages = [];
    private readonly Dictionary<string, Message> _messagesById = new(StringComparer.Ordinal);
    // Insertion order, so the oldest are dropped first.
    private readonly OrderedDictionary<string, ReactionChange> _pendingReactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ReactionChange>> _pendingReactionsByTarget = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_lock) return _messages.Count; }
    }

    /// <summary>The oldest message, or null if the window is empty.</summary>
    public Message? Oldest
    {
        get { lock (_lock) return _messages.Count > 0 ? _messages[0] : null; }
    }

    /// <summary>Whether the oldest message is the first one of the room's visible history.</summary>
    public bool ReachedStart
    {
        get { lock (_lock) return _reachedStart; }
    }
    private bool _reachedStart;

    public Message? Find(string eventId)
    {
        lock (_lock)
            return _messagesById.GetValueOrDefault(eventId);
    }

    /// <summary>The message an edit or reaction in the window belongs to.</summary>
    public Message? FindRelatedTarget(string relatedEventId)
    {
        lock (_lock)
            return _messages.FirstOrDefault(m => m.HasRelatedEvent(relatedEventId));
    }

    public Message? FindLatestInThread(string threadRootId)
    {
        lock (_lock)
            return _messages.LastOrDefault(m => m.ThreadRootId == threadRootId);
    }

    /// <summary>Up to <paramref name="limit"/> messages older than <paramref name="beforeId"/>, or the newest ones if it is null. Newest first.</summary>
    public List<Message> TakeOlder(string? beforeId, int limit)
    {
        lock (_lock)
        {
            var end = beforeId == null ? _messages.Count : _messages.FindIndex(m => m.Id == beforeId);
            if (end < 0)
                return [];
            var start = Math.Max(0, end - limit);
            var result = _messages.GetRange(start, end - start);
            result.Reverse();
            return result;
        }
    }

    /// <summary>Up to <paramref name="limit"/> replies of a thread older than <paramref name="beforeId"/>, or the newest ones if it is null. Newest first.</summary>
    public List<Message> TakeOlderInThread(string threadRootId, string? beforeId, int limit)
    {
        lock (_lock)
        {
            var end = beforeId == null ? _messages.Count : _messages.FindIndex(m => m.Id == beforeId);
            List<Message> result = [];
            for (var i = end - 1; i >= 0 && result.Count < limit; i--)
            {
                if (_messages[i].ThreadRootId == threadRootId)
                    result.Add(_messages[i]);
            }
            return result;
        }
    }

    /// <summary>Adds a message from the sync at the newer end. Returns false if the window already has a message with this ID.</summary>
    public bool Add(Message message)
    {
        lock (_lock)
        {
            if (!_messagesById.TryAdd(message.Id, message))
                return false;
            _messages.Add(message);
            if (message.ThreadRootId != null && _messagesById.TryGetValue(message.ThreadRootId, out var threadRoot))
                threadRoot.AddThreadReply(message);
            if (_messages.Count > MaxCount)
                Cut();
            return true;
        }
    }

    /// <summary>Adds messages from history at the older end. They must directly precede the oldest message.</summary>
    /// <param name="olderMessages">Newest first, as history pages come.</param>
    public void Prepend(IEnumerable<Message> olderMessages, bool reachedStart)
    {
        lock (_lock)
        {
            var added = olderMessages.Where(m => _messagesById.TryAdd(m.Id, m)).Reverse().ToList();
            _messages.InsertRange(0, added);
            _reachedStart = reachedStart;
        }
    }

    public void AddPendingReaction(ReactionChange change)
    {
        lock (_lock)
        {
            if (!_pendingReactions.TryAdd(change.Reaction.EventId, change))
                return;
            if (!_pendingReactionsByTarget.TryGetValue(change.TargetId, out var changes))
                _pendingReactionsByTarget[change.TargetId] = changes = [];
            changes.Add(change);
            if (_pendingReactions.Count > MaxPendingReactions)
                RemovePendingReactionCore(_pendingReactions.GetAt(0).Key);
        }
    }

    /// <summary>
    /// The pending reactions of a message. They stay pending after this, so the message gets them again
    /// when it is loaded again later, for example after the window was cut.
    /// </summary>
    public List<ReactionEvent> GetPendingReactions(string targetId)
    {
        lock (_lock)
            return _pendingReactionsByTarget.TryGetValue(targetId, out var changes) ? changes.Select(c => c.Reaction).ToList() : [];
    }

    public void RemovePendingReaction(string eventId)
    {
        lock (_lock)
            RemovePendingReactionCore(eventId);
    }

    private void RemovePendingReactionCore(string eventId)
    {
        if (!_pendingReactions.Remove(eventId, out var change))
            return;
        var changes = _pendingReactionsByTarget[change.TargetId];
        changes.Remove(change);
        if (changes.Count == 0)
            _pendingReactionsByTarget.Remove(change.TargetId);
    }

    private void Cut()
    {
        var removed = _messages.Count - CountAfterCut;
        foreach (var message in _messages.Take(removed))
            _messagesById.Remove(message.Id);
        _messages.RemoveRange(0, removed);
        _reachedStart = false;
    }
}
