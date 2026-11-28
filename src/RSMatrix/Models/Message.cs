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

/// <summary>The replies to a message that starts a thread.</summary>
/// <param name="CurrentUserParticipated">The logged-in user started the thread or replied to it.</param>
public sealed record ThreadSummary(int ReplyCount, Message? LatestReply, bool CurrentUserParticipated);

/// <summary>
/// A message in a room as it was when the client built this object: edits applied, deletion, reactions and thread.
/// It doesn't change afterwards. Later changes come out on <see cref="MatrixClient.Updates"/>, each with a new
/// <see cref="Message"/> for the same <see cref="Id"/>.
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

    public MessageKind Kind => _content.Kind;

    /// <summary>The text of the message with the latest edit applied. Empty once deleted.</summary>
    public string Body => _content.Body;

    /// <summary>The HTML version of <see cref="Body"/>, if the sender provided one.</summary>
    public string? Html => _content.Html;

    public IReadOnlyList<RoomUser> Mentions => _content.Mentions;

    /// <summary>When the edit that <see cref="Body"/> shows was sent. Null if the message wasn't edited.</summary>
    public DateTimeOffset? EditedAt => _currentEdit?.Timestamp;

    public bool IsDeleted { get; }

    public IReadOnlyList<Reaction> Reactions { get; }

    /// <summary>
    /// Whether <see cref="Reactions"/> is complete. True for messages the client reached by reading back from the
    /// newest one. Matrix servers don't send reactions along with a message, so messages loaded on their own, like
    /// reply targets, only have the reactions the client happened to see. <see cref="GetReactionsAsync"/> asks the server.
    /// </summary>
    public bool HasAllReactions { get; }

    /// <summary>
    /// Set on messages that start a thread. A new reply comes out as <see cref="NewMessage"/> with
    /// <see cref="ThreadRootId"/> pointing here.
    /// </summary>
    public ThreadSummary? Thread { get; }

    private readonly MessageContent _content;
    private readonly MessageEdit? _currentEdit;

    internal IReadOnlyList<ReactionEvent> ReactionEvents { get; }

    /// <summary>The edit <see cref="Body"/> shows, to tell whether an edit changed the message.</summary>
    internal string? CurrentEditId => _currentEdit?.EventId;

    /// <summary>A /messages token for the events before this message, if it is the oldest of a page outside the room's cache.</summary>
    internal string? HistoryToken { get; set; }

    /// <summary>A /relations token for the older replies of this message's thread, if a thread page told us one.</summary>
    internal string? ThreadHistoryToken { get; set; }

    internal Message(string id, Room room, RoomUser sender, DateTimeOffset timestamp, bool isOwn, bool isEncrypted,
        string? replyToId, string? threadRootId, MessageContent content, MessageEdit? currentEdit = null,
        IReadOnlyList<ReactionEvent>? reactionEvents = null, ThreadSummary? thread = null, bool isDeleted = false,
        bool hasAllReactions = false)
    {
        Id = id;
        Room = room;
        Sender = sender;
        Timestamp = timestamp;
        IsOwn = isOwn;
        IsEncrypted = isEncrypted;
        ReplyToId = replyToId;
        ThreadRootId = threadRootId;
        _content = currentEdit?.Content ?? content;
        _currentEdit = currentEdit;
        ReactionEvents = reactionEvents ?? [];
        Reactions = ToReactions(ReactionEvents);
        Thread = thread;
        IsDeleted = isDeleted;
        HasAllReactions = hasAllReactions;
    }

    /// <summary>Groups reaction events by key. A user's second reaction with the same key counts once.</summary>
    internal static IReadOnlyList<Reaction> ToReactions(IEnumerable<ReactionEvent> reactionEvents)
        => reactionEvents
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g => new Reaction(g.Key, g.Select(r => r.User).Distinct().ToList()))
            .ToList();

    internal bool HasReaction(string key, RoomUser user) => ReactionEvents.Any(r => r.Key == key && r.User == user);

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

    /// <summary>The message this one replies to, see <see cref="Room.GetMessageAsync"/>. Null if this isn't a reply.</summary>
    public Task<Message?> GetReplyToAsync(CancellationToken cancellationToken = default)
        => ReplyToId == null ? Task.FromResult<Message?>(null) : Room.GetMessageAsync(ReplyToId, cancellationToken);

    /// <summary>
    /// Fetches this message's event from the server as indented JSON, for inspecting it. An encrypted message shows its
    /// decrypted type and content if this client can decrypt it. Nothing of it stays in memory.
    /// </summary>
    public Task<string> GetSourceAsync(CancellationToken cancellationToken = default)
        => Room.GetEventSourceAsync(Id, cancellationToken);

    /// <summary>
    /// All reactions to this message. Returns <see cref="Reactions"/> if <see cref="HasAllReactions"/> is true and
    /// asks the server otherwise, which takes one or more requests. The client runs a few of these at a time.
    /// </summary>
    public Task<IReadOnlyList<Reaction>> GetReactionsAsync(CancellationToken cancellationToken = default)
        => HasAllReactions ? Task.FromResult(Reactions) : Room.GetReactionsAsync(Id, cancellationToken);

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
        foreach (var reaction in ReactionEvents)
        {
            if (reaction.Key == key && reaction.User.User.UserId.Equals(Room.Client.CurrentUser.UserId))
                await Room.RedactEventAsync(reaction.EventId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Marks the room as read up to this message, for a user who scrolled up and reads older messages. Like
    /// <see cref="Room.MarkAsReadAsync"/>, it never moves the read receipt backwards, and on our own message it points at
    /// the newest event from someone else before it.
    /// </summary>
    public Task MarkAsReadAsync(CancellationToken cancellationToken = default) => Room.MarkAsReadUpToAsync(this, cancellationToken);

    /// <summary>
    /// Edits this message, which only works for our own messages. Clients without edit support show the new text with a "* " in front.
    /// </summary>
    /// <returns>The event ID of the edit event</returns>
    public Task<string> EditAsync(string newBody, string? newHtml = null, IList<MatrixId>? mentions = null)
        => Room.EditMessageInternalAsync(Id, Kind, newBody, newHtml, mentions, Mentions);

    /// <summary>
    /// Deletes this message. Others still see that a message was there.
    /// </summary>
    /// <param name="reason">Optional reason for the deletion</param>
    /// <returns>The event ID of the redaction event</returns>
    public Task<string> RedactAsync(string? reason = null)
        => Room.RedactEventAsync(Id, reason);

    public override string ToString() => $"{Sender}: {Body}";
}

internal sealed record MessageContent(MessageKind Kind, string Body, string? Html, IReadOnlyList<RoomUser> Mentions);

internal sealed record MessageEdit(string EventId, DateTimeOffset Timestamp, MessageContent Content);

internal sealed record ReactionEvent(string EventId, string Key, RoomUser User);
