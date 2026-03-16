using System.Collections.Immutable;
using RSMatrix.Http;

namespace RSMatrix.Models;

public class Room
{
    internal MatrixTextClient Client { get; }
    public Room(MatrixId roomId, MatrixTextClient client)
    {
        RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public MatrixId RoomId { get; }

    // ConcurrentDictionary is expensive, we only use it for the global things. Inside the room we use ImmutableDictionary instead as there is less data and less movement
    public ImmutableDictionary<string, RoomUser> Users { get; internal set; } = ImmutableDictionary<string, RoomUser>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public string? DisplayName { get; internal set; }
    public MatrixId? CanonicalAlias { get; internal set; }
    public List<MatrixId>? AltAliases { get; internal set; }

    public ReceivedTextMessage? LastMessage { get; internal set; }

    public string? LastReceiptEventId { get; internal set; }
    public RoomEncryption? Encryption { get; internal set; }
    public bool IsEncrypted => Encryption != null;

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

    private async Task<string> SendMessageInternalAsync(string msgType, string body, (string Format, string FormattedBody)? formatted, string? inReplyTo, IList<MatrixId>? mentions)
    {
        RoomMessageMention? roomMessageMention = null;
        if (mentions != null)
        {
            roomMessageMention = new RoomMessageMention { UserIds = mentions.Select(m => m.Full).ToList() };
        }
        RoomMessageRelatesTo? relatesTo = null;
        if (inReplyTo != null)
        {
            relatesTo = new RoomMessageRelatesTo { InReplyTo = new RoomMessageInReplyTo() { EventId = inReplyTo } };
        }

        var messageRequest = new MessageRequest { MsgType = msgType, Body = body, Mentions = roomMessageMention, RelatesTo = relatesTo };
        if (formatted.HasValue)
        {
            messageRequest.Format = formatted.Value.Format;
            messageRequest.FormattedBody = formatted.Value.FormattedBody;
        }
        var response = await MatrixHelper.PutMessageAsync(Client.HttpClientParameters, RoomId, messageRequest).ConfigureAwait(false);
        return response.EventId ?? "";
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

        var response = await MatrixHelper.PutMessageAsync(Client.HttpClientParameters, RoomId, messageRequest).ConfigureAwait(false);
        return response.EventId ?? "";
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
        var response = await MatrixHelper.PutReactionAsync(Client.HttpClientParameters, RoomId, eventId, key).ConfigureAwait(false);
        return response.EventId ?? "";
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
            foreach (var e in response.Chunk)
            {
                if (e.Type != "m.room.message" || e.Content == null)
                    continue;

                var messageEvent = System.Text.Json.JsonSerializer.Deserialize<RoomMessageEvent>((System.Text.Json.JsonElement)e.Content);
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

                var message = new ReceivedTextMessage(messageEvent.Body, this, roomUser, e.EventId, serverTs, threadId, messageEvent.MsgType, Client);
                messages.Add(message);
            }
        }

        return new MessageHistoryResult { Messages = messages, NextToken = response.End };
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
    public required List<ReceivedTextMessage> Messages { get; init; }
    /// <summary>
    /// Token to pass as 'from' in the next FetchMessagesAsync call to continue paginating.
    /// Null when there are no more messages to fetch.
    /// </summary>
    public string? NextToken { get; init; }
}

public class RoomUser
{
    public RoomUser(User user)
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
