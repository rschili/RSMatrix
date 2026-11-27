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
        Cache = new TimelineCache(this, holdsNewest: true);
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

    /// <summary>
    /// The room members the client knows. The sync only delivers members who show up in the timeline, so this is
    /// incomplete until <see cref="GetMembersAsync"/> loaded the full list.
    /// </summary>
    public ImmutableDictionary<string, RoomUser> Users { get; internal set; } = ImmutableDictionary<string, RoomUser>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The name set for the room, or null. <see cref="DisplayName"/> also covers rooms without one.</summary>
    public string? Name { get; internal set; }
    public string? Topic { get; internal set; }
    /// <summary>The room's avatar as an mxc:// URI.</summary>
    public string? AvatarUrl { get; internal set; }
    public MatrixId? CanonicalAlias { get; internal set; }
    public List<MatrixId>? AltAliases { get; internal set; }

    public int JoinedMemberCount { get; internal set; }
    public int InvitedMemberCount { get; internal set; }

    /// <summary>User IDs the server picked to name a room without a name, never the current user.</summary>
    internal ImmutableArray<string> Heroes { get; set; } = [];

    /// <summary>Whether the current user is in the room. False after leaving, being kicked or banned.</summary>
    public bool IsJoined { get; internal set; }

    /// <summary>
    /// The room's newest message the client holds, for room lists. Thread replies count, deleted messages too
    /// (<see cref="Message.IsDeleted"/>). Null if the sync brought no message for the room yet. Doesn't ask the server.
    /// </summary>
    public Message? LatestMessage
    {
        get
        {
            lock (TimelineLock)
                return Cache.TakeOlder(null, 1).FirstOrDefault();
        }
    }

    private bool _allMembersLoaded;

    internal TimelineCache Cache { get; }

    /// <summary>Serialises the sync with history walks and guards <see cref="Cache"/>.</summary>
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
        string? threadFallbackId = null;
        if (threadRootId != null)
        {
            lock (TimelineLock)
                threadFallbackId = Cache.FindLatestInThread(threadRootId)?.EventId ?? threadRootId;
        }
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
    {
        Message? original;
        lock (TimelineLock)
            original = Cache.FindMessage(originalEventId) is { } entry ? Cache.Build(entry) : null;
        return EditMessageInternalAsync(originalEventId, original?.Kind ?? MessageKind.Text, newBody, newHtml, mentions, original?.Mentions);
    }

    /// <param name="previousMentions">
    /// The mentions of the current version, or null when unknown. Only users mentioned for the first time are notified again.
    /// </param>
    internal async Task<string> EditMessageInternalAsync(string originalEventId, MessageKind kind, string newBody, string? newHtml,
        IList<MatrixId>? mentions, IReadOnlyList<RoomUser>? previousMentions)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalEventId);
        ArgumentException.ThrowIfNullOrEmpty(newBody);
        var msgType = kind switch { MessageKind.Notice => "m.notice", MessageKind.Emote => "m.emote", _ => "m.text" };
        var newContent = new NewMessageContent { MsgType = msgType, Body = newBody, Mentions = ToMentions(mentions) };
        var previous = previousMentions?.Select(user => user.User.UserId.Full).ToHashSet(StringComparer.Ordinal);
        var content = new RoomMessageContent
        {
            MsgType = msgType,
            Body = $"* {newBody}",
            Mentions = ToMentions(previous == null ? mentions : mentions?.Where(id => !previous.Contains(id.Full)).ToList()),
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

    /// <summary>
    /// The spec recommends m.mentions on every message, empty without mentions, so servers don't fall back to finding
    /// names in the body. Our own ID is left out, since a message can't notify its sender.
    /// </summary>
    private Mentions ToMentions(IList<MatrixId>? mentions)
    {
        var userIds = mentions?.Select(id => id.Full).Where(id => id != Client.CurrentUser.Full).Distinct(StringComparer.Ordinal).ToList();
        return new Mentions { UserIds = userIds is { Count: > 0 } ? userIds : null };
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
    /// Recent messages come from memory; the client asks the server for the rest and keeps them for a while.
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
        return GetMessagesCoreAsync(limit, beforeId, null, cancellationToken);
    }

    private async Task<IReadOnlyList<Message>> GetMessagesCoreAsync(int limit, string? beforeId, string? beforeToken, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        List<Message> messages; // newest first, like the pages
        TimelineCache cache;
        int generation;
        string? cacheOldestId;
        string? token;
        string? startId;
        lock (TimelineLock)
        {
            Cache.MarkRead();
            if (beforeId == null || Cache.Contains(beforeId))
            {
                messages = Cache.TakeOlder(beforeId, limit);
                if (messages.Count == limit || Cache.ReachedStart)
                    return Chronological(messages);
                cache = Cache;
                token = Cache.OlderToken;
                startId = Cache.OldestEventId;
            }
            else
            {
                // Without the events after the starting point, messages may miss reactions.
                messages = [];
                cache = new TimelineCache(this, holdsNewest: false);
                token = beforeToken;
                startId = beforeId;
            }
            generation = cache.Generation;
            cacheOldestId = cache.OldestEventId;
        }

        // Without a token, the server tells us the position before the event. Without an event, /messages starts at the newest one.
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
                // The sync may have cut the room's cache meanwhile, or another walk extended it.
                if (cache.Generation != generation || cache.OldestEventId != cacheOldestId)
                    cache = new TimelineCache(this, holdsNewest: false);
                var entries = Client.ReadHistoryEntries(this, response.Chunk);
                cache.Prepend(entries, response.End, reachedStart);
                generation = cache.Generation;
                cacheOldestId = cache.OldestEventId;
                var page = entries.OfType<MessageEntry>().Select(cache.Build).ToList();
                if (page.Count > 0 && !reachedStart)
                    page[^1].HistoryToken = response.End;
                messages.AddRange(page);
            }
            if (reachedStart)
                break;
            token = response.End;
        }
        return Chronological(messages);
    }

    /// <summary>
    /// Returns the message with this ID, from memory or from the server. A message from the server may miss
    /// reactions, see <see cref="Message.HasAllReactions"/>. Null if the server doesn't know the event,
    /// we may not see it, or it isn't a message.
    /// </summary>
    public async Task<Message?> GetMessageAsync(string eventId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        lock (TimelineLock)
        {
            Cache.MarkRead();
            if (Cache.FindMessage(eventId) is { } known)
                return Cache.Build(known);
        }

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
        {
            var cache = new TimelineCache(this, holdsNewest: false);
            var entries = Client.ReadHistoryEntries(this, [e]);
            cache.Prepend(entries, null, false);
            return entries.OfType<MessageEntry>().Select(cache.Build).SingleOrDefault();
        }
    }

    /// <summary>
    /// Returns replies of a thread, oldest first, without the thread's first message. Paging works like
    /// <see cref="GetMessagesAsync(int, Message?, CancellationToken)"/>: pass the first reply of the previous result
    /// to continue.
    /// </summary>
    public async Task<IReadOnlyList<Message>> GetThreadMessagesAsync(string threadRootId, int limit, Message? before = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadRootId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        // Replies are newer than their thread's first message, so the cache holds all of them if it holds that one.
        lock (TimelineLock)
        {
            Cache.MarkRead();
            if (Cache.FindMessage(threadRootId) != null)
                return Chronological(Cache.TakeOlderInThread(threadRootId, before?.Id, limit));
        }

        List<Message> messages = []; // newest first, like the pages
        var cache = new TimelineCache(this, holdsNewest: false);
        var token = before?.ThreadHistoryToken;
        // Without a token, walk from the newest reply and skip everything up to the one to continue from.
        var skipUntilId = token == null ? before?.Id : null;
        while (messages.Count < limit)
        {
            var response = await MatrixHelper.GetRelationsAsync(Client.HttpClientParameters, RoomId, threadRootId, "m.thread", token,
                limit - messages.Count, cancellationToken).ConfigureAwait(false);
            lock (TimelineLock)
            {
                var entries = Client.ReadHistoryEntries(this, response.Chunk);
                cache.Prepend(entries, null, false);
                var page = entries.OfType<MessageEntry>().Select(cache.Build).ToList();
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

    internal async Task<IReadOnlyList<Reaction>> GetReactionsAsync(string messageId, CancellationToken cancellationToken)
    {
        lock (TimelineLock)
        {
            if (Cache.FindMessage(messageId) is { } cached)
                return Cache.Build(cached).Reactions;
        }

        List<ReactionEvent> reactions = []; // newest first, like the pages
        await Client.ReactionRequests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? token = null;
            do
            {
                var response = await MatrixHelper.GetRelationsAsync(Client.HttpClientParameters, RoomId, messageId, "m.annotation", token,
                    limit: 100, cancellationToken).ConfigureAwait(false);
                lock (TimelineLock)
                {
                    reactions.AddRange(Client.ReadHistoryEntries(this, response.Chunk).OfType<ReactionEntry>()
                        .Where(r => r.TargetId == messageId).Select(r => new ReactionEvent(r.EventId, r.Key, r.User)));
                }
                token = response.Chunk.Count == 0 ? null : response.NextBatch;
            }
            while (token != null);
        }
        finally
        {
            Client.ReactionRequests.Release();
        }
        reactions.Reverse();
        return Message.ToReactions(reactions);
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
                if (target.Id == _readMarkerSent?.Id)
                    return;
            }
            // The spec says not to send read receipts for our own messages; the fully read marker still moves.
            await MatrixHelper.PostReadMarkersAsync(Client.HttpClientParameters, RoomId, target.Id, sendReceipt: !target.IsOwn).ConfigureAwait(false);
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

    /// <summary>
    /// The joined and invited members. The first call loads the full list from the server, later calls return
    /// <see cref="Users"/>, which the sync keeps up to date.
    /// </summary>
    public async Task<IReadOnlyList<RoomUser>> GetMembersAsync(CancellationToken cancellationToken = default)
    {
        if (!_allMembersLoaded)
        {
            var response = await MatrixHelper.GetMembersAsync(Client.HttpClientParameters, RoomId, cancellationToken).ConfigureAwait(false);
            Client.AddLoadedMembers(this, response.Chunk ?? []);
            _allMembersLoaded = true;
        }
        return Users.Values.Where(u => u.Membership is Membership.Join or Membership.Invite).ToList();
    }

    /// <summary>
    /// The name to show for the room, following the spec: its name, its alias, or the names of a few members.
    /// Composed names are in English, like "Alice, Bob and 3 others".
    /// </summary>
    public string DisplayName => ComposeDisplayName();

    private string ComposeDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(Name))
            return Name;
        if (CanonicalAlias != null)
            return CanonicalAlias.Full;

        var users = Users;
        var names = Heroes.Select(id => users.TryGetValue(id, out var user) ? user.DisplayName : id).ToList();
        var memberCount = JoinedMemberCount + InvitedMemberCount;
        if (memberCount <= 1)
            return names.Count == 0 ? "Empty room" : $"Empty room (was {JoinNames(names, 0)})";
        if (names.Count == 0)
            return RoomId.Full;
        return JoinNames(names, memberCount - 1 - names.Count);
    }

    private static string JoinNames(List<string> names, int others)
    {
        if (others > 0)
            return $"{string.Join(", ", names)} and {others} {(others == 1 ? "other" : "others")}";
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
    }

    public override string ToString() => DisplayName;
}

public class RoomUser
{
    internal RoomUser(User user)
    {
        User = user;
    }

    public User User { get; }
    /// <summary>The name the user set for this room, or null.</summary>
    public string? RoomName { get; internal set; }

    /// <summary>The name to show: the name in this room, or else the user's <see cref="User.DisplayName"/>.</summary>
    public string DisplayName => RoomName ?? User.DisplayName;

    /// <summary>The user's avatar in this room as an mxc:// URI.</summary>
    public string? AvatarUrl { get; internal set; }
    public Membership? Membership { get; internal set; }

    public override string ToString() => DisplayName;
}

public class RoomEncryption
{
    public string Algorithm { get; }
    internal RoomEncryption(string algorithm)
    {
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }
}
