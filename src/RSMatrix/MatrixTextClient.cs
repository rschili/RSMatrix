using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Models;
using RSMatrix.Http;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using RSFlowControl;
using RSMatrix.Crypto;

namespace RSMatrix;

/// <summary>
/// The low level class for interacting with the Matrix server.
/// </summary>
public sealed class MatrixClient
{
    internal ILogger Logger => HttpClientParameters.Logger;
    public MatrixId CurrentUser { get; }
    internal IList<SpecVersion> SupportedSpecVersions { get; }
    internal static SpecVersion CurrentSpecVersion { get; } = new SpecVersion(1, 17, null, null);
    internal HttpClientParameters HttpClientParameters { get; private set; }
    internal Capabilities ServerCapabilities { get; private set; }

    internal Filter? Filter { get; private set; }

    /// <summary>Set when the client was connected with an encryption store directory.</summary>
    internal EncryptionEngine? Encryption { get; set; }

    /// <summary>
    /// Whether the client was connected with <see cref="EncryptionOptions"/>. Without them,
    /// messages in encrypted rooms are not received and sending to them throws.
    /// </summary>
    public bool IsEncryptionEnabled => Encryption != null;

    /// <summary>
    /// Replaces the account's cross-signing keys and recovery key, for when the recovery key is lost or the
    /// keys are damaged. The new recovery key goes to <see cref="EncryptionOptions.SaveRecoveryKeyAsync"/> first,
    /// then this device is signed with the new keys.
    /// </summary>
    /// <remarks>
    /// Other devices of the account lose their cross-signature: they need the new recovery key once to be signed again.
    /// Other users' clients see that the account's identity changed. The server asks for the account password,
    /// which the client has; servers that want a confirmation in the browser instead are not supported.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Encryption is not enabled, or the server refused the new keys.</exception>
    public Task ResetCrossSigningAsync()
        => Encryption?.ResetCrossSigningAsync()
           ?? throw new InvalidOperationException("Cross-signing needs encryption; connect with EncryptionOptions.");

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

    internal const int IssueChannelCapacity = 100;
    private readonly Channel<ClientIssue> _issueChannel = Channel.CreateBounded<ClientIssue>(
        new BoundedChannelOptions(IssueChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Problems the client ran into while it keeps running, like messages it could not decrypt.
    /// Reading is optional. The channel keeps the latest 100 issues and drops older ones.
    /// It completes when syncing ends.
    /// </summary>
    public ChannelReader<ClientIssue> Issues => _issueChannel.Reader;

    private long _lastSyncAtUnixMilliseconds;

    /// <summary>
    /// When the last sync with the server succeeded, or null before the first one.
    /// A sync is a long poll that returns at least every minute, so an older value means the connection is down.
    /// </summary>
    public DateTimeOffset? LastSyncAt => Interlocked.Read(ref _lastSyncAtUnixMilliseconds) is var value and > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(value)
        : null;

    internal bool DebugMode = false;

    /// <summary>
    /// When true, the client will automatically join rooms when invited.
    /// Default is false.
    /// </summary>
    public bool AutoJoinOnInvite { get; set; } = false;

    public bool IsSyncing { get; private set;}

    // Kept to log in again when the server ends the session (soft logout).
    private string? _password;
    private string? _deviceId;
    private SessionStore? _sessionStore;
    private string? _resumeSyncToken;

    private MatrixClient(HttpClientParameters parameters,
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
    /// Creates a MatrixClient for testing purposes without connecting to a server.
    /// </summary>
    internal static MatrixClient CreateForTesting(HttpClientParameters parameters, MatrixId userId)
    {
        var versions = new List<SpecVersion> { CurrentSpecVersion };
        var capabilities = new Capabilities();
        return new MatrixClient(parameters, userId, versions, capabilities);
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
    /// Once logged in, or when resuming a stored session, transient failures (network errors,
    /// unavailable server, rate limits) are retried until cancellation. Before the first login they
    /// throw, since they more likely point to a wrong configuration. If the server ends the session but allows
    /// logging in again (soft logout), the client logs in again with the same device and continues
    /// where it stopped. Other errors, like a wrong password, end the connection: ConnectAsync throws,
    /// or the Messages channel faults if the client was already running.
    /// </remarks>
    /// <param name="userId">User id e.g. @user:example.org</param>
    /// <param name="password">password for the user</param>
    /// <param name="deviceId">device id to identify this client to the server</param>
    /// <param name="httpClientFactory">factory to use for creating http clients</param>
    /// <param name="cancellationToken">cancellation token used to disconnect</param>
    /// <param name="logger">logger</param>
    /// <param name="sessionStoreDirectory">
    /// Keeps the session (access token and sync position) in this directory, so the next connect with
    /// the same user and device ID resumes instead of logging in and syncing from scratch. Messages sent
    /// while the client was offline are delivered after resuming. The files contain the access token in
    /// plain JSON; protect the directory accordingly. It can be the same as the encryption store directory.
    /// Only one client at a time can use a session.
    /// </param>
    /// <param name="encryption">
    /// Turns on end-to-end encryption; see <see cref="EncryptionOptions"/> for what it needs and does.
    /// Without it, encrypted messages are ignored and sending to encrypted rooms throws <see cref="NotSupportedException"/>.
    /// </param>
    /// <exception cref="ArgumentException">Provided arguments not valid</exception>
    /// <exception cref="InvalidOperationException">Mostly if connection fails</exception>
    public static Task<MatrixClient> ConnectAsync(string userId, string password, string deviceId, IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken, ILogger? logger = null, string? sessionStoreDirectory = null,
        EncryptionOptions? encryption = null)
        => ConnectAsync(userId, password, deviceId, httpClientFactory, cancellationToken, logger,
            (delay, token) => Task.Delay(delay, token), sessionStoreDirectory, encryption);

    internal static async Task<MatrixClient> ConnectAsync(string userId, string password, string deviceId,
        IHttpClientFactory httpClientFactory, CancellationToken cancellationToken, ILogger? logger,
        Func<TimeSpan, CancellationToken, Task> retryDelayAsync, string? sessionStoreDirectory = null,
        EncryptionOptions? encryption = null)
    {
        if (logger == null)
            logger = NullLogger<MatrixClient>.Instance;

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

        if (encryption != null && (string.IsNullOrWhiteSpace(encryption.StoreDirectory) ||
            encryption.GetRecoveryKeyAsync == null || encryption.SaveRecoveryKeyAsync == null))
        {
            throw new ArgumentException($"{nameof(EncryptionOptions)} needs a store directory and both recovery key callbacks.", nameof(encryption));
        }

        // Fail on a wrong or busy store before creating a session on the server.
        EncryptionStore? encryptionStore = null;
        SessionStore? sessionStore = null;
        try
        {
            if (encryption != null)
                encryptionStore = EncryptionEngine.OpenStore(encryption.StoreDirectory, parsedUserId.Full, deviceId);
            if (sessionStoreDirectory != null)
                sessionStore = SessionStore.Open(sessionStoreDirectory, parsedUserId.Full, deviceId);
            return await ConnectAsync(parsedUserId, password, deviceId, httpClientFactory, cancellationToken, logger,
                retryDelayAsync, encryptionStore, sessionStore, encryption).ConfigureAwait(false);
        }
        catch
        {
            encryptionStore?.Dispose();
            sessionStore?.Dispose();
            throw;
        }
    }

    private static async Task<MatrixClient> ConnectAsync(MatrixId userId, string password, string deviceId,
        IHttpClientFactory httpClientFactory, CancellationToken cancellationToken, ILogger logger,
        Func<TimeSpan, CancellationToken, Task> retryDelayAsync, EncryptionStore? encryptionStore, SessionStore? sessionStore,
        EncryptionOptions? encryption)
    {
        HttpClientParameters http = new(httpClientFactory, "", null, logger, cancellationToken)
        {
            RetryDelayAsync = retryDelayAsync
        };

        string? syncToken = null;
        if (sessionStore?.Session is { } stored)
        {
            logger.LogInformation("Resuming the stored session at {Url}", stored.Homeserver);
            http.BaseUri = stored.Homeserver;
            http.BearerToken = stored.AccessToken;
            try
            {
                var whoAmI = await MatrixRetry.ExecuteAsync(http, "Checking the stored session",
                    () => MatrixHelper.GetWhoAmIAsync(http)).ConfigureAwait(false);
                if (whoAmI.UserId != userId.Full || (whoAmI.DeviceId != null && whoAmI.DeviceId != deviceId))
                    throw new InvalidOperationException(
                        $"The stored session belongs to {whoAmI.UserId} with device {whoAmI.DeviceId}, not {userId.Full} with device {deviceId}.");
                syncToken = stored.SyncToken;
            }
            catch (MatrixResponseException ex) when (ex.ErrorCode == "M_UNKNOWN_TOKEN")
            {
                http.BearerToken = null;
                syncToken = SyncTokenAfterLogout(ex, stored.SyncToken, encryptionStore != null, logger);
                await LoginAsync(http, userId, password, deviceId, retry: true).ConfigureAwait(false);
            }
        }
        else
        {
            // Without a session that worked before, a failure here is more likely a wrong configuration
            // than an outage, so it is reported right away instead of retried.
            http.BaseUri = await DiscoverHomeserverAsync(http, userId).ConfigureAwait(false);
            await LoginAsync(http, userId, password, deviceId, retry: false).ConfigureAwait(false);
        }
        sessionStore?.Save(CreateStoredSession(http, userId, deviceId, syncToken));

        var versions = await MatrixRetry.ExecuteAsync(http, "Fetching spec versions",
            () => MatrixHelper.FetchSupportedSpecVersionsAsync(http)).ConfigureAwait(false);
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

        var serverCapabilities = await MatrixRetry.ExecuteAsync(http, "Fetching capabilities",
            () => MatrixHelper.FetchCapabilitiesAsync(http)).ConfigureAwait(false);
        http.RateLimiter = new LeakyBucket(10, serverCapabilities.Capabilities.RateLimit?.MaxRequestsPerHour ?? 600);
        var client = new MatrixClient(http, userId, parsedVersions, serverCapabilities.Capabilities)
        {
            _password = password,
            _deviceId = deviceId,
            _sessionStore = sessionStore,
            _resumeSyncToken = syncToken,
        };
        if (encryptionStore != null)
            client.Encryption = await EncryptionEngine.StartAsync(encryptionStore, client, encryption!, password).ConfigureAwait(false);
        await MatrixRetry.ExecuteAsync(http, "Initializing Matrix", client.InitAsync).ConfigureAwait(false);
        client.IsSyncing = true;
        _ = client.SyncAsync();
        return client;
    }

    private static async Task<string> DiscoverHomeserverAsync(HttpClientParameters http, MatrixId userId)
    {
        var logger = http.Logger;
        var serverUri = $"https://{userId.Domain}";
        if (!Uri.IsWellFormedUriString(serverUri, UriKind.Absolute))
        {
            logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", serverUri);
            throw new ArgumentException("The server address seems invalid, it should be a well formed Uri.", nameof(userId));
        }
        logger.LogInformation("Connecting to {Url}", serverUri);
        http.BaseUri = serverUri;
        var wkUri = await MatrixHelper.FetchWellKnownUriAsync(http).ConfigureAwait(false);
        if (wkUri.HomeServer == null || string.IsNullOrEmpty(wkUri.HomeServer.BaseUrl))
        {
            logger.LogError("Failed to load home server uri from provided server.");
            throw new InvalidOperationException("Failed to load home server uri from provided server.");
        }
        var baseUri = wkUri.HomeServer.BaseUrl;
        if (baseUri.EndsWith('/')) // according to doc, it may end with a trailing slash
            baseUri = baseUri.Substring(0, baseUri.Length - 1);

        logger.LogInformation("Resolved Base URL: {BaseUrl}", baseUri);
        if (!Uri.IsWellFormedUriString(baseUri, UriKind.Absolute))
        {
            logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", baseUri);
            throw new InvalidOperationException("The resolved base uri seems invalid, it should be a well formed Uri.");
        }
        return baseUri;
    }

    /// <summary>
    /// Logs in with the password and sets the new access token. Transient failures are only retried
    /// when <paramref name="retry"/> is set, i.e. when this server and these credentials worked before.
    /// </summary>
    private static async Task LoginAsync(HttpClientParameters http, MatrixId userId, string password, string deviceId, bool retry)
    {
        var logger = http.Logger;
        Task<T> SendAsync<T>(string operation, Func<Task<T>> action)
            => retry ? MatrixRetry.ExecuteAsync(http, operation, action) : action();

        var authFlows = await SendAsync("Fetching login flows",
            () => MatrixHelper.FetchSupportedAuthFlowsAsync(http)).ConfigureAwait(false);
        var authFlowsList = authFlows.Flows.Select(f => f.Type).ToList();
        if (!authFlowsList.Contains("m.login.password"))
        {
            logger.LogError("The server does not support password based authentication. Supported types: {Types}", string.Join(", ", authFlowsList));
            throw new InvalidOperationException("The server does not support password based authentication.");
        }

        var loginResponse = await SendAsync("Logging in",
            () => MatrixHelper.PasswordLoginAsync(http, userId.Full, password, deviceId)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(loginResponse.AccessToken))
        {
            logger.LogError("Failed to login to server.");
            throw new InvalidOperationException("Failed to login to server.");
        }
        http.BearerToken = loginResponse.AccessToken;
        logger.LogInformation("Logged in as {UserId} with device {DeviceId}", userId.Full, deviceId);
    }

    /// <summary>
    /// Decides how to continue after the server rejected our access token. Returns the sync token to continue from.
    /// </summary>
    private static string? SyncTokenAfterLogout(MatrixResponseException error, string? syncToken, bool isEncryptionEnabled, ILogger logger)
    {
        if (error.SoftLogout)
        {
            logger.LogWarning("The server ended the session but allows logging in again (soft logout). Logging in with the same device.");
            return syncToken;
        }

        // Without soft_logout, the spec requires discarding all state kept for this device, including its keys.
        if (isEncryptionEnabled)
            throw new InvalidOperationException(
                "The server logged out this device, so its encryption keys must not be used anymore. " +
                "Use a new device ID and a new encryption store directory.", error);
        logger.LogWarning("The server logged out this device. Logging in again and syncing from scratch.");
        return null;
    }

    private static StoredSession CreateStoredSession(HttpClientParameters http, MatrixId userId, string deviceId, string? syncToken) => new()
    {
        UserId = userId.Full,
        DeviceId = deviceId,
        Homeserver = http.BaseUri,
        AccessToken = http.BearerToken!,
        SyncToken = syncToken,
    };

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
        // A resumed sync position needs the full room state once, since our room cache starts empty.
        var request = new SyncParameters
        {
            Since = _resumeSyncToken,
            FullState = _resumeSyncToken != null,
            SetPresence = Presence.Online,
            Timeout = 60000,
            Filter = Filter?.FilterId
        };

        Exception? failure = null;
        IsSyncing = true;
        var loggedInSinceLastSync = false;
        var isFirstResumedSync = _resumeSyncToken != null;
        try
        {
            while (!HttpClientParameters.CancellationToken.IsCancellationRequested)
            {
                SyncResponse response;
                try
                {
                    response = await MatrixRetry.ExecuteAsync(HttpClientParameters, "Matrix sync",
                        () => MatrixHelper.GetSyncAsync(HttpClientParameters, request),
                        onFirstTransientFailure: ex => ReportIssue(ClientIssueKind.SyncInterrupted,
                            $"Sync with the server failed, retrying until it works again: {ex.Message}", exception: ex)).ConfigureAwait(false);
                }
                // A token rejected right after logging in again won't get better with another login.
                catch (MatrixResponseException ex) when (ex.ErrorCode == "M_UNKNOWN_TOKEN" && _password != null && !loggedInSinceLastSync)
                {
                    await LoginAgainAsync(ex, request).ConfigureAwait(false);
                    loggedInSinceLastSync = true;
                    continue;
                }
                // Synapse answers a sync token it can't parse with 400 M_UNKNOWN; the spec defines no specific error.
                catch (MatrixResponseException ex) when (isFirstResumedSync && ex.StatusCode == HttpStatusCode.BadRequest)
                {
                    Logger.LogWarning(ex, "The server rejected the stored sync position. Syncing from scratch.");
                    request.Since = null;
                    request.FullState = false;
                    isFirstResumedSync = false;
                    continue;
                }
                loggedInSinceLastSync = false;
                isFirstResumedSync = false;
                await HandleSyncResponseAsync(response).ConfigureAwait(false);
                request.Since = response.NextBatch;
                request.FullState = false;
                SaveSession(request.Since);
                Interlocked.Exchange(ref _lastSyncAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
            Encryption?.Dispose();
            _sessionStore?.Dispose();
            if (!MessageChannel.Writer.TryComplete(failure))
                Logger.LogError("Sync ended, but failed to complete message channel.");
            _issueChannel.Writer.TryComplete();
        }
    }

    private async Task LoginAgainAsync(MatrixResponseException error, SyncParameters request)
    {
        HttpClientParameters.BearerToken = null;
        request.Since = SyncTokenAfterLogout(error, request.Since, Encryption != null, Logger);
        if (request.Since == null)
            request.FullState = false;
        await LoginAsync(HttpClientParameters, CurrentUser, _password!, _deviceId!, retry: true).ConfigureAwait(false);
        SaveSession(request.Since);
    }

    private void SaveSession(string? syncToken)
    {
        if (_sessionStore == null)
            return;
        var stored = _sessionStore.Session;
        if (stored != null && stored.SyncToken == syncToken && stored.AccessToken == HttpClientParameters.BearerToken)
            return;
        _sessionStore.Save(CreateStoredSession(HttpClientParameters, CurrentUser, _deviceId!, syncToken));
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

        // Room keys arrive as to-device messages and must be stored before decrypting the timeline.
        if (Encryption != null)
            await Encryption.HandleSyncAsync(response).ConfigureAwait(false);

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
        Encryption?.OnMembershipChanged(room.RoomId.Full, roomMember.Membership);

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
                ReportIssue(ClientIssueKind.AutoJoinFailed, $"Could not join room {roomId.Full} after an invite: {ex.Message}",
                    GetOrAddRoom(roomId), inviterUserId, exception: ex);
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
                    HandleEncryptedEvent(room, e, messages);
                else
                    Logger.LogDebug("Ignoring unhandled timeline event type {Type} in room {RoomId}.", e.Type, roomId.Full);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
            {
                Logger.LogWarning(ex, "Could not interpret timeline event {Type} in room {RoomId}.", e.Type, roomId.Full);
            }
        }
    }

    private void HandleEncryptedEvent(Room room, ClientEventWithoutRoomID e, List<ReceivedTextMessage> messages)
    {
        var decrypted = DecryptRoomEvent(room, e);
        if (decrypted?.Type == "m.room.message")
            HandleMessageReceived(room.RoomId, messages, room, decrypted, isEncrypted: true);
        else if (decrypted != null)
            Logger.LogDebug("Ignoring decrypted event type {Type} in room {RoomId}.", decrypted.Type, room.RoomId.Full);
    }

    /// <summary>Decrypts an m.room.encrypted event, or reports why it can't and returns null.</summary>
    internal ClientEventWithoutRoomID? DecryptRoomEvent(Room room, ClientEventWithoutRoomID e)
    {
        string failure;
        if (Encryption == null)
        {
            Logger.LogDebug("Ignoring encrypted event {EventId} in room {RoomId}: encryption is not enabled.", e.EventId, room.RoomId.Full);
            failure = "encryption is not enabled for this client";
        }
        else if (Encryption.DecryptRoomEvent(room.RoomId.Full, e, out var engineFailure) is { } decrypted)
        {
            return decrypted;
        }
        else
        {
            failure = engineFailure ?? "unknown error";
        }

        // Reactions are not delivered even when they can be decrypted.
        if (!IsAnnotation(e))
        {
            ReportIssue(ClientIssueKind.DecryptionFailed,
                $"Could not decrypt event {e.EventId} from {e.Sender} in room {room.RoomId.Full}: {failure}.",
                room, e.Sender, e.EventId);
        }
        return null;
    }

    private static bool IsAnnotation(ClientEventWithoutRoomID e)
        => e.Content is { ValueKind: JsonValueKind.Object } content &&
           content.TryGetProperty("m.relates_to", out var relatesTo) && relatesTo.ValueKind == JsonValueKind.Object &&
           relatesTo.TryGetProperty("rel_type", out var relType) && relType.ValueKind == JsonValueKind.String &&
           relType.GetString() == "m.annotation";

    internal void ReportIssue(ClientIssueKind kind, string message, Room? room = null, string? userId = null,
        string? eventId = null, Exception? exception = null)
    {
        RoomUser? user = null;
        if (room != null && userId != null && UserId.TryParse(userId, out var parsedUserId) && parsedUserId != null)
            user = GetOrAddUser(GetOrAddUser(parsedUserId), room);
        _issueChannel.Writer.TryWrite(new ClientIssue(kind, message, room, user, eventId, exception));
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

    private void HandleMessageReceived(MatrixId roomId, List<ReceivedTextMessage> messages, Room room, ClientEventWithoutRoomID e,
        bool isEncrypted = false)
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

        var message = new ReceivedTextMessage(messageEvent.Body, room, roomUser, e.EventId, serverTs, threadId, messageEvent.MsgType, this, isEncrypted);
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

