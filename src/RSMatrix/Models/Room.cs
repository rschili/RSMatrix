using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Serialises the sync with history walks, so changes to the window and its messages apply one at a time.</summary>
    internal Lock TimelineLock { get; } = new();

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
    /// Returns messages of the room, oldest first. Without <paramref name="before"/>, the newest ones; to continue,
    /// pass the first message of the previous result. Fewer than <paramref name="limit"/> messages means the start
    /// of the visible history. Thread replies are included, each with <see cref="Message.ThreadRootId"/>.
    /// Messages that continue the room's window receive later edits, reactions and deletions. The others are
    /// separate objects that don't.
    /// </summary>
    public Task<IReadOnlyList<Message>> GetMessagesAsync(int limit, Message? before = null, CancellationToken cancellationToken = default)
    {
        if (before != null && !before.Room.RoomId.Equals(RoomId))
            throw new ArgumentException("The message is from another room.", nameof(before));
        return GetMessagesCoreAsync(limit, before?.Id, before?.HistoryToken, cancellationToken);
    }

    /// <summary>
    /// Returns the messages before the message with this ID, oldest first. Works for any message of the room,
    /// also ones the client hasn't loaded. See <see cref="GetMessagesAsync(int, Message?, CancellationToken)"/>.
    /// </summary>
    public Task<IReadOnlyList<Message>> GetMessagesAsync(int limit, string beforeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(beforeId);
        return GetMessagesCoreAsync(limit, beforeId, Window.Find(beforeId)?.HistoryToken, cancellationToken);
    }

    private async Task<IReadOnlyList<Message>> GetMessagesCoreAsync(int limit, string? beforeId, string? beforeToken, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        List<Message> messages; // newest first, like the pages
        bool continuesWindow;
        Message? windowStart;
        string? token;
        lock (TimelineLock)
        {
            continuesWindow = beforeId == null || Window.Find(beforeId) != null;
            messages = continuesWindow ? Window.TakeOlder(beforeId, limit) : [];
            if (messages.Count == limit || (continuesWindow && Window.ReachedStart))
                return Chronological(messages);
            windowStart = continuesWindow ? Window.Oldest : null;
            token = continuesWindow ? windowStart?.HistoryToken : beforeToken;
        }

        // Without a token, the server tells us the position before the message. Without a message, /messages starts at the newest one.
        var startId = continuesWindow ? windowStart?.Id : beforeId;
        if (token == null && startId != null)
        {
            var context = await MatrixHelper.GetEventContextAsync(Client.HttpClientParameters, RoomId, startId, limit: 0, cancellationToken).ConfigureAwait(false);
            if (context.Start is not { } start)
                return Chronological(messages);
            token = start;
        }

        // Pages also hold reactions, edits and state, so a page can have fewer messages than asked for.
        while (messages.Count < limit)
        {
            var response = await MatrixHelper.GetMessagesAsync(Client.HttpClientParameters, RoomId,
                new MessagesParameters { Dir = "b", From = token, Limit = limit - messages.Count }, cancellationToken).ConfigureAwait(false);
            var reachedStart = response.End == null || response.Chunk.Count == 0;
            lock (TimelineLock)
            {
                var page = Client.ReadHistoryPage(this, response.Chunk);
                if (page.Count > 0 && !reachedStart)
                    page[^1].HistoryToken = response.End;
                // The page joins the window if it continues it, and the sync didn't change the window's older end meanwhile.
                if (continuesWindow && Window.Oldest == windowStart)
                {
                    Window.Prepend(page, reachedStart);
                    windowStart = Window.Oldest;
                }
                else
                {
                    continuesWindow = false;
                }
                messages.AddRange(page);
            }
            if (reachedStart)
                break;
            token = response.End;
        }
        return Chronological(messages);
    }

    /// <summary>
    /// Returns the message with this ID: the one the room's window holds, or else a separate object loaded from the
    /// server that doesn't receive later changes and may miss reactions. Null if the server doesn't know the event,
    /// we may not see it, or it isn't a message.
    /// </summary>
    public async Task<Message?> GetMessageAsync(string eventId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        if (Window.Find(eventId) is { } known)
            return known;

        RoomEvent e;
        try
        {
            e = await MatrixHelper.GetRoomEventAsync(Client.HttpClientParameters, RoomId, eventId, cancellationToken).ConfigureAwait(false);
        }
        catch (MatrixResponseException ex) when (ex.ErrorCode == "M_NOT_FOUND")
        {
            return null;
        }
        lock (TimelineLock)
            return Client.ReadHistoryPage(this, [e]).SingleOrDefault();
    }

    /// <summary>
    /// Returns replies of a thread, oldest first, without the thread's first message. Paging works like
    /// <see cref="GetMessagesAsync(int, Message?, CancellationToken)"/>: pass the first reply of the previous result
    /// to continue. Replies the room's window holds are returned as they are.
    /// </summary>
    public async Task<IReadOnlyList<Message>> GetThreadMessagesAsync(string threadRootId, int limit, Message? before = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadRootId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        // Replies are newer than their thread's first message, so the window holds all of them if it holds that one.
        lock (TimelineLock)
        {
            if (Window.Find(threadRootId) != null)
                return Chronological(Window.TakeOlderInThread(threadRootId, before?.Id, limit));
        }

        List<Message> messages = []; // newest first, like the pages
        var token = before?.ThreadHistoryToken;
        // Without a token, walk from the newest reply and skip everything up to the one to continue from.
        var skipUntilId = token == null ? before?.Id : null;
        while (messages.Count < limit)
        {
            var response = await MatrixHelper.GetRelationsAsync(Client.HttpClientParameters, RoomId, threadRootId, "m.thread", token,
                limit - messages.Count, cancellationToken).ConfigureAwait(false);
            lock (TimelineLock)
            {
                var page = Client.ReadHistoryPage(this, response.Chunk);
                if (skipUntilId != null)
                {
                    var skipped = page.FindIndex(m => m.Id == skipUntilId) + 1;
                    page.RemoveRange(0, skipped > 0 ? skipped : page.Count);
                    if (skipped > 0)
                        skipUntilId = null;
                }
                if (page.Count > 0 && response.NextBatch != null)
                    page[^1].ThreadHistoryToken = response.NextBatch;
                messages.AddRange(page);
            }
            if (response.NextBatch == null || response.Chunk.Count == 0)
                break;
            token = response.NextBatch;
        }
        return Chronological(messages);
    }

    private static readonly JsonSerializerOptions SourceOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal async Task<string> GetEventSourceAsync(string eventId, CancellationToken cancellationToken)
    {
        var json = await MatrixHelper.GetRoomEventAsync<JsonElement>(Client.HttpClientParameters, RoomId, eventId, cancellationToken).ConfigureAwait(false);
        var source = JsonNode.Parse(json.GetRawText())!.AsObject();
        if (Client.Encryption is { } encryption && source["type"]?.GetValue<string>() == "m.room.encrypted" &&
            json.Deserialize<RoomEvent>() is { } encrypted && encryption.DecryptRoomEvent(RoomId.Full, encrypted, out _) is { } decrypted)
        {
            source["type"] = decrypted.Type;
            source["content"] = decrypted.Content is { } content ? JsonNode.Parse(content.GetRawText()) : null;
        }
        return source.ToJsonString(SourceOptions);
    }

    private static List<Message> Chronological(List<Message> newestFirst)
    {
        newestFirst.Reverse();
        return newestFirst;
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
