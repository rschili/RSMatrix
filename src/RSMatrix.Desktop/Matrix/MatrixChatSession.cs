using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.Storage;
using RSMatrix.Models;

namespace RSMatrix.Desktop.Matrix;

/// <param name="RecoveryKeyFile">Where the recovery key is read from and saved to. Defaults to the account's data directory.</param>
public sealed record MatrixLogin(string UserId, string Password, Uri? Homeserver, string? RecoveryKeyFile = null);

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
    private readonly CancellationTokenSource _lifetime;
    private readonly DesktopHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly Channel<ChatEvent> _events = Channel.CreateUnbounded<ChatEvent>(new() { SingleReader = true });
    private readonly Task _pumps;

    // RSMatrix has no public room list yet, so rooms are learned from the messages they deliver.
    // Joined rooms without a text message in the initial sync don't show up until someone writes.
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    // RSMatrix doesn't deliver reactions, so only the ones sent here are known, and only they can be taken back.
    private readonly ConcurrentDictionary<(string RoomId, string TargetEventId, string Key), string> _sentReactionIds = new();
    private long _syncInterruptedAtUnixMilliseconds;

    private MatrixChatSession(MatrixClient client, CancellationTokenSource lifetime,
        DesktopHttpClientFactory httpClientFactory, ILogger logger)
    {
        _client = client;
        _lifetime = lifetime;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        CurrentUser = new ChatUser(client.CurrentUser.Full, client.CurrentUser.Localpart.ToString());
        ServerName = client.CurrentUser.Domain.ToString();
        _pumps = Task.WhenAll(ForwardMessagesAsync(), ForwardIssuesAsync(), PollConnectionStateAsync());
    }

    public ChatUser CurrentUser { get; }

    public string ServerName { get; }

    public static async Task<MatrixChatSession> ConnectAsync(MatrixLogin login, AccountData account,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        // RSMatrix uses one token for connecting and for the lifetime of the client.
        // The caller's token only cancels the connect attempt.
        var lifetime = new CancellationTokenSource();
        var httpClientFactory = new DesktopHttpClientFactory(login.Homeserver);
        try
        {
            MatrixClient client;
            await using (cancellationToken.Register(lifetime.Cancel))
            {
                client = await MatrixClient.ConnectAsync(login.UserId, login.Password, account.DeviceId,
                    httpClientFactory, lifetime.Token, loggerFactory.CreateLogger<MatrixClient>(),
                    sessionStoreDirectory: account.SessionDirectory,
                    encryption: CreateEncryptionOptions(account, login.RecoveryKeyFile ?? account.RecoveryKeyPath)).ConfigureAwait(false);
            }
            return new MatrixChatSession(client, lifetime, httpClientFactory, loggerFactory.CreateLogger<MatrixChatSession>());
        }
        catch
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            lifetime.Dispose();
            httpClientFactory.Dispose();
            throw;
        }
    }

    // The app has no screen for the recovery key yet, so it keeps it in a file.
    private static EncryptionOptions CreateEncryptionOptions(AccountData account, string recoveryKeyFile) => new()
    {
        StoreDirectory = account.EncryptionDirectory,
        GetRecoveryKeyAsync = async cancellationToken => File.Exists(recoveryKeyFile)
            ? (await File.ReadAllTextAsync(recoveryKeyFile, cancellationToken).ConfigureAwait(false)).Trim()
            : null,
        SaveRecoveryKeyAsync = async (recoveryKey, cancellationToken) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(recoveryKeyFile))!);
            await File.WriteAllTextAsync(recoveryKeyFile, recoveryKey, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(recoveryKeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        },
    };

    public IAsyncEnumerable<ChatEvent> ReadEventsAsync(CancellationToken cancellationToken)
        => _events.Reader.ReadAllAsync(cancellationToken);

    // RSMatrix sends to a thread only as a response to a received message object, which this session doesn't keep.
    public Task<string> SendTextAsync(string roomId, string text, string? replyToEventId, string? threadRootId,
        CancellationToken cancellationToken)
        => threadRootId != null
            ? throw new NotSupportedException("RSMatrix can't send to threads yet.")
            : GetRoom(roomId).SendTextMessageAsync(text, replyToEventId, mentions: null).WaitAsync(cancellationToken);

    public async Task<HistoryPage> LoadHistoryAsync(string roomId, string? from, int limit, CancellationToken cancellationToken)
    {
        var result = await GetRoom(roomId).FetchMessagesAsync(limit, from).WaitAsync(cancellationToken).ConfigureAwait(false);
        var messages = result.Messages.Select(ToChatMessage).Reverse().ToList();
        return new HistoryPage(messages, result.NextToken);
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
        // Cancelling stops the sync. RSMatrix has no logout, so the device stays signed in and resumes next time.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pumps.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _lifetime.Dispose();
        _httpClientFactory.Dispose();
    }

    private Room GetRoom(string roomId)
        => _rooms.TryGetValue(roomId, out var room) ? room : throw new InvalidOperationException($"Unknown room {roomId}.");

    private async Task ForwardMessagesAsync()
    {
        string? failure = null;
        try
        {
            await foreach (var message in _client.Messages.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                _rooms.TryAdd(message.Room.RoomId.Full, message.Room);
                _events.Writer.TryWrite(new MessageReceived(ToRoomInfo(message.Room), ToChatMessage(message)));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The message channel faults when the sync ends with an error, like a revoked session.
            _logger.LogError(ex, "Sync stopped.");
            failure = ex.Message;
        }
        _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Stopped, failure));
        _events.Writer.TryComplete();
    }

    private async Task ForwardIssuesAsync()
    {
        try
        {
            await foreach (var issue in _client.Issues.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                _logger.LogWarning("{Issue}", issue);
                if (issue.Kind == ClientIssueKind.SyncInterrupted)
                {
                    Interlocked.Exchange(ref _syncInterruptedAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Reconnecting, issue.Message));
                    continue;
                }
                _events.Writer.TryWrite(new IssueReported(issue.Message, issue.Room?.RoomId.Full));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
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

    // RSMatrix delivers the plain text body only, without formatted_body.
    private static ChatMessage ToChatMessage(ReceivedTextMessage message)
    {
        var kind = message.MsgType == "m.notice" ? ChatMessageKind.Notice : ChatMessageKind.Text;
        var sender = new ChatUser(message.Sender.User.UserId.Full, message.Sender.GetDisplayName());
        return new ChatMessage(message.Room.RoomId.Full, message.EventId, sender, message.Timestamp, kind,
            message.Body ?? "", PlainTextParser.Parse(message.Body), message.IsEncrypted, message.ThreadId);
    }
}
