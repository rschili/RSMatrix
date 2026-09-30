using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Models;
using RSMatrix.Http;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using RSFlowControl;

namespace RSMatrix;

/// <summary>
/// The low level class for interacting with the Matrix server.
/// </summary>
public sealed class MatrixTextClient
{
    internal ILogger Logger => HttpClientParameters.Logger;
    public MatrixId CurrentUser { get; }
    internal IList<SpecVersion> SupportedSpecVersions { get; }
    internal static SpecVersion CurrentSpecVersion { get; } = new SpecVersion(1, 17, null, null);
    internal HttpClientParameters HttpClientParameters { get; private set; }
    internal Capabilities ServerCapabilities { get; private set; }

    internal Filter? Filter { get; private set; }

    private ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    private ConcurrentDictionary<string, User> _users = new(StringComparer.Ordinal);
    private Channel<ReceivedTextMessage> MessageChannel { get; set; }

    /// <summary>
    /// The channel to receive messages from the server.
    /// The channel is unbounded, so it will not block the sender.
    /// The channel closes on cancellation and faults on a terminal sync error.
    /// Transient failures are retried with the existing session.
    /// </summary>
    public ChannelReader<ReceivedTextMessage> Messages => MessageChannel.Reader;

    public bool DebugMode = false;

    /// <summary>
    /// When true, the client will automatically join rooms when invited.
    /// Default is false.
    /// </summary>
    public bool AutoJoinOnInvite { get; set; } = false;

    public bool IsSyncing { get; private set;}

    private MatrixTextClient(HttpClientParameters parameters,
        MatrixId userId,
        IList<SpecVersion> supportedSpecVersions,
        Capabilities capabilities)
    {
        HttpClientParameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        CurrentUser = userId ?? throw new ArgumentNullException(nameof(userId));
        if (userId.Kind != IdKind.User)
            throw new ArgumentException("User ID must be of type 'User'.", nameof(userId));
        SupportedSpecVersions = supportedSpecVersions ?? throw new ArgumentNullException(nameof(supportedSpecVersions));
        ServerCapabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        MessageChannel = Channel.CreateUnbounded<ReceivedTextMessage>();
    }

    /// <summary>
    /// Creates a MatrixTextClient for testing purposes without connecting to a server.
    /// </summary>
    internal static MatrixTextClient CreateForTesting(HttpClientParameters parameters, MatrixId userId)
    {
        var versions = new List<SpecVersion> { CurrentSpecVersion };
        var capabilities = new Capabilities();
        return new MatrixTextClient(parameters, userId, versions, capabilities);
    }

    /// <summary>
    /// Exposes HandleSyncResponseAsync for testing.
    /// </summary>
    internal Task HandleSyncResponseForTestingAsync(SyncResponse response)
        => HandleSyncResponseAsync(response);

    /// <summary>
    /// Connects to the Matrix server using the provided credentials.
    /// The Task will finish when the client is connected and ready to receive messages.
    /// Use <see cref="Messages" />  to retrieve messages.
    /// </summary>
    /// <remarks>
    /// Rate-limited requests wait for the server's retry delay. Transient failures during
    /// post-login initialization and sync are retried until cancellation, using the same
    /// access token and sync position. No session information is written to disk.
    /// Terminal sync errors fault the Messages channel. Only reauthenticate when needed,
    /// for example after M_UNKNOWN_TOKEN with soft_logout=true; do not retry bad credentials.
    /// </remarks>
    /// <param name="userId">User id e.g. @user:example.org</param>
    /// <param name="password">password for the user</param>
    /// <param name="deviceId">device id to identify this client to the server</param>
    /// <param name="httpClientFactory">factory to use for creating http clients</param>
    /// <param name="cancellationToken">cancellation token used to disconnect</param>
    /// <param name="logger">logger</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException">Provided arguments not valid</exception>
    /// <exception cref="InvalidOperationException">Mostly if connection fails</exception>
    public static Task<MatrixTextClient> ConnectAsync(string userId, string password, string deviceId, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken, ILogger? logger = null)
        => ConnectAsync(userId, password, deviceId, httpClientFactory, cancellationToken, logger,
            (delay, token) => Task.Delay(delay, token));

    internal static async Task<MatrixTextClient> ConnectAsync(string userId, string password, string deviceId,
        IHttpClientFactory httpClientFactory, CancellationToken cancellationToken, ILogger? logger,
        Func<TimeSpan, CancellationToken, Task> retryDelayAsync)
    {
        if (logger == null)
            logger = NullLogger<MatrixTextClient>.Instance;

        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogError("User ID cannot be null or empty.");
            throw new ArgumentException("User ID cannot be null or empty.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogError("Password cannot be null or empty.");
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            logger.LogError("Device ID cannot be null or empty.");
            throw new ArgumentException("Device ID cannot be null or empty.", nameof(deviceId));
        }

        if (!UserId.TryParse(userId, out var parsedUserId) || parsedUserId == null)
        {
            logger.LogError("The user id '{UserId}' seems invalid, it should look like: '@user:example.org'.", userId);
            throw new ArgumentException("The user id seems invalid, it should look like: '@user:example.org'.", nameof(userId));
        }

        var baseUri = $"https://{parsedUserId.Domain}";
        if (!Uri.IsWellFormedUriString(baseUri, UriKind.Absolute))
        {
            logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", baseUri);
            throw new ArgumentException("The server address seems invalid, it should be a well formed Uri.", nameof(userId));
        }
        logger.LogInformation("Connecting to {Url}", baseUri);
        HttpClientParameters httpClientParameters = new(httpClientFactory, baseUri, null, logger, cancellationToken)
        {
            RetryDelayAsync = retryDelayAsync
        };
        var wkUri = await MatrixHelper.FetchWellKnownUriAsync(httpClientParameters).ConfigureAwait(false);
        if (wkUri.HomeServer == null || string.IsNullOrEmpty(wkUri.HomeServer.BaseUrl))
        {
            logger.LogError("Failed to load home server uri from provided server.");
            throw new InvalidOperationException("Failed to load home server uri from provided server.");
        }
        baseUri = wkUri.HomeServer.BaseUrl;
        if (string.IsNullOrEmpty(baseUri))
        {
            logger.LogError("The 'base_url' returned by server property is empty.");
            throw new InvalidOperationException("The 'base_url' property returned by the server is empty.");
        }
        if (baseUri.EndsWith('/')) // according to doc, it may end with a trailing slash
            baseUri = baseUri.Substring(0, baseUri.Length - 1);

        logger.LogInformation("Resolved Base URL: {BaseUrl}", baseUri);
        httpClientParameters.BaseUri = baseUri;

        if (!Uri.IsWellFormedUriString(baseUri, UriKind.Absolute))
        {
            logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", baseUri);
            throw new InvalidOperationException("The resolved base uri seems invalid, it should be a well formed Uri.");
        }

        var versions = await MatrixHelper.FetchSupportedSpecVersionsAsync(httpClientParameters).ConfigureAwait(false);
        if (versions.Versions == null || versions.Versions.Count == 0)
        {
            logger.LogError("Failed to load supported spec versions from server.");
            throw new InvalidOperationException("Failed to load supported spec versions from server.");
        }

        var parsedVersions = versions.Versions.Select(v => SpecVersion.TryParse(v, out var cv) ? cv! : throw new InvalidOperationException($"Failed to parse server spec version number {v}"))
            .OrderBy(v => v, SpecVersion.Comparer.Instance).ToList();

        if (!parsedVersions.Contains(CurrentSpecVersion))
            logger.LogWarning("The server does not support the spec version which was used to implement this library ({ExpectedVersion}), so errors may occur. Supported versions by the server are: {SupportedVersions}",
                CurrentSpecVersion.VersionString, string.Join(',', parsedVersions));

        var authFlows = await MatrixHelper.FetchSupportedAuthFlowsAsync(httpClientParameters).ConfigureAwait(false);
        var authFlowsList = authFlows.Flows.Select(f => f.Type).ToList();
        if (!authFlowsList.Contains("m.login.password"))
        {
            logger.LogError("The server does not support password based authentication. Supported types: {Types}", string.Join(", ", authFlows));
            throw new InvalidOperationException("The server does not support password based authentication.");
        }

        var loginResponse = await MatrixHelper.PasswordLoginAsync(httpClientParameters, userId, password, deviceId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(loginResponse.AccessToken))
        {
            logger.LogError("Failed to login to server.");
            throw new InvalidOperationException("Failed to login to server.");
        }

        httpClientParameters.BearerToken = loginResponse.AccessToken;

        // A successful login must not be repeated just because initialization hit a
        // network error or an unavailable server. Retain this session while retrying.
        var serverCapabilities = await MatrixRetry.ExecuteAsync(httpClientParameters, "Fetching capabilities",
            () => MatrixHelper.FetchCapabilitiesAsync(httpClientParameters)).ConfigureAwait(false);
        httpClientParameters.RateLimiter = new LeakyBucket(10, serverCapabilities.Capabilities.RateLimit?.MaxRequestsPerHour ?? 600);
        var client = new MatrixTextClient(httpClientParameters, parsedUserId, parsedVersions, serverCapabilities.Capabilities);
        await MatrixRetry.ExecuteAsync(httpClientParameters, "Initializing Matrix", client.InitAsync).ConfigureAwait(false);
        client.IsSyncing = true;
        _ = client.SyncAsync();
        return client;
    }

    internal async Task<Filter> SetFilterAsync(Filter filter)
    {
        Logger.LogInformation("Setting filter new filter");
        var filterResponse = await MatrixHelper.PostFilterAsync(HttpClientParameters, CurrentUser, filter).ConfigureAwait(false);
        var filterId = filterResponse.FilterId;
        if (string.IsNullOrEmpty(filterId))
        {
            Logger.LogError("Failed to set filter.");
            throw new InvalidOperationException("Failed to set filter.");
        }

        var updatedFilter = await MatrixHelper.GetFilterAsync(HttpClientParameters, CurrentUser, filterId).ConfigureAwait(false);
        if (updatedFilter != null)
        {
            updatedFilter.FilterId = filterId;
            Filter = updatedFilter;
            return updatedFilter;
        }

        Logger.LogError("Failed to get filter after setting it.");
        throw new InvalidOperationException("Failed to get filter after setting it.");
    }

    private async Task InitAsync()
    {
        await MatrixHelper.PutPresenceAsync(HttpClientParameters, CurrentUser, new PresenceRequest() {Presence = Presence.Online }).ConfigureAwait(false);
        // We only want to receive text messages, filter out spam we're not interested in
        Filter filter = new()
        {
            Room = new()
            {
                Ephemeral = new()
                {
                    NotTypes = new() { "m.typing", "m.receipt" },
                    //LazyLoadMembers = true
                },
                Timeline = new()
                {
                    //LazyLoadMembers = true,
                },
                State = new()
                {
                    NotTypes = new() { "m.room.join_rules", "m.room.guest_access", "m.room.avatar", "m.room.history_visibility", "m.room.power_levels", "im.vector.modular.widgets" },
                    LazyLoadMembers = true
                },
                AccountData = new()
                {
                    NotTypes = new() { "m.fully_read" }
                }
            }
        };

        filter = await SetFilterAsync(filter).ConfigureAwait(false);
        if(filter.FilterId == null)
            Logger.LogWarning("No filter ID was returned after setting a filter. This should not happen. It won't break the client, but unnecessary events will be received.");
    }

    internal async Task SyncAsync()
    {
        // Refresh-token support is not advertised by this client. A terminal token
        // error is surfaced to the caller rather than silently reauthenticating.
        var request = new SyncParameters
        {
            FullState = false,
            SetPresence = Presence.Online,
            Timeout = 60000,
            Filter = Filter?.FilterId
        };

        Exception? failure = null;
        IsSyncing = true;
        try
        {
            while (!HttpClientParameters.CancellationToken.IsCancellationRequested)
            {
                var response = await MatrixRetry.ExecuteAsync(HttpClientParameters, "Matrix sync",
                    () => MatrixHelper.GetSyncAsync(HttpClientParameters, request)).ConfigureAwait(false);
                await HandleSyncResponseAsync(response).ConfigureAwait(false);
                request.Since = response.NextBatch;
            }
        }
        catch (OperationCanceledException) when (HttpClientParameters.CancellationToken.IsCancellationRequested)
        {
            // Normal shutdown, not a failed connection.
        }
        catch (Exception ex)
        {
            failure = ex;
            Logger.LogError(ex, "Matrix sync stopped after a terminal error.");
        }
        finally
        {
            IsSyncing = false;
            if (!MessageChannel.Writer.TryComplete(failure))
                Logger.LogError("Sync ended, but failed to complete message channel.");
        }
    }

    private async Task WriteSyncResponseToFileAsync(SyncResponse response)
    {
        if(response == null)
            return;

        if(response.AccountData?.Events?.Count == 0 &&
            response.Presence?.Events?.Count == 0 &&
            response.Rooms?.Joined?.Count == 0)
            return; // do not log boring empty syncs

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "matrix");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"sync_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
            using var stream = File.OpenWrite(path);
            await JsonSerializer.SerializeAsync(stream, response).ConfigureAwait(false);
            Logger.LogInformation("Sync response written to {Path}", path);
        }
        catch(Exception ex)
        {
            Logger.LogError(ex, "Error writing sync response to file.");
        }
    }


    private LeakyBucket _receiptRateLimiter = new LeakyBucket(1, 30);

    private async Task HandleSyncResponseAsync(SyncResponse response)
    {
        if (response == null)
            return;

        var cancellationToken = HttpClientParameters.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (DebugMode)
            await WriteSyncResponseToFileAsync(response).ConfigureAwait(false);

        List<ReceivedTextMessage> messages = new();
        if (response.AccountData?.Events is { } accountData)
            HandleAccountDataReceived(null, accountData);
        if (response.Presence?.Events is { } presence)
            HandlePresenceReceived(presence);

        if (response.Rooms?.Joined is { } joined)
        {
            foreach (var pair in joined)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!RoomId.TryParse(pair.Key, out var roomId) || roomId == null || pair.Value == null)
                {
                    Logger.LogWarning("Received invalid joined room entry: {RoomId}", pair.Key);
                    continue;
                }

                var roomEvents = pair.Value;
                if (roomEvents.Summary != null)
                    HandleRoomSummaryReceived(roomId, roomEvents.Summary);
                if (roomEvents.AccountData?.Events is { } roomAccountData)
                    HandleAccountDataReceived(roomId, roomAccountData);
                if (roomEvents.Ephemeral?.Events is { } ephemeral)
                    HandleEphemeralReceived(roomId, ephemeral);
                if (roomEvents.State?.Events is { } state)
                    HandleStateReceived(roomId, state);
                if (roomEvents.Timeline?.Events is { } timeline)
                    HandleTimelineReceived(roomId, timeline, messages);
            }
        }

        // Stripped invite state can already tell us that a room is encrypted.
        // Record it before delivering messages or making optional join requests.
        if (response.Rooms?.Invites is { } inviteState)
        {
            foreach (var pair in inviteState)
                HandleInviteEncryptionState(pair.Key, pair.Value);
        }

        // Malformed events are skipped at their own boundary. Delivery failures or
        // unexpected processing errors must propagate, not advance the sync token.
        foreach (var message in messages)
            await MessageChannel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);

        // Optional HTTP side effects cannot discard messages already collected.
        if (response.Rooms?.Invites is { } invites)
        {
            foreach (var pair in invites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await HandleInviteReceivedAsync(pair.Key, pair.Value).ConfigureAwait(false);
            }
        }

        foreach (var room in _rooms.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = room.LastMessage;
            if (message == null || message.EventId == room.LastReceiptEventId || !_receiptRateLimiter.Leak())
                continue;

            try
            {
                await message.SendReceiptAsync().ConfigureAwait(false);
                room.LastReceiptEventId = message.EventId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or MatrixResponseException or JsonException or OperationCanceledException)
            {
                Logger.LogWarning(ex, "Failed to send read marker in room {RoomId}.", room.RoomId.Full);
            }
        }
    }

    private void HandleRoomSummaryReceived(MatrixId roomId, RoomSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(summary, nameof(summary));

        var users = summary?.Heroes?.Select(s => UserId.TryParse(s, out MatrixId? userId) ? userId : null)
            ?.Where(id => id != null)?.Select(id => id!)
            ?.Select(GetOrAddUser)?.ToList();

        var room = GetOrAddRoom(roomId);

        if(users == null || users.Count == 0)
            return;

        foreach (var user in users)
        {
            var roomUser = GetOrAddUser(user, room);
        }
    }

    private void HandlePresenceReceived(List<MatrixEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        foreach(var ev in events)
        {
            if (ev == null)
            {
                Logger.LogWarning("Received null presence event.");
                continue;
            }
            if(ev.Type != "m.presence")
            {
                Logger.LogWarning("Received event of type {Type} in presence events.", ev.Type);
                continue;
            }

            if (ev.Content is not { ValueKind: JsonValueKind.Object })
            {
                Logger.LogWarning("Received presence event without object content.");
                continue;
            }

            var userId = UserId.TryParse(ev.Sender, out MatrixId? id) ? id : null;
            if(userId == null)
            {
                Logger.LogWarning("Received presence event with invalid sender ID: {Sender}", ev.Sender);
                continue;
            }
            var user = GetOrAddUser(userId);

            PresenceEvent? parsedPresence;
            try
            {
                parsedPresence = JsonSerializer.Deserialize<PresenceEvent>(ev.Content.Value);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Could not interpret presence event from {Sender}.", ev.Sender);
                continue;
            }
            if (parsedPresence == null || !Enum.IsDefined(parsedPresence.Presence))
            {
                Logger.LogWarning("Received presence event with no valid content.");
                continue;
            }

            lock(user)
            {
                if(parsedPresence.CurrentlyActive != null)
                    user.CurrentlyActive = parsedPresence.CurrentlyActive;

                if(parsedPresence.AvatarUrl != null)
                    user.AvatarUrl = parsedPresence.AvatarUrl;

                if(parsedPresence.DisplayName != null)
                    user.DisplayName = parsedPresence.DisplayName;

                if(parsedPresence.CurrentlyActive != null)
                    user.CurrentlyActive = parsedPresence.CurrentlyActive;

                user.Presence = parsedPresence.Presence;

                if(parsedPresence.StatusMsg != null)
                    user.StatusMessage = parsedPresence.StatusMsg;
            }
        }
    }

    
    private void HandleEphemeralReceived(MatrixId roomId, List<MatrixEvent> events)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        foreach(var e in events.Where(ev => ev != null && ev.Type != "m.typing" && ev.Type != "m.receipt"))
        {
            // Just track these for now. We are not interested in typing and receipt events
            Logger.LogWarning("Received unknown Ephemeral event type in room {RoomId}: {Type}.", roomId.Full, e.Type);
        }
    }

    
    private void HandleStateReceived(MatrixId roomId, List<ClientEventWithoutRoomID> events)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        var room = GetOrAddRoom(roomId);

        foreach (var e in events)
        {
            // Encryption cannot be disabled by clearing/redacting its content or
            // by advertising an algorithm this text-only client cannot interpret.
            if (e?.Type == "m.room.encryption" && e.StateKey == "")
            {
                lock (room)
                    room.HasEncryptionState = true;
            }
            if (e == null || e.StateKey == null || string.IsNullOrEmpty(e.Type) ||
                e.Content is not { ValueKind: JsonValueKind.Object })
            {
                Logger.LogWarning("Received malformed state event in room {RoomId}. Type {Type}", roomId.Full, e?.Type);
                continue;
            }

            var stateEvent = new RoomStateEvent(e);
            lock (room)
            {
                room.StateEvents = room.StateEvents.SetItem((e.Type, e.StateKey), stateEvent);
            }

            try
            {
                ApplyStateEvent(room, e);
            }
            catch (JsonException ex)
            {
                // One malformed/redacted event must not discard the rest of the sync.
                Logger.LogWarning(ex, "Could not interpret state event {Type} in room {RoomId}.", e.Type, roomId.Full);
            }
        }
    }

    private void ApplyStateEvent(Room room, ClientEventWithoutRoomID e)
    {
        var roomId = room.RoomId;
        switch (e.Type)
        {
            case "m.room.member":
                HandleRoomMemberEvent(room, e);
                break;
            case "m.room.name":
                var nameEvent = JsonSerializer.Deserialize<RoomNameEvent>(e.Content!.Value);
                if (nameEvent == null)
                {
                    Logger.LogWarning("Received m.room.name event deserialize returned null in room {RoomId}.", roomId.Full);
                    break;
                }

                lock (room)
                {
                    room.DisplayName = nameEvent.Name;
                }
                break;
            case "m.room.canonical_alias":
                var parsed = JsonSerializer.Deserialize<CanonicalAliasEvent>(e.Content!.Value);
                RoomAlias.TryParse(parsed?.Alias, out MatrixId? alias);
                var altAliases = parsed?.AltAliases?.Select(a => RoomAlias.TryParse(a, out MatrixId? id) ? id : null).Where(id => id != null).Select(id => id!).ToList();
                lock (room)
                {
                    room.CanonicalAlias = alias;
                    if (altAliases != null)
                        room.AltAliases = room.AltAliases?.Union(altAliases).ToList() ?? altAliases;
                }
                break;
            case "m.room.encryption":
                if (e.StateKey == "")
                    HandleRoomEncryptionEvent(room, e.Content);
                break;
            case "m.room.power_levels":
            case "m.room.join_rules":
            case "m.room.topic":
            case "m.room.avatar":
            case "m.room.create":
            case "m.room.pinned_events":
            case "m.room.tombstone":
            case "m.room.retention":
            case "m.room.related_groups":
            case "m.room.history_visibility":
            case "m.room.guest_access":
                // Retained as raw state, but not projected onto other Room properties.
                break;
            default:
                // Custom and future event types are normal in Matrix. Preserve
                // their content without requiring a built-in model or vendor allowlist.
                Logger.LogDebug("Retained unhandled state event type in room {RoomId}: {Type}.", roomId.Full, e.Type);
                break;
        }
    }

    private void HandleRoomMemberEvent(Room room, ClientEventWithoutRoomID e)
    {
        var roomMember = JsonSerializer.Deserialize<RoomMemberEvent>((JsonElement)e.Content!);
        if (roomMember == null || !Enum.IsDefined(roomMember.Membership))
        {
            Logger.LogWarning("Received invalid m.room.member content in room {RoomId}.", room.RoomId.Full);
            return;
        }
        var userIdStr = e.StateKey ?? e.Sender;
        if (!UserId.TryParse(userIdStr, out MatrixId? userId) || userId == null)
        {
            Logger.LogWarning("Received m.room.member event with invalid user ID: {UserId} in room {RoomId}.", userIdStr, room.RoomId.Full);
            return;
        }
        var user = GetOrAddUser(userId);
        RoomUser? roomUser = GetOrAddUser(user, room);

        if (roomUser.DisplayName != roomMember.DisplayName || roomUser.Membership != roomMember.Membership)
        {
            lock (roomUser)
            {
                roomUser.DisplayName = roomMember.DisplayName;
                roomUser.Membership = roomMember.Membership;
            }
        }

        if (roomMember.IsDirect == true)
        {
            lock (room)
            {
                room.IsDirect = true;
            }
        }
    }

    private void HandleInviteEncryptionState(string roomIdString, InvitedRoomEvents? invitedRoom)
    {
        if (invitedRoom?.InviteState?.Events is not { } events ||
            !RoomId.TryParse(roomIdString, out var roomId) || roomId == null)
            return;

        foreach (var ev in events)
        {
            if (ev?.Type != "m.room.encryption" || ev.StateKey != "")
                continue;

            var room = GetOrAddRoom(roomId);
            lock (room)
                room.HasEncryptionState = true;
            try
            {
                HandleRoomEncryptionEvent(room, ev.Content);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Could not interpret encryption invite state in room {RoomId}.", roomId.Full);
            }
        }
    }

    private async Task HandleInviteReceivedAsync(string roomIdString, InvitedRoomEvents? invitedRoom)
    {
        if (invitedRoom == null || !RoomId.TryParse(roomIdString, out var roomId) || roomId == null)
        {
            Logger.LogWarning("Received invite for room with invalid ID: {RoomId}", roomIdString);
            return;
        }

        var isDirect = false;
        var inviterUserId = (string?)null;

        if (invitedRoom.InviteState?.Events != null)
        {
            foreach (var ev in invitedRoom.InviteState.Events)
            {
                if (ev?.Type == "m.room.member" && ev.StateKey == CurrentUser.Full)
                {
                    try
                    {
                        var memberEvent = JsonSerializer.Deserialize<RoomMemberEvent>(ev.Content);
                        if (memberEvent?.IsDirect == true)
                            isDirect = true;
                        inviterUserId = ev.Sender;
                    }
                    catch (JsonException ex)
                    {
                        Logger.LogWarning(ex, "Could not interpret invite state in room {RoomId}.", roomId.Full);
                    }
                }
            }
        }

        Logger.LogInformation("Received invite for room {RoomId} (isDirect: {IsDirect}, inviter: {Inviter})",
            roomId.Full, isDirect, inviterUserId ?? "unknown");

        if (AutoJoinOnInvite)
        {
            try
            {
                await MatrixHelper.PostJoinRoomAsync(HttpClientParameters, roomId).ConfigureAwait(false);
                var room = GetOrAddRoom(roomId);
                lock (room)
                {
                    room.IsDirect = isDirect;
                }
                Logger.LogInformation("Auto-joined room {RoomId}", roomId.Full);
            }
            catch (OperationCanceledException) when (HttpClientParameters.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or MatrixResponseException or JsonException or OperationCanceledException)
            {
                Logger.LogError(ex, "Failed to auto-join room {RoomId}", roomId.Full);
            }
        }
    }

    private void HandleTimelineReceived(MatrixId roomId, List<ClientEventWithoutRoomID> events, List<ReceivedTextMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        ArgumentNullException.ThrowIfNull(messages, nameof(messages));
        var room = GetOrAddRoom(roomId);

        foreach (var e in events)
        {
            if (e == null)
            {
                Logger.LogWarning("Received null timeline event in room {RoomId}.", roomId.Full);
                continue;
            }
            try
            {
                // The presence of state_key (including "") identifies state, not
                // a hardcoded list of event types. State precedes message dispatch.
                if (e.StateKey != null)
                    HandleStateReceived(roomId, [e]);
                else if (e.Type == "m.room.message")
                    HandleMessageReceived(roomId, messages, room, e);
                else if (e.Type == "m.room.encrypted")
                    HandleEncryptedEvent(room, e);
                else
                    Logger.LogDebug("Ignoring unhandled timeline event type {Type} in room {RoomId}.", e.Type, roomId.Full);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
            {
                Logger.LogWarning(ex, "Could not interpret timeline event {Type} in room {RoomId}.", e.Type, roomId.Full);
            }
        }
    }

    private void HandleEncryptedEvent(Room room, ClientEventWithoutRoomID e)
    {
        ArgumentNullException.ThrowIfNull(room, nameof(room));
        ArgumentNullException.ThrowIfNull(e, nameof(e));

        if(e.Content == null)
        {
            Logger.LogWarning("Received m.room.encrypted event with no content in room {RoomId}.", room.RoomId.Full);
            return;
        }

        var encryptedEvent = JsonSerializer.Deserialize<RoomEncryptedEvent>((JsonElement)e.Content);
        if(encryptedEvent == null)
        {
            Logger.LogWarning("Received m.room.encrypted event deserialize returned null in room {RoomId}.", room.RoomId.Full);
            return;
        }

        if(encryptedEvent.Algorithm != room.Encryption?.Algorithm)
        {
            Logger.LogWarning("Received m.room.encrypted event with mismatching algorithm for room {RoomId}. Expected: {ExpectedAlgorithm}, Received: {ReceivedAlgorithm}", room.RoomId.Full, room.Encryption?.Algorithm, encryptedEvent.Algorithm);
            return;
        }

        // TODO handle ciphertext
    }

    private void HandleRoomEncryptionEvent(Room room, JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.Object })
        {
            Logger.LogWarning("Received encryption state without object content in room {RoomId}.", room.RoomId.Full);
            return;
        }

        var encryptionEvent = JsonSerializer.Deserialize<RoomEncryptionEvent>(content.Value);
        if(encryptionEvent == null)
        {
            Logger.LogWarning("Received m.room.encryption event deserialize returned null in room {RoomId}.", room.RoomId.Full);
            return;
        }

        if (string.IsNullOrWhiteSpace(encryptionEvent.Algorithm))
        {
            Logger.LogWarning("Received encryption state without an algorithm in room {RoomId}.", room.RoomId.Full);
            return;
        }

        lock (room)
        {
            room.Encryption = new RoomEncryption(encryptionEvent.Algorithm);
        }
        if (encryptionEvent.Algorithm != "m.megolm.v1.aes-sha2")
            Logger.LogWarning("Received unknown encryption algorithm {Algorithm} in room {RoomId}.", encryptionEvent.Algorithm, room.RoomId.Full);
    }

    private void HandleMessageReceived(MatrixId roomId, List<ReceivedTextMessage> messages, Room room, ClientEventWithoutRoomID e)
    {
        if (e.Content == null)
        {
            Logger.LogWarning("Received timeline event with no content in room {RoomId}. Type {Type}", roomId.Full, e.Type);
            return;
        }

        var messageEvent = JsonSerializer.Deserialize<RoomMessageEvent>((JsonElement)e.Content);
        if (messageEvent == null)
        {
            Logger.LogWarning("Received m.room.message event deserialize returned null in room {RoomId}.", roomId.Full);
            return;
        }
        var userIdStr = e.Sender;
        if (!UserId.TryParse(userIdStr, out MatrixId? userId) || userId == null)
        {
            Logger.LogWarning("Received m.room.message event with invalid user ID: {UserId} in room {RoomId}.", userIdStr, roomId.Full);
            return;
        }
        if (messageEvent.MsgType is not "m.text" and not "m.notice")
        {
            Logger.LogInformation("Ignoring m.room.message event with non-text message type {MsgType} in room {RoomId}.", messageEvent.MsgType, roomId.Full);
            return;
        }

        var serverTs = DateTimeOffset.FromUnixTimeMilliseconds(e.OriginServerTs);

        var user = GetOrAddUser(userId);
        var roomUser = GetOrAddUser(user, room);
        var threadId = (string?)null;
        if(messageEvent.RelatesTo != null && messageEvent.RelatesTo.EventId != null
            && messageEvent.RelatesTo.RelType == "m.thread")
        {
            threadId = messageEvent.RelatesTo.EventId;
        }

        var message = new ReceivedTextMessage(messageEvent.Body, room, roomUser, e.EventId, serverTs, threadId, messageEvent.MsgType, this);
        if (messageEvent.Mentions != null && messageEvent.Mentions.UserIds != null && messageEvent.Mentions.UserIds.Count > 0)
        {
            message.Mentions = messageEvent.Mentions.UserIds
                .Select(id => UserId.TryParse(id, out MatrixId? mentionId) ? mentionId : null)
                .Where(id => id != null)
                .Select(id => id!)
                .Select(id => GetOrAddUser(id))
                .Select(u => GetOrAddUser(u, room)).ToList();
        }

        messages.Add(message);
        room.LastMessage = message;
    }

    internal User GetOrAddUser(MatrixId id)
    {
        return _users.GetOrAdd(id.Full, (_) => new User(id));
    }

    internal Room GetOrAddRoom(MatrixId id)
    {
        return _rooms.GetOrAdd(id.Full, (_) => new Room(id, this));
    }

    internal bool IsRoomEncrypted(MatrixId id)
        => _rooms.TryGetValue(id.Full, out var room) && room.IsEncrypted;

    internal RoomUser GetOrAddUser(User user, Room room)
    {
        if(room.Users.TryGetValue(user.UserId.Full, out var roomUser) && roomUser != null)
            {
                return roomUser;
            }

        lock(room)
        {
            if(room.Users.TryGetValue(user.UserId.Full, out roomUser) && roomUser != null)
            {
                return roomUser;
            }
            roomUser = new RoomUser(user);
            room.Users = room.Users.Add(user.UserId.Full, roomUser);
            return roomUser;
        }
    }

    private void HandleAccountDataReceived(MatrixId? roomId, List<MatrixEvent> accountData)
    {
        // Sadly, we don't have any account data events we're interested in
        foreach(var ev in accountData)
        {
            Logger.LogDebug("Received account data event: {Event} in Room {Room}", ev?.Type, roomId?.Full ?? "(global)");
        }
    }
}

