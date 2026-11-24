using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using RSMatrix.Http;

namespace RSMatrix.Models;

/// <summary>A room observed by the client.</summary>
/// <remarks>Messages, notices, edits and reactions are encrypted automatically in encrypted rooms
/// when the client was connected with an encryption store. Without one, they throw
/// <see cref="NotSupportedException"/> once encryption has been observed.</remarks>
public class Room
{
    internal MatrixTextClient Client { get; }
    internal Room(MatrixId roomId, MatrixTextClient client)
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

    public ReceivedTextMessage? LastMessage { get; internal set; }

    public string? LastReceiptEventId { get; internal set; }
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

    public Task<string> SendTextMessageAsync(string body, string? inReplyTo, IList<MatrixId>? mentions)
     => SendMessageInternalAsync("m.text", body, null, inReplyTo, mentions);

    public Task<string> SendHtmlMessageAsync(string body, string htmlBody, string? inReplyTo, IList<MatrixId>? mentions)
        => SendMessageInternalAsync("m.text", body, (Format: "org.matrix.custom.html", FormattedBody: htmlBody), inReplyTo, mentions);

    /// <summary>
    /// Sends a notice (bot message) to the room.
    /// Per the Matrix spec, m.notice should be used for automated/bot messages
    /// to avoid notification loops between bots.
    /// </summary>
    public Task<string> SendNoticeAsync(string body, string? inReplyTo, IList<MatrixId>? mentions)
        => SendMessageInternalAsync("m.notice", body, null, inReplyTo, mentions);

    /// <summary>
    /// Sends an HTML notice (bot message) to the room.
    /// </summary>
    public Task<string> SendHtmlNoticeAsync(string body, string htmlBody, string? inReplyTo, IList<MatrixId>? mentions)
        => SendMessageInternalAsync("m.notice", body, (Format: "org.matrix.custom.html", FormattedBody: htmlBody), inReplyTo, mentions);

    private Task<string> SendMessageInternalAsync(string msgType, string body, (string Format, string FormattedBody)? formatted, string? inReplyTo, IList<MatrixId>? mentions)
        => SendMessageInternalAsync(msgType, body, formatted, inReplyTo, mentions, null, null);

    internal async Task<string> SendMessageInternalAsync(string msgType, string body, (string Format, string FormattedBody)? formatted,
        string? inReplyTo, IList<MatrixId>? mentions, string? threadRootId, string? threadFallbackEventId)
    {
        RoomMessageMention? roomMessageMention = null;
        if (mentions != null)
        {
            roomMessageMention = new RoomMessageMention { UserIds = mentions.Select(m => m.Full).ToList() };
        }
        RoomMessageRelatesTo? relatesTo = null;
        if (threadRootId != null)
        {
            relatesTo = new RoomMessageRelatesTo
            {
                RelType = "m.thread",
                EventId = threadRootId,
                IsFallingBack = inReplyTo == null
            };
        }

        // A thread continuation uses the received event as its fallback, not as the thread root.
        var replyTarget = inReplyTo ?? (threadRootId != null ? threadFallbackEventId : null);
        if (replyTarget != null)
        {
            relatesTo ??= new RoomMessageRelatesTo();
            relatesTo.InReplyTo = new RoomMessageInReplyTo { EventId = replyTarget };
        }

        var messageRequest = new MessageRequest { MsgType = msgType, Body = body, Mentions = roomMessageMention, RelatesTo = relatesTo };
        if (formatted.HasValue)
        {
            messageRequest.Format = formatted.Value.Format;
            messageRequest.FormattedBody = formatted.Value.FormattedBody;
        }
        return await SendRoomEventAsync("m.room.message", messageRequest).ConfigureAwait(false);
    }

    /// <summary>
    /// Edits a previously sent message with new text content.
    /// Per the Matrix spec, the body is prefixed with "* " as a fallback for clients that don't support edits.
    /// </summary>
    /// <param name="originalEventId">The event ID of the message to edit</param>
    /// <param name="newBody">The new message body</param>
    /// <param name="mentions">Optional user mentions for the edited message</param>
    /// <returns>The event ID of the edit event</returns>
    public Task<string> EditMessageAsync(string originalEventId, string newBody, IList<MatrixId>? mentions = null)
        => EditMessageInternalAsync(originalEventId, "m.text", newBody, null, mentions);

    /// <summary>
    /// Edits a previously sent message with new HTML content.
    /// </summary>
    public Task<string> EditHtmlMessageAsync(string originalEventId, string newBody, string newHtmlBody, IList<MatrixId>? mentions = null)
        => EditMessageInternalAsync(originalEventId, "m.text", newBody, (Format: "org.matrix.custom.html", FormattedBody: newHtmlBody), mentions);

    private async Task<string> EditMessageInternalAsync(string originalEventId, string msgType, string newBody,
        (string Format, string FormattedBody)? formatted, IList<MatrixId>? mentions)
    {
        RoomMessageMention? roomMessageMention = null;
        if (mentions != null)
        {
            roomMessageMention = new RoomMessageMention { UserIds = mentions.Select(m => m.Full).ToList() };
        }

        var newContent = new MessageNewContent { MsgType = msgType, Body = newBody, Mentions = roomMessageMention };
        if (formatted.HasValue)
        {
            newContent.Format = formatted.Value.Format;
            newContent.FormattedBody = formatted.Value.FormattedBody;
        }

        var messageRequest = new MessageRequest
        {
            MsgType = msgType,
            Body = $"* {newBody}",
            Mentions = roomMessageMention,
            NewContent = newContent,
            RelatesTo = new RoomMessageRelatesTo
            {
                RelType = "m.replace",
                EventId = originalEventId
            }
        };

        if (formatted.HasValue)
        {
            messageRequest.Format = formatted.Value.Format;
            messageRequest.FormattedBody = $"* {formatted.Value.FormattedBody}";
        }

        return await SendRoomEventAsync("m.room.message", messageRequest).ConfigureAwait(false);
    }

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
        var reaction = new ReactionRequest
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
    /// Fetches message history from the room.
    /// </summary>
    /// <param name="limit">Maximum number of messages to return (default 10)</param>
    /// <param name="from">Pagination token to start from (null for most recent)</param>
    /// <param name="direction">Direction to paginate: "b" for backwards (default), "f" for forwards</param>
    /// <returns>A result containing messages and a pagination token for further fetching</returns>
    public async Task<MessageHistoryResult> FetchMessagesAsync(int limit = 10, string? from = null, string direction = "b")
    {
        var parameters = new MessagesParameters { Dir = direction, Limit = limit, From = from };
        var response = await MatrixHelper.GetMessagesAsync(Client.HttpClientParameters, RoomId, parameters).ConfigureAwait(false);

        var messages = new List<ReceivedTextMessage>();
        if (response.Chunk != null)
        {
            foreach (var chunkEvent in response.Chunk)
            {
                ClientEventWithoutRoomID? e = chunkEvent;
                var isEncrypted = e.Type == "m.room.encrypted";
                if (isEncrypted)
                    e = Client.DecryptRoomEvent(this, e);
                if (e?.Type != "m.room.message" || e.Content == null)
                    continue;

                var messageEvent = JsonSerializer.Deserialize<RoomMessageEvent>((JsonElement)e.Content);
                if (messageEvent == null || (messageEvent.MsgType != "m.text" && messageEvent.MsgType != "m.notice"))
                    continue;

                if (!UserId.TryParse(e.Sender, out MatrixId? userId) || userId == null)
                    continue;

                var serverTs = DateTimeOffset.FromUnixTimeMilliseconds(e.OriginServerTs);
                var user = Client.GetOrAddUser(userId);
                var roomUser = Client.GetOrAddUser(user, this);

                var threadId = (string?)null;
                if (messageEvent.RelatesTo?.EventId != null && messageEvent.RelatesTo.RelType == "m.thread")
                    threadId = messageEvent.RelatesTo.EventId;

                var message = new ReceivedTextMessage(messageEvent.Body, this, roomUser, e.EventId, serverTs, threadId, messageEvent.MsgType, Client, isEncrypted);
                messages.Add(message);
            }
        }

        return new MessageHistoryResult { Messages = messages, NextToken = response.End };
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
                    "connect with an encryption store directory to enable end-to-end encryption.");
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
    public List<ReceivedTextMessage> Messages { get; internal init; } = [];
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
