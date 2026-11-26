using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using RSMatrix.Http;

namespace RSMatrix.Models;

/// <summary>A room observed by the client.</summary>
/// <remarks>Messages, notices, edits and reactions are encrypted automatically in encrypted rooms
/// when the client was logged in with a store directory. Without one, they throw
/// <see cref="NotSupportedException"/> once encryption has been observed.</remarks>
public class Room
{
    internal MatrixClient Client { get; }
    internal Room(MatrixId roomId, MatrixClient client)
    {
        RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public MatrixId RoomId { get; }

    /// <summary>
    /// Latest observed state events, keyed by (type, state_key), including custom
    /// events such as uk.half-shot.matrix-hookshot.feed and m.space.parent.
    /// This is a filtered, lazily populated snapshot, not the room's complete state
    /// or an event history. Empty content replaces previous content, it is not merged.
    /// Space parent declarations are not verified hierarchy/permission relationships.
    /// </summary>
    public ImmutableDictionary<(string Type, string StateKey), RoomStateEvent> StateEvents { get; internal set; }
        = ImmutableDictionary<(string Type, string StateKey), RoomStateEvent>.Empty;

    // ConcurrentDictionary is expensive, we only use it for the global things. Inside the room we use ImmutableDictionary instead as there is less data and less movement
    public ImmutableDictionary<string, RoomUser> Users { get; internal set; } = ImmutableDictionary<string, RoomUser>.Empty.WithComparers(StringComparer.Ordinal);
    public string? DisplayName { get; internal set; }
    public MatrixId? CanonicalAlias { get; internal set; }
    public List<MatrixId>? AltAliases { get; internal set; }

    internal MessageWindow Window { get; } = new();

    public RoomEncryption? Encryption { get; internal set; }
    internal bool HasEncryptionState { get; set; }

    /// <summary>
    /// Whether encryption has been observed, even if its algorithm is unknown or
    /// its state was cleared. False does not establish safety before room state is synced.
    /// </summary>
    public bool IsEncrypted => HasEncryptionState || Encryption != null;

    /// <summary>
    /// Whether this room is a direct message room, as indicated by is_direct on the m.room.member event.
    /// </summary>
    public bool IsDirect { get; internal set; }

    public Task SendTypingNotificationAsync(uint? timeoutMS = null)
    {
        return MatrixHelper.PutTypingNotificationAsync(Client.HttpClientParameters, RoomId, Client.CurrentUser, timeoutMS);
    }

    /// <summary>
    /// Sends a message to the room.
    /// </summary>
    /// <param name="body">The plain text, also shown by clients that don't render <paramref name="html"/>.</param>
    /// <param name="html">An HTML version of the body.</param>
    /// <param name="notice">Sends an m.notice, which the spec recommends for bots: other bots don't answer notices.</param>
    /// <param name="replyToId">The message this one replies to, shown as a quote.</param>
    /// <param name="threadRootId">The first message of the thread to send to, or null for the main timeline.</param>
    /// <param name="mentions">Users to notify.</param>
    /// <returns>The event ID of the sent message</returns>
    public Task<string> SendMessageAsync(string body, string? html = null, bool notice = false, string? replyToId = null,
        string? threadRootId = null, IList<MatrixId>? mentions = null)
    {
        // Clients without threads show a thread message as a reply to the thread's latest message.
        var threadFallbackId = threadRootId == null ? null : Window.FindLatestInThread(threadRootId)?.Id ?? threadRootId;
        return SendMessageInternalAsync(notice ? "m.notice" : "m.text", body, html, replyToId, mentions, threadRootId, threadFallbackId);
    }

    internal async Task<string> SendMessageInternalAsync(string msgType, string body, string? html,
        string? replyToId, IList<MatrixId>? mentions, string? threadRootId, string? threadFallbackId)
    {
        ArgumentException.ThrowIfNullOrEmpty(body);
        RelatesTo? relatesTo = null;
        if (threadRootId != null)
            relatesTo = new RelatesTo { RelType = "m.thread", EventId = threadRootId, IsFallingBack = replyToId == null };

        var inReplyTo = replyToId ?? (threadRootId != null ? threadFallbackId : null);
        if (inReplyTo != null)
        {
            relatesTo ??= new RelatesTo();
            relatesTo.InReplyTo = new InReplyTo { EventId = inReplyTo };
        }

        var content = new RoomMessageContent { MsgType = msgType, Body = body, Mentions = ToMentions(mentions), RelatesTo = relatesTo };
        if (html != null)
        {
            content.Format = HtmlFormat;
            content.FormattedBody = html;
        }
        return await SendRoomEventAsync("m.room.message", content).ConfigureAwait(false);
    }

    /// <summary>
    /// Edits a message we sent before. Clients without edit support show the new text with a "* " in front.
    /// A notice or emote stays one when the client has the message in memory.
    /// </summary>
    /// <returns>The event ID of the edit event</returns>
    public Task<string> EditMessageAsync(string originalEventId, string newBody, string? newHtml = null, IList<MatrixId>? mentions = null)
        => EditMessageInternalAsync(originalEventId, Window.Find(originalEventId)?.Kind ?? MessageKind.Text, newBody, newHtml, mentions);

    internal async Task<string> EditMessageInternalAsync(string originalEventId, MessageKind kind, string newBody, string? newHtml,
        IList<MatrixId>? mentions)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalEventId);
        ArgumentException.ThrowIfNullOrEmpty(newBody);
        var msgType = kind switch { MessageKind.Notice => "m.notice", MessageKind.Emote => "m.emote", _ => "m.text" };
        var newContent = new NewMessageContent { MsgType = msgType, Body = newBody, Mentions = ToMentions(mentions) };
        var content = new RoomMessageContent
        {
            MsgType = msgType,
            Body = $"* {newBody}",
            Mentions = newContent.Mentions,
            NewContent = newContent,
            RelatesTo = new RelatesTo { RelType = "m.replace", EventId = originalEventId }
        };
        if (newHtml != null)
        {
            newContent.Format = content.Format = HtmlFormat;
            newContent.FormattedBody = newHtml;
            content.FormattedBody = $"* {newHtml}";
        }
        return await SendRoomEventAsync("m.room.message", content).ConfigureAwait(false);
    }

    private const string HtmlFormat = "org.matrix.custom.html";

    private static Mentions? ToMentions(IList<MatrixId>? mentions)
        => mentions == null ? null : new Mentions { UserIds = mentions.Select(m => m.Full).ToList() };

    /// <summary>
    /// Sends a reaction (annotation) to the specified event.
    /// Per the Matrix spec (v1.7+), the key can be any string, though emoji are the typical use case.
    /// The server will reject duplicate reactions (same event type + key) from the same user
    /// with M_DUPLICATE_ANNOTATION. To change a reaction, redact the original and send a new one.
    /// </summary>
    /// <param name="eventId">The event ID to react to</param>
    /// <param name="key">The reaction key — any string, typically an emoji like 👍</param>
    /// <returns>The event ID of the sent reaction</returns>
    public async Task<string> SendReactionAsync(string eventId, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId, nameof(eventId));
        ArgumentException.ThrowIfNullOrEmpty(key, nameof(key));
        var reaction = new ReactionContent
        {
            RelatesTo = new ReactionRelatesTo { RelType = "m.annotation", EventId = eventId, Key = key }
        };
        return await SendRoomEventAsync("m.reaction", reaction).ConfigureAwait(false);
    }

    /// <summary>
    /// Redacts (deletes) an event in the room.
    /// Redaction is irreversible. The server may remove the event content from storage.
    /// </summary>
    /// <param name="eventId">The event ID to redact</param>
    /// <param name="reason">Optional reason for the redaction</param>
    /// <returns>The event ID of the redaction event</returns>
    public async Task<string> RedactEventAsync(string eventId, string? reason = null)
    {
        var response = await MatrixHelper.PutRedactionAsync(Client.HttpClientParameters, RoomId, eventId, reason).ConfigureAwait(false);
        return response.EventId ?? "";
    }

    /// <summary>
    /// Fetches message history from the room. Messages the room's window holds are returned as the same objects.
    /// The others don't receive later changes.
    /// </summary>
    /// <param name="limit">Maximum number of events to request (default 10)</param>
    /// <param name="from">Pagination token to start from (null for most recent)</param>
    /// <param name="direction">Direction to paginate: "b" for backwards (default), "f" for forwards</param>
    /// <returns>A result containing messages and a pagination token for further fetching</returns>
    public async Task<MessageHistoryResult> FetchMessagesAsync(int limit = 10, string? from = null, string direction = "b")
    {
        var parameters = new MessagesParameters { Dir = direction, Limit = limit, From = from };
        var response = await MatrixHelper.GetMessagesAsync(Client.HttpClientParameters, RoomId, parameters).ConfigureAwait(false);

        var messages = new List<Message>();
        foreach (var chunkEvent in response.Chunk ?? [])
        {
            if (Window.Find(chunkEvent.EventId) is { } known)
            {
                messages.Add(known);
                continue;
            }
            if (Client.CreateMessage(this, chunkEvent) is { } message)
                messages.Add(message);
        }

        return new MessageHistoryResult { Messages = messages, NextToken = response.End };
    }

    private readonly Lock _readMarkerLock = new();
    private readonly SemaphoreSlim _readMarkerRequest = new(1, 1);
    private Message? _readMarkerTarget;
    private Message? _readMarkerSent;

    internal async Task MarkAsReadAsync(Message message)
    {
        lock (_readMarkerLock)
        {
            // A newer message was marked already, so the request for it covers this one.
            if (_readMarkerTarget != null && message.Timestamp < _readMarkerTarget.Timestamp)
                return;
            _readMarkerTarget = message;
        }

        await _readMarkerRequest.WaitAsync(Client.HttpClientParameters.CancellationToken).ConfigureAwait(false);
        try
        {
            Message target;
            lock (_readMarkerLock)
            {
                target = _readMarkerTarget;
                if (target == _readMarkerSent)
                    return;
            }
            await MatrixHelper.PostReadMarkersAsync(Client.HttpClientParameters, RoomId, target.Id, sendReceipt: true).ConfigureAwait(false);
            lock (_readMarkerLock)
                _readMarkerSent = target;
        }
        finally
        {
            _readMarkerRequest.Release();
        }
    }

    private static readonly JsonSerializerOptions SendOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private async Task<string> SendRoomEventAsync(string eventType, object content)
    {
        var json = JsonSerializer.SerializeToNode(content, SendOptions)!.AsObject();
        // A caller can construct another Room wrapper; use the client's canonical room
        // so that observed encryption state cannot be bypassed.
        var room = Client.IsRoomEncrypted(RoomId) ? Client.GetOrAddRoom(RoomId) : this;
        if (IsEncrypted || room.IsEncrypted)
        {
            if (Client.Encryption == null)
                throw new NotSupportedException($"Cannot send plaintext messages to encrypted room {RoomId.Full}: " +
                    "log in with a store directory to enable end-to-end encryption.");
            json = await Client.Encryption.EncryptRoomEventAsync(room, eventType, json).ConfigureAwait(false);
            eventType = "m.room.encrypted";
        }
        var response = await MatrixHelper.PutRoomEventAsync(Client.HttpClientParameters, RoomId, eventType, json).ConfigureAwait(false);
        return response.EventId ?? "";
    }

    public override string ToString()
    {
        return DisplayName ?? RoomId.Localpart.ToString();
    }
}

/// <summary>
/// Result of a message history fetch, containing messages and a pagination token.
/// </summary>
public class MessageHistoryResult
{
    public List<Message> Messages { get; internal init; } = [];
    /// <summary>
    /// Token to pass as 'from' in the next FetchMessagesAsync call to continue paginating.
    /// Null when there are no more messages to fetch.
    /// </summary>
    public string? NextToken { get; internal init; }
}

public class RoomUser
{
    internal RoomUser(User user)
    {
        User = user;
    }

    public User User { get; }
    public string? DisplayName { get; internal set; }
    public Membership? Membership { get; internal set; }

    /// <summary>
    /// RoomUser and User may have display names. This takes care of choosing the right one
    /// </summary>
    public string GetDisplayName()
    {
        return DisplayName ?? User.DisplayName ?? User.UserId.Localpart.ToString();
    }

    public override string ToString()
    {
        return GetDisplayName();
    }
}

public class RoomEncryption
{
    public string Algorithm { get; }
    internal RoomEncryption(string algorithm)
    {
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }
}
