using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.Storage;
using RSMatrix.Models;
using ConnectionChanged = RSMatrix.Desktop.Chat.ConnectionChanged;

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

    private MatrixChatSession(MatrixClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
        CurrentUser = new ChatUser(client.CurrentUser.Full, client.CurrentUser.Localpart.ToString());
        ServerName = client.CurrentUser.Domain.ToString();
        _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Online, null));
        foreach (var room in client.Rooms)
            AddRoom(room);
        _pumps = ForwardUpdatesAsync();
    }

    public ChatUser CurrentUser { get; }

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
            .Select(u => new ChatMember(new ChatUser(u.User.UserId.Full, u.DisplayName), u.Membership == Membership.Invite))
            .ToList();
    }

    // RSMatrix can't download media yet.
    public Task<byte[]?> DownloadMediaAsync(string mxcUri, CancellationToken cancellationToken)
        => Task.FromResult<byte[]?>(null);

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

    private static ChatRoomInfo ToRoomInfo(Room room)
        => new(room.RoomId.Full, room.DisplayName, room.IsEncrypted, room.IsDirect);

    private static ChatMessage ToChatMessage(Message message)
    {
        var kind = message.Kind == MessageKind.Notice ? ChatMessageKind.Notice : ChatMessageKind.Text;
        var sender = ToChatUser(message.Sender);
        return new ChatMessage(message.Room.RoomId.Full, message.Id, sender, message.Timestamp, kind,
            message.Body, PlainTextParser.Parse(message.Body), message.IsEncrypted, message.ThreadRootId, message.ReplyToId,
            Reactions: ToChatReactions(message.Reactions));
    }

    private static List<ChatReaction> ToChatReactions(IReadOnlyList<Reaction> reactions)
        => reactions.Select(r => new ChatReaction(r.Key, r.Users.Select(ToChatUser).ToList())).ToList();

    private static ChatUser ToChatUser(RoomUser user)
        => new(user.User.UserId.Full, user.DisplayName);
}
