using System.Collections.Immutable;

namespace RSMatrix.Models;

public enum MessageKind
{
    Text,
    Notice,
    Emote,
    Image,
    File,
    Audio,
    Video,
    /// <summary>A message type RSMatrix doesn't know. <see cref="Message.Body"/> holds the sender's text fallback.</summary>
    Unknown
}

/// <summary>One reaction key on a message and the users who reacted with it, in the order they did.</summary>
public sealed record Reaction(string Key, IReadOnlyList<RoomUser> Users);

/// <summary>
/// A message in a room, in its current state: edits applied, deletion and reactions.
/// While the message is in its room's window, the client updates this object and reports each change on
/// <see cref="MatrixClient.Updates"/>. Messages fetched on their own don't receive later changes.
/// </summary>
public sealed class Message
{
    public string Id { get; }
    public Room Room { get; }
    public RoomUser Sender { get; }
    public DateTimeOffset Timestamp { get; }

    /// <summary>Sent by the logged-in user, from this or another device.</summary>
    public bool IsOwn { get; }

    /// <summary>
    /// Whether the message arrived end-to-end encrypted. A message that is not encrypted
    /// in a room where <see cref="Room.IsEncrypted"/> is true could be read by the server.
    /// </summary>
    public bool IsEncrypted { get; }

    /// <summary>The message this one replies to.</summary>
    public string? ReplyToId { get; }

    /// <summary>The first message of the thread this message belongs to. Null for messages in the main timeline and thread roots.</summary>
    public string? ThreadRootId { get; }

    public MessageKind Kind => _state.Content.Kind;

    /// <summary>The text of the message with the latest edit applied. Empty once deleted.</summary>
    public string Body => _state.Content.Body;

    /// <summary>The HTML version of <see cref="Body"/>, if the sender provided one.</summary>
    public string? Html => _state.Content.Html;

    public IReadOnlyList<RoomUser> Mentions => _state.Content.Mentions;

    /// <summary>When the edit that <see cref="Body"/> shows was sent. Null if the message wasn't edited.</summary>
    public DateTimeOffset? EditedAt => _state.CurrentEdit?.Timestamp;

    public bool IsDeleted => _state.IsDeleted;

    public IReadOnlyList<Reaction> Reactions => _state.Reactions;

    // Replaced as a whole, so a reader on another thread sees the old state or the new one, never a mix.
    private volatile MessageState _state;

    internal Message(string id, Room room, RoomUser sender, DateTimeOffset timestamp, bool isOwn, bool isEncrypted,
        string? replyToId, string? threadRootId, MessageContent content)
    {
        Id = id;
        Room = room;
        Sender = sender;
        Timestamp = timestamp;
        IsOwn = isOwn;
        IsEncrypted = isEncrypted;
        ReplyToId = replyToId;
        ThreadRootId = threadRootId;
        _state = MessageState.Create(content, [], [], isDeleted: false);
    }

    /// <summary>
    /// Answers this message. Inside a thread, the answer stays in the thread.
    /// </summary>
    /// <param name="body">The plain text, also shown by clients that don't render <paramref name="html"/>.</param>
    /// <param name="html">An HTML version of the body.</param>
    /// <param name="notice">Sends an m.notice, which the spec recommends for bots: other bots don't answer notices.</param>
    /// <param name="quote">Shows this message as a quote above the answer. Without it, the answer just continues the thread or the room.</param>
    /// <param name="mentions">Users to notify.</param>
    /// <returns>The event ID of the sent message</returns>
    public Task<string> ReplyAsync(string body, string? html = null, bool notice = false, bool quote = true, IList<MatrixId>? mentions = null)
        => Room.SendMessageInternalAsync(notice ? "m.notice" : "m.text", body, html, quote ? Id : null, mentions, ThreadRootId, Id);

    /// <summary>
    /// Sends a reaction to this message. The key can be any string, typically an emoji like 👍.
    /// The server rejects a second reaction with the same key from the same user with M_DUPLICATE_ANNOTATION.
    /// </summary>
    /// <returns>The event ID of the sent reaction</returns>
    public Task<string> SendReactionAsync(string key)
        => Room.SendReactionAsync(Id, key);

    /// <summary>
    /// Removes our reaction with this key, by deleting the reaction event. Does nothing if the client hasn't seen
    /// such a reaction from us, for example when the sync hasn't delivered a reaction we just sent.
    /// </summary>
    public async Task RemoveReactionAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        foreach (var reaction in _state.ReactionEvents)
        {
            if (reaction.Key == key && reaction.User.User.UserId.Equals(Room.Client.CurrentUser))
                await Room.RedactEventAsync(reaction.EventId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Marks the room as read up to this message: sets our read receipt, which other members see,
    /// and the read marker our other devices use. Calls for several messages of one room are combined,
    /// so this can be called for every message without running into rate limits.
    /// </summary>
    public Task MarkAsReadAsync() => Room.MarkAsReadAsync(this);

    /// <summary>
    /// Edits this message, which only works for our own messages. Clients without edit support show the new text with a "* " in front.
    /// </summary>
    /// <returns>The event ID of the edit event</returns>
    public Task<string> EditAsync(string newBody, string? newHtml = null, IList<MatrixId>? mentions = null)
        => Room.EditMessageInternalAsync(Id, Kind, newBody, newHtml, mentions);

    /// <summary>
    /// Deletes this message. Others still see that a message was there.
    /// </summary>
    /// <param name="reason">Optional reason for the deletion</param>
    /// <returns>The event ID of the redaction event</returns>
    public Task<string> RedactAsync(string? reason = null)
        => Room.RedactEventAsync(Id, reason);

    public override string ToString() => $"{Sender}: {Body}";

    /// <summary>Returns false if the edit is already known.</summary>
    internal bool AddEdit(MessageEdit edit, out bool contentChanged)
    {
        var state = _state;
        contentChanged = false;
        if (state.IsDeleted || state.Edits.Any(e => e.EventId == edit.EventId))
            return false;
        _state = MessageState.Create(state.Original, state.Edits.Add(edit), state.ReactionEvents, state.IsDeleted);
        contentChanged = !ReferenceEquals(state.Content, _state.Content);
        return true;
    }

    /// <summary>Returns true if the content changed, because the removed edit was the one shown.</summary>
    internal bool RemoveEdit(string editEventId)
    {
        var state = _state;
        if (state.IsDeleted || !state.Edits.Any(e => e.EventId == editEventId))
            return false;
        _state = MessageState.Create(state.Original, state.Edits.RemoveAll(e => e.EventId == editEventId), state.ReactionEvents, state.IsDeleted);
        return !ReferenceEquals(state.Content, _state.Content);
    }

    /// <summary>Returns false if the reaction is already known. A user's second reaction with the same key counts once.</summary>
    internal bool AddReaction(ReactionEvent reaction, out bool isNewKey)
    {
        var state = _state;
        isNewKey = false;
        if (state.IsDeleted || state.ReactionEvents.Any(r => r.EventId == reaction.EventId))
            return false;
        isNewKey = !state.ReactionEvents.Any(r => r.Key == reaction.Key && r.User == reaction.User);
        _state = MessageState.Create(state.Original, state.Edits, state.ReactionEvents.Add(reaction), state.IsDeleted);
        return true;
    }

    /// <summary>Returns the removed reaction if its user has no other reaction with the same key.</summary>
    internal ReactionEvent? RemoveReaction(string reactionEventId)
    {
        var state = _state;
        var removed = state.ReactionEvents.FirstOrDefault(r => r.EventId == reactionEventId);
        if (state.IsDeleted || removed == null)
            return null;
        _state = MessageState.Create(state.Original, state.Edits, state.ReactionEvents.Remove(removed), state.IsDeleted);
        return _state.ReactionEvents.Any(r => r.Key == removed.Key && r.User == removed.User) ? null : removed;
    }

    /// <summary>Returns false if the message was deleted before.</summary>
    internal bool Delete()
    {
        if (_state.IsDeleted)
            return false;
        _state = MessageState.Create(_state.Original with { Body = "", Html = null, Mentions = [] }, [], [], isDeleted: true);
        return true;
    }
}

internal sealed record MessageContent(MessageKind Kind, string Body, string? Html, IReadOnlyList<RoomUser> Mentions);

internal sealed record MessageEdit(string EventId, DateTimeOffset Timestamp, MessageContent Content);

internal sealed record ReactionEvent(string EventId, string Key, RoomUser User);

internal sealed class MessageState
{
    public required MessageContent Original { get; init; }
    public required ImmutableList<MessageEdit> Edits { get; init; }
    public required ImmutableList<ReactionEvent> ReactionEvents { get; init; }
    public required bool IsDeleted { get; init; }
    public required MessageEdit? CurrentEdit { get; init; }
    public required IReadOnlyList<Reaction> Reactions { get; init; }
    public MessageContent Content => CurrentEdit?.Content ?? Original;

    public static MessageState Create(MessageContent original, ImmutableList<MessageEdit> edits,
        ImmutableList<ReactionEvent> reactionEvents, bool isDeleted) => new()
    {
        Original = original,
        Edits = edits,
        ReactionEvents = reactionEvents,
        IsDeleted = isDeleted,
        // The newest edit wins, by timestamp, then by the larger event ID.
        CurrentEdit = edits.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.EventId, StringComparer.Ordinal).FirstOrDefault(),
        Reactions = reactionEvents
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g => new Reaction(g.Key, g.Select(r => r.User).Distinct().ToList()))
            .ToList(),
    };
}
