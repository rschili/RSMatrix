namespace RSMatrix.Models;

/// <summary>
/// The messages of one room from the newest one back, in server order, and the edits and reactions that point at them.
/// Filled by the sync. Edits, reactions and deletions only apply to messages in here.
/// </summary>
internal sealed class MessageWindow
{
    // The window only grows while a room is in use and is cut back rarely, in one go.
    internal const int MaxCount = 200;
    internal const int CountAfterCut = 100;

    private readonly Lock _lock = new();
    private readonly List<Message> _messages = [];
    private readonly Dictionary<string, Message> _messagesById = new(StringComparer.Ordinal);
    // Edit and reaction event IDs, pointing at the message they change, so their deletion can be undone.
    private readonly Dictionary<string, Message> _relatedEvents = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_lock) return _messages.Count; }
    }

    public Message? Find(string eventId)
    {
        lock (_lock)
            return _messagesById.GetValueOrDefault(eventId);
    }

    public Message? FindRelatedTarget(string relatedEventId)
    {
        lock (_lock)
            return _relatedEvents.GetValueOrDefault(relatedEventId);
    }

    public Message? FindLatestInThread(string threadRootId)
    {
        lock (_lock)
            return _messages.LastOrDefault(m => m.ThreadRootId == threadRootId);
    }

    /// <summary>Returns false if the window already has a message with this ID.</summary>
    public bool Add(Message message)
    {
        lock (_lock)
        {
            if (!_messagesById.TryAdd(message.Id, message))
                return false;
            _messages.Add(message);
            if (_messages.Count > MaxCount)
                Cut();
            return true;
        }
    }

    public void AddRelated(string relatedEventId, Message target)
    {
        lock (_lock)
            _relatedEvents[relatedEventId] = target;
    }

    public void RemoveRelated(string relatedEventId)
    {
        lock (_lock)
            _relatedEvents.Remove(relatedEventId);
    }

    private void Cut()
    {
        var removed = _messages.Count - CountAfterCut;
        foreach (var message in _messages.Take(removed))
            _messagesById.Remove(message.Id);
        _messages.RemoveRange(0, removed);
        foreach (var (relatedEventId, target) in _relatedEvents.ToList())
        {
            if (!_messagesById.ContainsKey(target.Id))
                _relatedEvents.Remove(relatedEventId);
        }
    }
}
