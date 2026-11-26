using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.Storage;
using RSMatrix.Models;
using LibraryReactionRemoved = RSMatrix.Models.ReactionRemoved;

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
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);
    // A sync long poll returns at least once a minute, so a longer silence means the connection is gone.
    private static readonly TimeSpan SyncSilenceLimit = TimeSpan.FromSeconds(90);

    private readonly MatrixClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger _logger;
    private readonly Channel<ChatEvent> _events = Channel.CreateUnbounded<ChatEvent>(new() { SingleReader = true });
    private readonly Task _pumps;

    // RSMatrix has no public room list yet, so rooms are learned from the messages they deliver.
    // Joined rooms without a text message in the initial sync don't show up until someone writes.
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    // RSMatrix doesn't deliver reactions, so only the ones sent here are known, and only they can be taken back.
    private readonly ConcurrentDictionary<(string RoomId, string TargetEventId, string Key), string> _sentReactionIds = new();
    private long _syncInterruptedAtUnixMilliseconds;

    private MatrixChatSession(MatrixClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
        CurrentUser = new ChatUser(client.CurrentUser.Full, client.CurrentUser.Localpart.ToString());
        ServerName = client.CurrentUser.Domain.ToString();
        _pumps = Task.WhenAll(ForwardUpdatesAsync(), PollConnectionStateAsync());
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
        _events.Writer.TryWrite(new RSMatrix.Desktop.Chat.ReactionRemoved(roomId, targetEventId, key, CurrentUser));
    }

    // The sync lazy-loads members and RSMatrix has no public call for the full list,
    // so this only knows the members that showed up in the room's state and timeline.
    public Task<IReadOnlyList<ChatMember>> GetMembersAsync(string roomId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ChatMember> members = GetRoom(roomId).Users.Values
            .Where(u => u.Membership is null or Membership.Join or Membership.Invite)
            .Select(u => new ChatMember(new ChatUser(u.User.UserId.Full, u.GetDisplayName()), u.Membership == Membership.Invite))
            .ToList();
        return Task.FromResult(members);
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
                        _rooms.TryAdd(message.Room.RoomId.Full, message.Room);
                        _events.Writer.TryWrite(new MessageReceived(ToRoomInfo(message.Room), ToChatMessage(message)));
                        break;
                    case ClientIssue issue:
                        _logger.LogWarning("{Issue}", issue);
                        if (issue.Kind == ClientIssueKind.SyncInterrupted)
                        {
                            Interlocked.Exchange(ref _syncInterruptedAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Reconnecting, issue.Message));
                            continue;
                        }
                        _events.Writer.TryWrite(new IssueReported(issue.Message, issue.Room?.RoomId.Full));
                        break;
                    case ReactionAdded reaction:
                        _events.Writer.TryWrite(new ReactionReceived(reaction.Message.Room.RoomId.Full, reaction.Message.Id,
                            reaction.Key, ToChatUser(reaction.User)));
                        break;
                    case LibraryReactionRemoved reaction:
                        _events.Writer.TryWrite(new RSMatrix.Desktop.Chat.ReactionRemoved(reaction.Message.Room.RoomId.Full,
                            reaction.Message.Id, reaction.Key, ToChatUser(reaction.User)));
                        break;
                    case MessageEdited:
                        // The app does not show message edits yet.
                        break;
                    case MessageDeleted:
                        // The app does not show message deletions yet.
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

    // RSMatrix offers connection health as properties without change notifications, so they are polled.
    private async Task PollConnectionStateAsync()
    {
        var reported = (ConnectionState?)null;
        using var timer = new PeriodicTimer(StatusPollInterval);
        try
        {
            do
            {
                var state = GetConnectionState();
                if (state == ConnectionState.Stopped)
                    return; // ForwardMessagesAsync reports why.
                if (state != reported)
                    _events.Writer.TryWrite(new ConnectionChanged(state, null));
                reported = state;
            }
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private ConnectionState GetConnectionState()
    {
        if (!_client.IsSyncing)
            return ConnectionState.Stopped;
        if (_client.LastSyncAt is not { } lastSync)
            return ConnectionState.Connecting;
        var interruptedAt = DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _syncInterruptedAtUnixMilliseconds));
        if (interruptedAt > lastSync || DateTimeOffset.UtcNow - lastSync > SyncSilenceLimit)
            return ConnectionState.Reconnecting;
        return ConnectionState.Online;
    }

    private ChatRoomInfo ToRoomInfo(Room room)
        => new(room.RoomId.Full, GetRoomName(room), room.IsEncrypted, room.IsDirect);

    /// <summary>
    /// Room name following the spec's order: name, canonical alias, then members.
    /// RSMatrix doesn't expose the member counts from the room summary, so the member variant is approximate.
    /// </summary>
    private string GetRoomName(Room room)
    {
        if (!string.IsNullOrWhiteSpace(room.DisplayName))
            return room.DisplayName;
        if (room.CanonicalAlias != null)
            return room.CanonicalAlias.Full;

        var others = room.Users.Values
            .Where(u => u.User.UserId.Full != CurrentUser.UserId && u.Membership is null or Membership.Join or Membership.Invite)
            .Select(u => u.GetDisplayName())
            .Order(StringComparer.CurrentCulture)
            .ToList();
        return others.Count switch
        {
            0 => "Empty room",
            <= 3 => string.Join(", ", others),
            _ => $"{string.Join(", ", others.Take(3))} and {others.Count - 3} others",
        };
    }

    private static ChatMessage ToChatMessage(Message message)
    {
        var kind = message.Kind == MessageKind.Notice ? ChatMessageKind.Notice : ChatMessageKind.Text;
        var sender = ToChatUser(message.Sender);
        return new ChatMessage(message.Room.RoomId.Full, message.Id, sender, message.Timestamp, kind,
            message.Body, PlainTextParser.Parse(message.Body), message.IsEncrypted, message.ThreadRootId, message.ReplyToId,
            Reactions: message.Reactions.Select(r => new ChatReaction(r.Key, r.Users.Select(ToChatUser).ToList())).ToList());
    }

    private static ChatUser ToChatUser(RoomUser user)
        => new(user.User.UserId.Full, user.GetDisplayName());
}
