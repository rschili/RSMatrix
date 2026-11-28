using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.Storage;
using RSMatrix.Models;
using ConnectionChanged = RSMatrix.Desktop.Chat.ConnectionChanged;
using PresenceChanged = RSMatrix.Desktop.Chat.PresenceChanged;

namespace RSMatrix.Desktop.Matrix;

/// <param name="Password">Needed to log in. Resuming a stored session only needs it after the server ended the session.</param>
/// <param name="Homeserver">Only used to log in; a stored session knows its homeserver.</param>
/// <param name="RecoveryKeyFile">Where the recovery key is read from, when a new device needs it. Defaults to the account's data directory.</param>
public sealed record MatrixLogin(string UserId, string? Password, Uri? Homeserver, string? RecoveryKeyFile = null);

/// <summary>
/// Connects the UI to a homeserver through <see cref="MatrixClient"/>.
/// Workarounds for things RSMatrix does not offer yet are kept in this class.
/// </summary>
public sealed class MatrixChatSession : IChatSession
{
    private readonly MatrixClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger _logger;
    private readonly Channel<ChatEvent> _events = Channel.CreateUnbounded<ChatEvent>(new() { SingleReader = true });
    private readonly Task _pumps;

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    // RSMatrix doesn't deliver reactions, so only the ones sent here are known, and only they can be taken back.
    private readonly ConcurrentDictionary<(string RoomId, string TargetEventId, string Key), string> _sentReactionIds = new();
    // The UI refers to media by mxc:// URI. Files of messages download through their attachment, which can decrypt them.
    private readonly ConcurrentDictionary<string, Attachment> _attachmentsByUri = new(StringComparer.Ordinal);

    private MatrixChatSession(MatrixClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
        CurrentUser = new ChatUser(client.CurrentUser.UserId.Full, client.CurrentUser.UserId.Localpart.ToString());
        ServerName = client.CurrentUser.UserId.Domain.ToString();
        _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Online, null));
        foreach (var room in client.Rooms)
            AddRoom(room);
        _pumps = ForwardUpdatesAsync();
    }

    public ChatUser CurrentUser { get; }

    public ChatPresence? CurrentUserPresence => ToChatPresence(_client.CurrentUser);

    public string ServerName { get; }

    /// <summary>
    /// Resumes the account's stored session, or logs in with the password if there is none or the server deleted
    /// the device.
    /// </summary>
    public static async Task<MatrixChatSession> ConnectAsync(MatrixLogin login, LocalData localData,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        // The app has no screen for the recovery key yet, so it reads it from a file.
        var recoveryKeyFile = login.RecoveryKeyFile ?? localData.GetRecoveryKeyPath(login.UserId);
        var recoveryKey = File.Exists(recoveryKeyFile)
            ? (await File.ReadAllTextAsync(recoveryKeyFile, cancellationToken).ConfigureAwait(false)).Trim()
            : null;
        var logger = loggerFactory.CreateLogger<MatrixClient>();
        MatrixClient? client = null;
        if (MatrixClient.GetStoredSession(localData.StoreDirectory, login.UserId) is { } stored)
        {
            try
            {
                client = await MatrixClient.ResumeAsync(stored, cancellationToken, login.Password, logger, recoveryKey).ConfigureAwait(false);
            }
            catch (SessionEndedException) when (login.Password != null)
            {
                // Logging in below creates a new device.
            }
        }
        client ??= await MatrixClient.LoginAsync(login.UserId,
            login.Password ?? throw new InvalidOperationException("Enter the password to sign in."), cancellationToken,
            storeDirectory: localData.StoreDirectory, homeserver: login.Homeserver, logger: logger,
            recoveryKey: recoveryKey).ConfigureAwait(false);
        return new MatrixChatSession(client, loggerFactory.CreateLogger<MatrixChatSession>());
    }

    public IAsyncEnumerable<ChatEvent> ReadEventsAsync(CancellationToken cancellationToken)
        => _events.Reader.ReadAllAsync(cancellationToken);

    public Task<string> SendTextAsync(string roomId, string text, string? replyToEventId, string? threadRootId,
        CancellationToken cancellationToken)
        => GetRoom(roomId).SendMessageAsync(text, replyToId: replyToEventId, threadRootId: threadRootId).WaitAsync(cancellationToken);

    public async Task<HistoryPage> LoadHistoryAsync(string roomId, string? beforeEventId, int limit, CancellationToken cancellationToken)
    {
        var room = GetRoom(roomId);
        var messages = beforeEventId == null
            ? await room.GetMessagesAsync(limit, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await room.GetMessagesAsync(limit, beforeEventId, cancellationToken).ConfigureAwait(false);
        return new HistoryPage(messages.Select(ToChatMessage).ToList(), messages.Count == limit);
    }

    public async Task SendReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken)
    {
        var reactionId = await GetRoom(roomId).SendReactionAsync(targetEventId, key).WaitAsync(cancellationToken).ConfigureAwait(false);
        _sentReactionIds[(roomId, targetEventId, key)] = reactionId;
        _events.Writer.TryWrite(new ReactionReceived(roomId, targetEventId, key, CurrentUser));
    }

    public async Task RemoveReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken)
    {
        if (!_sentReactionIds.TryGetValue((roomId, targetEventId, key), out var reactionId))
            throw new InvalidOperationException("Only reactions sent in this session can be removed.");
        await GetRoom(roomId).RedactEventAsync(reactionId).WaitAsync(cancellationToken).ConfigureAwait(false);
        _sentReactionIds.TryRemove((roomId, targetEventId, key), out _);
        _events.Writer.TryWrite(new ReactionRemoved(roomId, targetEventId, key, CurrentUser));
    }

    public async Task<IReadOnlyList<ChatReaction>?> GetReactionsAsync(string roomId, string eventId, CancellationToken cancellationToken)
    {
        var message = await GetRoom(roomId).GetMessageAsync(eventId, cancellationToken).ConfigureAwait(false);
        return message == null ? null : ToChatReactions(await message.GetReactionsAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ChatMember>> GetMembersAsync(string roomId, CancellationToken cancellationToken)
    {
        var members = await GetRoom(roomId).GetMembersAsync(cancellationToken).ConfigureAwait(false);
        return members
            .Select(u => new ChatMember(ToChatUser(u), u.Membership == Membership.Invite,
                ToChatPresence(u.User)))
            .ToList();
    }

    public Task SetPresenceAsync(PresenceState state, CancellationToken cancellationToken)
        => _client.SetPresenceAsync(state switch
        {
            PresenceState.Away => Presence.Unavailable,
            PresenceState.Offline => Presence.Offline,
            _ => Presence.Online,
        }, cancellationToken);

    public Task SetStatusMessageAsync(string? statusMessage, CancellationToken cancellationToken)
        => _client.SetStatusMessageAsync(statusMessage, cancellationToken);

    public async Task<byte[]?> DownloadMediaAsync(string mxcUri, CancellationToken cancellationToken)
        => _attachmentsByUri.TryGetValue(mxcUri, out var attachment)
            ? await attachment.DownloadAsync(cancellationToken).ConfigureAwait(false)
            : (await _client.DownloadAsync(mxcUri, cancellationToken).ConfigureAwait(false))?.Content;

    public async Task DownloadMediaAsync(string mxcUri, Stream destination, CancellationToken cancellationToken)
    {
        if (_attachmentsByUri.TryGetValue(mxcUri, out var attachment))
        {
            await attachment.DownloadAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }
        var file = await _client.DownloadAsync(mxcUri, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The server doesn't have this file.");
        await destination.WriteAsync(file.Content, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]?> DownloadThumbnailAsync(string mxcUri, int width, int height, bool crop, CancellationToken cancellationToken)
        => _attachmentsByUri.TryGetValue(mxcUri, out var attachment)
            ? attachment.DownloadThumbnailAsync(width, height, cancellationToken)
            : _client.DownloadThumbnailAsync(mxcUri, width, height, crop, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // RSMatrix has no logout yet, so the device stays signed in and resumes on the next start.
        await _client.DisposeAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pumps.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _lifetime.Dispose();
    }

    private Room GetRoom(string roomId)
        => _rooms.TryGetValue(roomId, out var room) ? room : throw new InvalidOperationException($"Unknown room {roomId}.");

    private async Task ForwardUpdatesAsync()
    {
        string? failure = null;
        try
        {
            await foreach (var update in _client.Updates.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                switch (update.Value)
                {
                    case NewMessage { Message: var message }:
                        _events.Writer.TryWrite(new MessageReceived(ToRoomInfo(message.Room), ToChatMessage(message)));
                        break;
                    case ClientIssue issue:
                        _logger.LogWarning("{Issue}", issue);
                        _events.Writer.TryWrite(new IssueReported(issue.Message, issue.Room?.RoomId.Full));
                        break;
                    case Models.ConnectionChanged changed:
                        _events.Writer.TryWrite(new ConnectionChanged(
                            changed.IsOnline ? ConnectionState.Online : ConnectionState.Reconnecting, changed.Error?.Message));
                        break;
                    case MessageChanged { Change: MessageChange.Reactions } changed:
                        _events.Writer.TryWrite(new ReactionsChanged(changed.Reference.Room.RoomId.Full, changed.Reference.Id,
                            changed.Message is { } current ? ToChatReactions(current.Reactions) : null));
                        break;
                    case MessageChanged:
                        // The app does not show edits, deletions or thread changes yet.
                        break;
                    case Models.PresenceChanged { User: var user } when ToChatPresence(user) is { } presence:
                        _events.Writer.TryWrite(new PresenceChanged(user.UserId.Full, presence));
                        break;
                    case RoomChanged { Room: var room } when !room.IsJoined:
                        if (_rooms.TryRemove(room.RoomId.Full, out _))
                            _events.Writer.TryWrite(new RoomLeft(room.RoomId.Full));
                        break;
                    case RoomChanged { Room: var room } when !_rooms.ContainsKey(room.RoomId.Full):
                        AddRoom(room);
                        break;
                    case RoomChanged { Room: var room }:
                        _events.Writer.TryWrite(new RoomUpdated(ToRoomInfo(room)));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The update channel faults when the sync ends with an error, like a revoked session.
            _logger.LogError(ex, "Sync stopped.");
            failure = ex.Message;
        }
        _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Stopped, failure));
        _events.Writer.TryComplete();
    }

    private void AddRoom(Room room)
    {
        _rooms[room.RoomId.Full] = room;
        var latest = room.LatestMessage is { IsDeleted: false } message ? ToChatMessage(message) : null;
        _events.Writer.TryWrite(new RoomJoined(ToRoomInfo(room), latest));
    }

    private ChatRoomInfo ToRoomInfo(Room room)
        => new(room.RoomId.Full, room.DisplayName, room.IsEncrypted, room.IsDirect, GetRoomAvatarUrl(room));

    /// <summary>Direct chats rarely have a picture of their own; like other clients, show the other person's instead.</summary>
    private string? GetRoomAvatarUrl(Room room)
    {
        if (room.AvatarUrl != null || !room.IsDirect)
            return room.AvatarUrl;
        return room.Users.Values
            .Where(u => u.Membership == Membership.Join && !u.User.UserId.Equals(_client.CurrentUser.UserId))
            .Select(u => u.AvatarUrl)
            .FirstOrDefault(url => url != null);
    }

    private ChatMessage ToChatMessage(Message message)
    {
        var sender = ToChatUser(message.Sender);
        var chatMessage = new ChatMessage(message.Room.RoomId.Full, message.Id, sender, message.Timestamp,
            message.Kind == MessageKind.Notice ? ChatMessageKind.Notice : ChatMessageKind.Text,
            message.Body, PlainTextParser.Parse(message.Body), message.IsEncrypted, message.ThreadRootId, message.ReplyToId,
            Reactions: ToChatReactions(message.Reactions));
        if (message.Attachment is not { } attachment)
            return chatMessage;
        _attachmentsByUri[attachment.MxcUri] = attachment;
        var caption = chatMessage.Content.Blocks;
        if (message.Kind == MessageKind.Image)
        {
            var picture = new ImageBlock(new MediaReference(attachment.MxcUri, attachment.FileName, attachment.Width, attachment.Height));
            return chatMessage with { Kind = ChatMessageKind.Image, Content = new MessageDocument([picture, .. caption]) };
        }
        // Files, audio and video. The file name stands in for a missing caption in previews.
        return chatMessage with
        {
            Body = message.Body.Length > 0 ? message.Body : attachment.FileName,
            Content = new MessageDocument([new FileBlock(attachment.MxcUri, attachment.FileName, attachment.Size), .. caption]),
        };
    }

    private static List<ChatReaction> ToChatReactions(IReadOnlyList<Reaction> reactions)
        => reactions.Select(r => new ChatReaction(r.Key, r.Users.Select(ToChatUser).ToList())).ToList();

    private static ChatPresence? ToChatPresence(User user)
        => user.Presence is not { } presence ? null : new ChatPresence(presence switch
        {
            Presence.Unavailable => PresenceState.Away,
            Presence.Offline => PresenceState.Offline,
            _ => PresenceState.Online,
        }, user.StatusMessage, user.LastActiveAt, user.CurrentlyActive == true);

    private static ChatUser ToChatUser(RoomUser user)
        => new(user.User.UserId.Full, user.DisplayName, user.AvatarUrl);
}
