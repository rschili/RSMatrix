using System.Net;
using System.Runtime.ExceptionServices;
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
public sealed class MatrixClient : IAsyncDisposable
{
    internal ILogger Logger => HttpClientParameters.Logger;
    public MatrixId CurrentUser { get; }
    internal IList<SpecVersion> SupportedSpecVersions { get; }
    internal static SpecVersion CurrentSpecVersion { get; } = new SpecVersion(1, 17, null, null);
    internal HttpClientParameters HttpClientParameters { get; private set; }
    internal Capabilities ServerCapabilities { get; private set; }

    internal Filter? Filter { get; private set; }

    /// <summary>Set when the client was logged in with a store directory.</summary>
    internal EncryptionEngine? Encryption { get; set; }

    /// <summary>
    /// How ready the client is for encrypted rooms. Encryption is on when the client was logged in with a store directory.
    /// Clients that follow the spec only share keys with devices their owner cross-signed and only show encrypted
    /// messages from them, so this device works in encrypted rooms once it is <see cref="EncryptionState.Ready"/>.
    /// </summary>
    public EncryptionState EncryptionState => Encryption?.State ?? EncryptionState.Off;

    /// <summary>
    /// Creates new cross-signing keys for the account, protected by a new recovery key, and signs this device with them.
    /// Replaces the keys the account had before. Returns the recovery key: save it somewhere safe, apart from the store
    /// directory. Every new device of the account needs it once, and nobody can recover it for you.
    /// </summary>
    /// <remarks>
    /// Other devices of the account lose their cross-signature: they need the new recovery key once to be signed again.
    /// Other users' clients see that the account's identity changed. Servers ask for the password before they replace
    /// existing keys; servers that want a confirmation in the browser instead are not supported.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Encryption is not enabled, or the server refused the new keys.</exception>
    public Task<string> SetUpCrossSigningAsync(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return Encryption?.SetUpCrossSigningAsync(password)
           ?? throw new InvalidOperationException("Cross-signing needs encryption; log in with a store directory.");
    }

    private ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    private ConcurrentDictionary<string, User> _users = new(StringComparer.Ordinal);

    /// <summary>
    /// The rooms the current user is in. Filled by the first sync before <see cref="LoginAsync"/> and
    /// <see cref="ResumeAsync"/> return; it loads no further data per room. Changes come as <see cref="RoomChanged"/>.
    /// </summary>
    public IReadOnlyList<Room> Rooms => _rooms.Values.Where(room => room.IsJoined).ToList();

    /// <summary>Whether the first sync of this session was handled. It fills in the rooms without reporting changes.</summary>
    private bool _hasSynced;
    /// <summary>
    /// False while handling a sync without a sync position. It fills the rooms' message cache, but its messages and
    /// their decryption failures happened before the login and aren't reported.
    /// </summary>
    private bool _reportMessages = true;
    /// <summary>Completes with null once the first sync was handled, or with the error that ended the sync before.</summary>
    private readonly TaskCompletionSource<Exception?> _firstSyncHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Update> _updates = Channel.CreateUnbounded<Update>();

    /// <summary>Limits how many messages load their reactions from the server at once.</summary>
    internal SemaphoreSlim ReactionRequests { get; } = new(4, 4);

    /// <summary>
    /// Everything the client receives: new messages, edits, deletions, reactions, room changes, requests like invites, and problems the client keeps
    /// running through, like messages it could not decrypt. Read it continuously; the channel is unbounded and the sync
    /// never waits for the reader. It completes when the client is disposed and throws the error that ended the
    /// session, like <see cref="SessionEndedException"/> or <see cref="PasswordRequiredException"/>.
    /// Transient failures are retried with the existing session.
    /// </summary>
    public ChannelReader<Update> Updates => _updates.Reader;

    private long _lastSyncAtUnixMilliseconds;

    /// <summary>
    /// When the last sync with the server succeeded, or null before the first one.
    /// A sync is a long poll that returns at least every minute, so an older value means the connection is down.
    /// </summary>
    public DateTimeOffset? LastSyncAt => Interlocked.Read(ref _lastSyncAtUnixMilliseconds) is var value and > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(value)
        : null;

    public bool IsSyncing { get; private set;}

    // Kept to log in again after a soft logout.
    private string? _password;
    // Null for clients created for tests, which don't own their lifetime.
    private CancellationTokenSource? _lifetime;
    private Task? _syncTask;
    private int _disposed;
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
    /// Returns the session kept in <paramref name="storeDirectory"/> for the user, or null if there is none.
    /// Only reads the session file; doesn't check the session with the server.
    /// </summary>
    /// <param name="storeDirectory">The store directory given to <see cref="LoginAsync"/>.</param>
    /// <param name="userId">The user ID as the server confirmed it, see <see cref="CurrentUser"/>. Compared exactly.</param>
    public static StoredSession? GetStoredSession(string storeDirectory, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var session = SessionStore.Read(storeDirectory, userId);
        return session == null ? null : new StoredSession(session.UserId, session.DeviceId, new Uri(session.Homeserver), storeDirectory);
    }

    /// <summary>
    /// Logs in with the password and creates a new session. The task finishes after the first sync: <see cref="Rooms"/>
    /// is filled, and messages sent from then on come through <see cref="Updates"/>. Older messages, including the ones
    /// the first sync brings, are in the rooms' history. Dispose the client to disconnect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failures before the login, like a wrong homeserver or password, throw right away, since they more likely point to
    /// a wrong configuration than to an outage. Once logged in, transient failures (network errors, unavailable server,
    /// rate limits) are retried until the client is disposed.
    /// </para>
    /// <para>
    /// If the server ends the session but allows logging in again (soft logout), the client logs in again with the
    /// password and continues where it stopped. If the server deleted the device, <see cref="Updates"/> faults with
    /// <see cref="SessionEndedException"/>. Other terminal errors fault <see cref="Updates"/> as well.
    /// </para>
    /// </remarks>
    /// <param name="userId">User ID, like @user:example.org.</param>
    /// <param name="password">The account password. The client keeps it in memory to log in again after a soft logout.</param>
    /// <param name="cancellationToken">Cancels connecting. Once connected, the client runs until it is disposed.</param>
    /// <param name="deviceId">
    /// Identifies this client to the server. With a store directory it is optional: the server creates one, and the
    /// store keeps it. Without a store directory it is required; keep it across restarts, or every start creates
    /// another device on the account.
    /// </param>
    /// <param name="deviceDisplayName">The name other clients show for a new device. Defaults to "RSMatrix".</param>
    /// <param name="storeDirectory">
    /// Keeps the session (access token and sync position) and this device's encryption keys, so a restarted client
    /// can continue with <see cref="GetStoredSession"/> and <see cref="ResumeAsync"/>. The files are plain JSON and
    /// contain the access token and private keys; protect the directory like a password file. It holds one session per
    /// user, and only one client at a time can use it. A session the directory held for this user before is replaced;
    /// its device is logged out if the server still knows it. Without a store directory nothing is kept, encrypted
    /// messages are ignored and sending to encrypted rooms throws <see cref="NotSupportedException"/>.
    /// </param>
    /// <param name="homeserver">
    /// The homeserver's URL, like https://matrix.example.org. Without it, the client looks it up through the
    /// .well-known file of the user ID's domain.
    /// </param>
    /// <param name="logger">logger</param>
    /// <param name="recoveryKey">
    /// The account's recovery key, in the format other clients show it ("EsTc oGp8 ..."; spaces don't matter). Used
    /// when the account has a cross-signing identity, to sign this new device. Without it, the device stays unsigned;
    /// see <see cref="EncryptionState"/>. A key that doesn't belong to the account makes LoginAsync throw.
    /// </param>
    /// <exception cref="ArgumentException">Provided arguments not valid</exception>
    /// <exception cref="InvalidOperationException">Mostly if connection fails</exception>
    public static Task<MatrixClient> LoginAsync(string userId, string password, CancellationToken cancellationToken,
        string? deviceId = null, string? deviceDisplayName = null, string? storeDirectory = null, Uri? homeserver = null,
        ILogger? logger = null, string? recoveryKey = null)
        => LoginAsync(userId, password, cancellationToken, deviceId, deviceDisplayName, storeDirectory, homeserver, logger,
            recoveryKey, CreateHttpClient(), (delay, token) => Task.Delay(delay, token));

    /// <summary>Takes ownership of <paramref name="httpClient"/>, also when logging in fails.</summary>
    internal static Task<MatrixClient> LoginAsync(string userId, string password, CancellationToken cancellationToken,
        string? deviceId, string? deviceDisplayName, string? storeDirectory, Uri? homeserver, ILogger? logger,
        string? recoveryKey, HttpClient httpClient, Func<TimeSpan, CancellationToken, Task> retryDelayAsync)
        => StartAsync(httpClient, retryDelayAsync, logger, cancellationToken, (http, lifetime) => LoginCoreAsync(http, lifetime,
            userId, password, deviceId, deviceDisplayName, storeDirectory, homeserver, recoveryKey));

    /// <summary>
    /// Continues a stored session without logging in, at the stored sync position: messages sent while the client was
    /// offline are delivered after resuming, as many per room as the server includes in one sync. The rest are in the
    /// room's history. The task finishes after the first sync, with <see cref="Rooms"/> filled. Dispose the client to
    /// disconnect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transient failures (network errors, unavailable server, rate limits) are retried until the client is disposed.
    /// </para>
    /// <para>
    /// If the server ended the session but allows logging in again (soft logout), the client logs in again with
    /// <paramref name="password"/> and the same device, and continues where it stopped. Without a password, ResumeAsync
    /// throws <see cref="PasswordRequiredException"/>, and <see cref="Updates"/> faults with it when that happens
    /// while the client runs. If the server deleted the device, for example after a password change, the store
    /// discards the session and the device's encryption keys, and ResumeAsync throws <see cref="SessionEndedException"/>
    /// (or <see cref="Updates"/> faults with it); log in again with <see cref="LoginAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="session">The session from <see cref="GetStoredSession"/>.</param>
    /// <param name="cancellationToken">Cancels connecting. Once connected, the client runs until it is disposed.</param>
    /// <param name="password">(optional) The account password, to log in again after a soft logout.</param>
    /// <param name="logger">logger</param>
    /// <param name="recoveryKey">
    /// The account's recovery key, to sign this device if the account has a cross-signing identity that hasn't signed
    /// it yet. See <see cref="LoginAsync"/>.
    /// </param>
    /// <exception cref="PasswordRequiredException">The server ended the session, and no password was given.</exception>
    /// <exception cref="SessionEndedException">The server deleted the device.</exception>
    /// <exception cref="InvalidOperationException">Mostly if connection fails</exception>
    public static Task<MatrixClient> ResumeAsync(StoredSession session, CancellationToken cancellationToken,
        string? password = null, ILogger? logger = null, string? recoveryKey = null)
        => ResumeAsync(session, cancellationToken, password, logger, recoveryKey, CreateHttpClient(),
            (delay, token) => Task.Delay(delay, token));

    /// <summary>Takes ownership of <paramref name="httpClient"/>, also when resuming fails.</summary>
    internal static Task<MatrixClient> ResumeAsync(StoredSession session, CancellationToken cancellationToken,
        string? password, ILogger? logger, string? recoveryKey, HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> retryDelayAsync)
        => StartAsync(httpClient, retryDelayAsync, logger, cancellationToken, (http, lifetime) => ResumeCoreAsync(http, lifetime,
            session, password, recoveryKey));

    private static HttpClient CreateHttpClient()
        => new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            MaxResponseContentBufferSize = 1024 * 1024 * 2 // 2 MB
        };

    private static async Task<MatrixClient> StartAsync(HttpClient httpClient, Func<TimeSpan, CancellationToken, Task> retryDelayAsync,
        ILogger? logger, CancellationToken cancellationToken,
        Func<HttpClientParameters, CancellationTokenSource, Task<MatrixClient>> startAsync)
    {
        logger ??= NullLogger<MatrixClient>.Instance;
        var lifetime = new CancellationTokenSource();
        try
        {
            MatrixDiagnostics.FromEnvironment.LogSetting(logger);
            HttpClientParameters http = new(httpClient, "", null, logger, lifetime.Token)
            {
                RetryDelayAsync = retryDelayAsync,
                Diagnostics = MatrixDiagnostics.FromEnvironment,
            };
            // The caller's token only cancels connecting. Afterwards, the client runs until it is disposed.
            using (cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), lifetime))
            {
                return await startAsync(http, lifetime).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (ex is not PasswordRequiredException and not SessionEndedException and not OperationCanceledException and not ArgumentException)
                logger.LogError(ex, "Connecting to Matrix failed.");
            httpClient.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    private static async Task<MatrixClient> LoginCoreAsync(HttpClientParameters http, CancellationTokenSource lifetime,
        string userId, string password, string? deviceId, string? deviceDisplayName, string? storeDirectory, Uri? homeserver,
        string? recoveryKey)
    {
        var requestedUserId = ParseUserId(userId);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (deviceId != null && string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        if (deviceId == null && storeDirectory == null)
            throw new ArgumentException("Without a store directory, the device ID is required. Otherwise every start creates another device on the account.", nameof(deviceId));
        if (homeserver != null && (!homeserver.IsAbsoluteUri || (homeserver.Scheme != Uri.UriSchemeHttps && homeserver.Scheme != Uri.UriSchemeHttp)))
            throw new ArgumentException("The homeserver must be an absolute http or https URL, like https://matrix.example.org.", nameof(homeserver));

        // Fail on a busy store before creating a session on the server.
        var store = storeDirectory != null ? SessionStore.Open(storeDirectory, requestedUserId.Full) : null;
        try
        {
            // Without a session that worked before, a failure here is more likely a wrong configuration
            // than an outage, so it is reported right away instead of retried.
            http.BaseUri = homeserver != null ? ToBaseUri(homeserver) : await DiscoverHomeserverAsync(http, requestedUserId).ConfigureAwait(false);
            // Logged out first, so a login with the same device ID creates the device anew instead of taking over its keys.
            if (store?.Session is { } previous)
                await TryLogOutAsync(http with { BaseUri = previous.Homeserver, BearerToken = previous.AccessToken }, previous.DeviceId).ConfigureAwait(false);

            var login = await PasswordLoginAsync(http, requestedUserId, password, deviceId, deviceDisplayName ?? "RSMatrix", retry: false).ConfigureAwait(false);
            if (!UserId.TryParse(login.UserId, out var confirmedUserId) || confirmedUserId == null)
                throw new InvalidOperationException($"The server confirmed the login with an invalid user ID: {login.UserId}");

            // The server may spell the user ID differently than it was given, for example with other letter cases.
            if (store != null && confirmedUserId.Full != store.UserId)
            {
                store.Dispose();
                store = null;
                try
                {
                    store = SessionStore.Open(storeDirectory!, confirmedUserId.Full);
                }
                catch
                {
                    await TryLogOutAsync(http, login.DeviceId).ConfigureAwait(false);
                    throw;
                }
                if (store.Session is { } previousOfConfirmed && previousOfConfirmed.DeviceId != login.DeviceId)
                    await TryLogOutAsync(http with { BaseUri = previousOfConfirmed.Homeserver, BearerToken = previousOfConfirmed.AccessToken },
                        previousOfConfirmed.DeviceId).ConfigureAwait(false);
            }

            EncryptionStore? encryptionStore = null;
            if (store != null)
            {
                encryptionStore = EncryptionStore.Create(store.Directory, confirmedUserId.Full, login.DeviceId);
                store.Save(CreateSessionData(http, confirmedUserId, login.DeviceId, syncToken: null));
            }
            return await CreateClientAsync(http, lifetime, confirmedUserId, login.DeviceId, password, recoveryKey, store,
                encryptionStore, syncToken: null).ConfigureAwait(false);
        }
        catch
        {
            store?.Dispose();
            throw;
        }
    }

    private static async Task<MatrixClient> ResumeCoreAsync(HttpClientParameters http, CancellationTokenSource lifetime,
        StoredSession session, string? password, string? recoveryKey)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(password))
            password = null;
        var userId = ParseUserId(session.UserId);
        var store = SessionStore.Open(session.StoreDirectory, session.UserId);
        try
        {
            var stored = store.Session;
            if (stored == null || stored.DeviceId != session.DeviceId)
                throw new InvalidOperationException($"The store no longer holds the session of {session.UserId} with device {session.DeviceId}.");
            var deviceId = stored.DeviceId;
            http.BaseUri = stored.Homeserver;
            http.BearerToken = stored.AccessToken;
            http.Logger.LogInformation("Resuming the stored session at {Url}", http.BaseUri);
            try
            {
                var whoAmI = await MatrixRetry.ExecuteAsync(http, "Checking the stored session",
                    () => MatrixHelper.GetWhoAmIAsync(http)).ConfigureAwait(false);
                if (whoAmI.UserId != userId.Full || (whoAmI.DeviceId != null && whoAmI.DeviceId != deviceId))
                    throw new InvalidOperationException(
                        $"The stored session belongs to {whoAmI.UserId} with device {whoAmI.DeviceId}, not {userId.Full} with device {deviceId}.");
            }
            catch (MatrixResponseException ex) when (ex.ErrorCode == "M_UNKNOWN_TOKEN")
            {
                http.BearerToken = null;
                ThrowIfSessionEnded(ex, store);
                if (password == null)
                    throw new PasswordRequiredException("The server ended the stored session, so logging in again needs the password. Resume with the password.", ex);
                http.Logger.LogWarning("The server ended the stored session but allows logging in again (soft logout). Logging in with the same device.");
                await PasswordLoginAsync(http, userId, password, deviceId, deviceDisplayName: null, retry: true).ConfigureAwait(false);
            }
            store.Save(CreateSessionData(http, userId, deviceId, stored.SyncToken));
            var encryptionStore = EncryptionStore.Open(store.Directory, userId.Full, deviceId);
            return await CreateClientAsync(http, lifetime, userId, deviceId, password, recoveryKey, store, encryptionStore,
                stored.SyncToken).ConfigureAwait(false);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private static MatrixId ParseUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID cannot be null or empty.", nameof(userId));
        if (!UserId.TryParse(userId, out var parsedUserId) || parsedUserId == null)
            throw new ArgumentException("The user id seems invalid, it should look like: '@user:example.org'.", nameof(userId));
        return parsedUserId;
    }

    /// <summary>
    /// Throws <see cref="SessionEndedException"/> if the server deleted the device, after discarding the stored session
    /// and encryption keys, as the spec requires.
    /// </summary>
    private static void ThrowIfSessionEnded(MatrixResponseException error, SessionStore? store)
    {
        if (error.SoftLogout)
            return;
        store?.Delete();
        throw new SessionEndedException("The server logged out this device and deleted it. Log in again, which creates a new device.", error);
    }

    private static async Task<MatrixClient> CreateClientAsync(HttpClientParameters http, CancellationTokenSource lifetime,
        MatrixId userId, string deviceId, string? password, string? recoveryKey, SessionStore? sessionStore,
        EncryptionStore? encryptionStore, string? syncToken)
    {
        var logger = http.Logger;
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
            _lifetime = lifetime,
        };
        if (encryptionStore != null)
            client.Encryption = await EncryptionEngine.StartAsync(encryptionStore, client, recoveryKey).ConfigureAwait(false);
        await MatrixRetry.ExecuteAsync(http, "Initializing Matrix", client.InitAsync).ConfigureAwait(false);
        client.IsSyncing = true;
        client._syncTask = client.SyncAsync();
        if (await client._firstSyncHandled.Task.ConfigureAwait(false) is { } failure)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Throw(failure);
        }
        return client;
    }

    private static string ToBaseUri(Uri homeserver) => homeserver.AbsoluteUri.TrimEnd('/');

    /// <summary>
    /// Stops syncing and waits until the client has stopped: running requests are cancelled, the session and keys
    /// are saved, <see cref="Updates"/> completes, and the store directory is released.
    /// Doesn't log out; <see cref="ResumeAsync"/> continues the stored session.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        HttpClientParameters.IsDisposed = true;
        _lifetime?.Cancel();
        if (_syncTask != null)
            await _syncTask.ConfigureAwait(false);
        HttpClientParameters.HttpClient.Dispose();
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
    private static async Task<LoginResponse> PasswordLoginAsync(HttpClientParameters http, MatrixId userId, string password,
        string? deviceId, string? deviceDisplayName, bool retry)
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
            () => MatrixHelper.PasswordLoginAsync(http, userId.Full, password, deviceId, deviceDisplayName)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(loginResponse.AccessToken) || string.IsNullOrEmpty(loginResponse.DeviceId))
        {
            logger.LogError("Failed to login to server.");
            throw new InvalidOperationException("Failed to login to server.");
        }
        http.BearerToken = loginResponse.AccessToken;
        logger.LogInformation("Logged in as {UserId} with device {DeviceId}", loginResponse.UserId, loginResponse.DeviceId);
        return loginResponse;
    }

    /// <summary>Logs out a device we no longer use, so devices don't pile up on the account. Failures are only logged.</summary>
    private static async Task TryLogOutAsync(HttpClientParameters http, string deviceId)
    {
        try
        {
            await MatrixHelper.LogoutAsync(http).ConfigureAwait(false);
            http.Logger.LogInformation("Logged out device {DeviceId}.", deviceId);
        }
        catch (Exception ex) when (!http.CancellationToken.IsCancellationRequested)
        {
            http.Logger.LogWarning(ex, "Could not log out device {DeviceId}.", deviceId);
        }
    }

    private static SessionData CreateSessionData(HttpClientParameters http, MatrixId userId, string deviceId, string? syncToken) => new()
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
                    NotTypes = new() { "m.room.join_rules", "m.room.guest_access", "m.room.history_visibility", "m.room.power_levels", "im.vector.modular.widgets" },
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
                catch (MatrixResponseException ex) when (ex.ErrorCode == "M_UNKNOWN_TOKEN" && !loggedInSinceLastSync)
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
                _reportMessages = request.Since != null;
                await HandleSyncResponseAsync(response).ConfigureAwait(false);
                _reportMessages = true;
                request.Since = response.NextBatch;
                request.FullState = false;
                SaveSession(request.Since);
                Interlocked.Exchange(ref _lastSyncAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _firstSyncHandled.TrySetResult(null);
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
            _sessionStore?.Dispose();
            _firstSyncHandled.TrySetResult(failure ?? new OperationCanceledException(HttpClientParameters.CancellationToken));
            if (!_updates.Writer.TryComplete(failure))
                Logger.LogError("Sync ended, but failed to complete the update channel.");
        }
    }

    private async Task LoginAgainAsync(MatrixResponseException error, SyncParameters request)
    {
        HttpClientParameters.BearerToken = null;
        ThrowIfSessionEnded(error, _sessionStore);
        if (_password == null)
            throw new PasswordRequiredException("The server ended the session, so logging in again needs the password. Resume with the password.", error);
        Logger.LogWarning("The server ended the session but allows logging in again (soft logout). Logging in with the same device.");
        await PasswordLoginAsync(HttpClientParameters, CurrentUser, _password, _deviceId!, deviceDisplayName: null, retry: true).ConfigureAwait(false);
        SaveSession(request.Since);
    }

    private void SaveSession(string? syncToken)
    {
        if (_sessionStore == null)
            return;
        var stored = _sessionStore.Session;
        if (stored != null && stored.SyncToken == syncToken && stored.AccessToken == HttpClientParameters.BearerToken)
            return;
        _sessionStore.Save(CreateSessionData(HttpClientParameters, CurrentUser, _deviceId!, syncToken));
    }

    private async Task HandleSyncResponseAsync(SyncResponse response)
    {
        if (response == null)
            return;

        var cancellationToken = HttpClientParameters.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        // Room keys arrive as to-device messages and must be stored before decrypting the timeline.
        if (Encryption != null)
            await Encryption.HandleSyncAsync(response).ConfigureAwait(false);

        List<Update> updates = [];
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
                var room = GetOrAddRoom(roomId);
                var before = RoomSnapshot.Of(room);
                var membersChanged = false;
                List<Update> messageUpdates = [];
                if (roomEvents.Summary != null)
                    membersChanged |= HandleRoomSummaryReceived(room, roomEvents.Summary);
                if (roomEvents.AccountData?.Events is { } roomAccountData)
                    HandleAccountDataReceived(roomId, roomAccountData);
                if (roomEvents.Ephemeral?.Events is { } ephemeral)
                    HandleEphemeralReceived(roomId, ephemeral);
                if (roomEvents.State?.Events is { } state)
                    membersChanged |= HandleStateReceived(roomId, state, fromTimeline: false);
                if (roomEvents.Timeline != null)
                    membersChanged |= HandleTimelineReceived(roomId, roomEvents.Timeline, messageUpdates);
                var newlyJoined = !room.IsJoined;
                if (newlyJoined)
                {
                    lock (room)
                        room.IsJoined = true;
                }
                // The first sync fills in every room, and a room we just joined is new as a whole. Reporting each
                // detail of that as a change would only be noise.
                if (_hasSynced && newlyJoined)
                    updates.Add(new RoomChanged(room, RoomChange.Members));
                else if (_hasSynced)
                    AddRoomChanges(room, before, membersChanged, updates);
                if (_reportMessages)
                    updates.AddRange(messageUpdates);
            }
        }

        if (response.Rooms?.Leaves is { } leaves)
        {
            foreach (var roomIdString in leaves.Keys)
            {
                // Leaving also covers invites that were declined or withdrawn. Those rooms were never joined.
                if (!_rooms.TryGetValue(roomIdString, out var room) || !room.IsJoined)
                    continue;
                lock (room)
                    room.IsJoined = false;
                if (_hasSynced)
                    updates.Add(new RoomChanged(room, RoomChange.Members));
            }
        }

        // Stripped invite state can already tell us that a room is encrypted, so a send right after joining stays encrypted.
        if (response.Rooms?.Invites is { } invites)
        {
            foreach (var pair in invites)
            {
                HandleInviteEncryptionState(pair.Key, pair.Value);
                if (ReadInvite(pair.Key, pair.Value) is { } invite)
                    updates.Add((Request)invite);
            }
        }

        // Malformed events are skipped at their own boundary. Delivery failures or
        // unexpected processing errors must propagate, not advance the sync token.
        foreach (var update in updates)
            await _updates.Writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
        _hasSynced = true;

        if (response.Rooms?.Knocks is { Count: > 0 } knocks)
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.SyncSection, "rooms.knock", knocks.First().Value);
    }

    /// <summary>Takes the fields the server sent; it leaves out the ones that didn't change. Returns whether the member counts changed.</summary>
    private static bool HandleRoomSummaryReceived(Room room, RoomSummary summary)
    {
        lock (room)
        {
            var countsChanged = false;
            if (summary.Heroes is { } heroes)
                room.Heroes = [.. heroes.Where(id => UserId.TryParse(id, out _))];
            if (summary.JoinedMemberCount is { } joined && joined != room.JoinedMemberCount)
            {
                room.JoinedMemberCount = joined;
                countsChanged = true;
            }
            if (summary.InvitedMemberCount is { } invited && invited != room.InvitedMemberCount)
            {
                room.InvitedMemberCount = invited;
                countsChanged = true;
            }
            return countsChanged;
        }
    }

    private static void AddRoomChanges(Room room, RoomSnapshot before, bool membersChanged, List<Update> updates)
    {
        var after = RoomSnapshot.Of(room);
        if (after.DisplayName != before.DisplayName)
            updates.Add(new RoomChanged(room, RoomChange.Name));
        if (after.Topic != before.Topic)
            updates.Add(new RoomChanged(room, RoomChange.Topic));
        if (after.AvatarUrl != before.AvatarUrl)
            updates.Add(new RoomChanged(room, RoomChange.Avatar));
        if (membersChanged)
            updates.Add(new RoomChanged(room, RoomChange.Members));
        if (after.IsEncrypted != before.IsEncrypted)
            updates.Add(new RoomChanged(room, RoomChange.Encryption));
    }

    private sealed record RoomSnapshot(string DisplayName, string? Topic, string? AvatarUrl, bool IsEncrypted)
    {
        public static RoomSnapshot Of(Room room) => new(room.DisplayName, room.Topic, room.AvatarUrl, room.IsEncrypted);
    }

    private void HandlePresenceReceived(List<BasicEvent> events)
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
                Diagnostics.ReportUnhandled(Logger, UnhandledKind.PresenceEvent, ev.Type, ev);
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

            PresenceContent? parsedPresence;
            try
            {
                parsedPresence = ParseContent<PresenceContent>(ev.Content.Value);
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
                    user.ProfileName = parsedPresence.DisplayName;

                if(parsedPresence.CurrentlyActive != null)
                    user.CurrentlyActive = parsedPresence.CurrentlyActive;

                user.Presence = parsedPresence.Presence;

                if(parsedPresence.StatusMsg != null)
                    user.StatusMessage = parsedPresence.StatusMsg;
            }
        }
    }

    
    private void HandleEphemeralReceived(MatrixId roomId, List<BasicEvent> events)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        foreach (var e in events.Where(ev => ev != null))
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.EphemeralEvent, e.Type, e);
    }

    
    /// <summary>Returns whether the room's members changed.</summary>
    /// <param name="fromTimeline">
    /// Whether the events come from the timeline, where each one is a change. The state section also brings members
    /// the client just didn't know yet.
    /// </param>
    private bool HandleStateReceived(MatrixId roomId, List<RoomEvent> events, bool fromTimeline)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        var room = GetOrAddRoom(roomId);
        var membersChanged = false;

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
                membersChanged |= ApplyStateEvent(room, e, fromTimeline);
            }
            catch (JsonException ex)
            {
                // One malformed/redacted event must not discard the rest of the sync.
                Logger.LogWarning(ex, "Could not interpret state event {Type} in room {RoomId}.", e.Type, roomId.Full);
            }
        }
        return membersChanged;
    }

    /// <summary>Returns whether the room's members changed.</summary>
    private bool ApplyStateEvent(Room room, RoomEvent e, bool fromTimeline)
    {
        switch (e.Type)
        {
            case "m.room.member":
                return HandleRoomMemberEvent(room, e, fromTimeline);
            case "m.room.name" when e.StateKey == "":
                var name = ParseContent<RoomNameContent>(e.Content!.Value)?.Name;
                lock (room)
                    room.Name = string.IsNullOrWhiteSpace(name) ? null : name;
                break;
            case "m.room.topic" when e.StateKey == "":
                var topic = ParseContent<RoomTopicContent>(e.Content!.Value)?.Topic;
                lock (room)
                    room.Topic = string.IsNullOrWhiteSpace(topic) ? null : topic;
                break;
            case "m.room.avatar" when e.StateKey == "":
                var url = ParseContent<RoomAvatarContent>(e.Content!.Value)?.Url;
                lock (room)
                    room.AvatarUrl = string.IsNullOrWhiteSpace(url) ? null : url;
                break;
            case "m.room.canonical_alias" when e.StateKey == "":
                var parsed = ParseContent<CanonicalAliasContent>(e.Content!.Value);
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
            default:
                // Room.StateEvents keeps the raw content of every state event.
                Diagnostics.ReportUnhandled(Logger, UnhandledKind.StateEvent, e.Type, e);
                break;
        }
        return false;
    }

    /// <summary>Returns whether this changed the room's members, see <see cref="HandleStateReceived"/>.</summary>
    private bool HandleRoomMemberEvent(Room room, RoomEvent e, bool fromTimeline)
    {
        var roomMember = ParseContent<RoomMemberContent>((JsonElement)e.Content!);
        if (roomMember == null || !Enum.IsDefined(roomMember.Membership))
        {
            Logger.LogWarning("Received invalid m.room.member content in room {RoomId}.", room.RoomId.Full);
            return false;
        }
        var userIdStr = e.StateKey ?? e.Sender;
        if (!UserId.TryParse(userIdStr, out MatrixId? userId) || userId == null)
        {
            Logger.LogWarning("Received m.room.member event with invalid user ID: {UserId} in room {RoomId}.", userIdStr, room.RoomId.Full);
            return false;
        }
        var wasKnown = room.Users.TryGetValue(userId.Full, out var known) && known.Membership != null;
        var user = GetOrAddUser(userId);
        RoomUser? roomUser = GetOrAddUser(user, room);
        Encryption?.OnMembershipChanged(room.RoomId.Full, roomMember.Membership);

        var changed = roomUser.RoomName != roomMember.DisplayName || roomUser.AvatarUrl != roomMember.AvatarUrl ||
            roomUser.Membership != roomMember.Membership;
        if (changed)
        {
            lock (roomUser)
            {
                roomUser.RoomName = roomMember.DisplayName;
                roomUser.AvatarUrl = roomMember.AvatarUrl;
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
        return fromTimeline || (wasKnown && changed);
    }

    /// <summary>
    /// Adds members loaded with <see cref="Room.GetMembersAsync"/>. Members the client already knows stay as they are:
    /// the sync keeps them current, and the loaded list may be older.
    /// </summary>
    internal void AddLoadedMembers(Room room, List<RoomEvent> events)
    {
        foreach (var e in events)
        {
            if (e?.Type != "m.room.member" || e.StateKey == null || e.Content is not { ValueKind: JsonValueKind.Object } content ||
                !UserId.TryParse(e.StateKey, out MatrixId? userId) || userId == null)
                continue;
            if (room.Users.TryGetValue(userId.Full, out var known) && known.Membership != null)
                continue;
            RoomMemberContent? member;
            try
            {
                member = ParseContent<RoomMemberContent>(content);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Could not interpret loaded member {UserId} in room {RoomId}.", userId.Full, room.RoomId.Full);
                continue;
            }
            if (member == null || !Enum.IsDefined(member.Membership))
                continue;
            var roomUser = GetOrAddUser(GetOrAddUser(userId), room);
            lock (roomUser)
            {
                roomUser.RoomName = member.DisplayName;
                roomUser.AvatarUrl = member.AvatarUrl;
                roomUser.Membership = member.Membership;
            }
        }
    }

    private void HandleInviteEncryptionState(string roomIdString, SyncInvitedRoom? invitedRoom)
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

    /// <summary>Reads the invite preview. Returns null when it doesn't hold the invite itself, so nobody can be named as inviter.</summary>
    private RoomInvite? ReadInvite(string roomIdString, SyncInvitedRoom? invitedRoom)
    {
        if (!RoomId.TryParse(roomIdString, out var roomId) || roomId == null)
        {
            Logger.LogWarning("Received invite for room with invalid ID: {RoomId}", roomIdString);
            return null;
        }

        var events = invitedRoom?.InviteState?.Events?.Where(e => e != null).ToList() ?? [];
        T? Parse<T>(StrippedStateEvent e) where T : class
        {
            try
            {
                return ParseContent<T>(e.Content);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Could not interpret {Type} in the invite to room {RoomId}.", e.Type, roomId.Full);
                return null;
            }
        }
        StrippedStateEvent? RoomState(string type) => events.FirstOrDefault(e => e.Type == type && e.StateKey == "");

        var ownMember = events.FirstOrDefault(e => e.Type == "m.room.member" && e.StateKey == CurrentUser.Full);
        if (ownMember == null || !UserId.TryParse(ownMember.Sender, out var inviterId) || inviterId == null)
        {
            Logger.LogWarning("The invite to room {RoomId} doesn't say who sent it. Ignoring it.", roomId.Full);
            return null;
        }
        var inviter = new RoomUser(GetOrAddUser(inviterId));
        if (events.FirstOrDefault(e => e.Type == "m.room.member" && e.StateKey == inviterId.Full) is { } inviterMember &&
            Parse<RoomMemberContent>(inviterMember) is { } inviterContent)
        {
            inviter.RoomName = string.IsNullOrWhiteSpace(inviterContent.DisplayName) ? null : inviterContent.DisplayName;
            inviter.AvatarUrl = inviterContent.AvatarUrl;
        }

        foreach (var e in events)
        {
            if (e.Type is not ("m.room.member" or "m.room.encryption" or "m.room.name" or "m.room.topic" or "m.room.avatar" or "m.room.canonical_alias"))
                Diagnostics.ReportUnhandled(Logger, UnhandledKind.InviteStateEvent, e.Type, e);
        }

        var name = RoomState("m.room.name") is { } nameEvent ? Parse<RoomNameContent>(nameEvent)?.Name : null;
        var topic = RoomState("m.room.topic") is { } topicEvent ? Parse<RoomTopicContent>(topicEvent)?.Topic : null;
        var avatarUrl = RoomState("m.room.avatar") is { } avatarEvent ? Parse<RoomAvatarContent>(avatarEvent)?.Url : null;
        MatrixId? alias = null;
        if (RoomState("m.room.canonical_alias") is { } aliasEvent)
            RoomAlias.TryParse(Parse<CanonicalAliasContent>(aliasEvent)?.Alias, out alias);

        var invite = new RoomInvite(this, roomId, inviter,
            roomName: string.IsNullOrWhiteSpace(name) ? null : name,
            topic: string.IsNullOrWhiteSpace(topic) ? null : topic,
            avatarUrl: string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl,
            canonicalAlias: alias,
            isDirect: Parse<RoomMemberContent>(ownMember)?.IsDirect == true,
            isEncrypted: RoomState("m.room.encryption") != null);
        Logger.LogInformation("Received {Invite}", invite);
        return invite;
    }

    /// <summary>Returns whether the room's members changed.</summary>
    private bool HandleTimelineReceived(MatrixId roomId, SyncTimeline timeline, List<Update> updates)
    {
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        var room = GetOrAddRoom(roomId);
        var membersChanged = false;

        lock (room.TimelineLock)
        {
            var cache = room.Cache;
            // A limited timeline skipped events since the previous sync. The cache holds no gaps, so it starts over,
            // and a history walk crosses the gap.
            if (timeline.Limited == true)
                cache.Clear();
            var wasEmpty = cache.IsEmpty;
            foreach (var e in timeline.Events ?? [])
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
                        membersChanged |= HandleStateReceived(roomId, [e], fromTimeline: true);
                    else if (!cache.Contains(e.EventId) && ReadEntry(room, e, live: true) is { } entry)
                        AddLiveEntry(room, entry, updates);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
                {
                    Logger.LogWarning(ex, "Could not interpret timeline event {Type} in room {RoomId}.", e.Type, roomId.Full);
                }
            }
            // History walks continue from the first events the sync delivers for a room.
            if (wasEmpty)
                cache.OlderToken = timeline.PrevBatch;
            cache.Trim();
        }
        return membersChanged;
    }

    /// <summary>Adds an event from the sync to the room's cache and reports what it changed.</summary>
    private void AddLiveEntry(Room room, TimelineEntry entry, List<Update> updates)
    {
        var cache = room.Cache;
        var redacted = entry is RedactionEntry redaction ? cache.Find(redaction.TargetId) : null;
        var targetId = entry switch
        {
            EditEntry edit => edit.TargetId,
            ReactionEntry reaction => reaction.TargetId,
            RedactionEntry => redacted switch
            {
                MessageEntry message => message.EventId,
                EditEntry edit => edit.TargetId,
                ReactionEntry reaction => reaction.TargetId,
                _ => null,
            },
            _ => null,
        };
        var target = targetId == null ? null : cache.FindMessage(targetId);
        var before = target == null ? null : cache.Build(target);
        cache.Append(entry);

        if (entry is MessageEntry newMessage)
        {
            // A message deleted before we saw it never comes out as new. History shows where it was.
            if (newMessage.IsDeleted)
                return;
            updates.Add(new NewMessage(cache.Build(newMessage)));
            AddThreadChange(room, newMessage, updates);
            return;
        }
        if (targetId == null)
        {
            Logger.LogDebug("Event {EventId} in room {RoomId} deletes an event the client never saw.", entry.EventId, room.RoomId.Full);
            return;
        }
        if (target == null || before == null)
        {
            // Edits and reactions of edits or reactions aren't changes to a message.
            if (cache.Find(targetId) != null)
                return;
            // Without the message, the client can't tell whether the change took effect. It reports it anyway.
            MessageChange? unknownChange = entry switch
            {
                EditEntry => MessageChange.Edited,
                ReactionEntry => MessageChange.Reactions,
                RedactionEntry when redacted is EditEntry => MessageChange.Edited,
                RedactionEntry when redacted is ReactionEntry => MessageChange.Reactions,
                _ => null,
            };
            if (unknownChange != null)
                updates.Add(new MessageChanged(new MessageReference(room, targetId), null, unknownChange.Value));
            return;
        }

        if (entry is EditEntry editEntry && !TimelineCache.IsValidEdit(target, editEntry))
        {
            Logger.LogDebug("Ignoring invalid edit {EventId} of message {TargetId} in room {RoomId}.", editEntry.EventId, target.EventId, room.RoomId.Full);
            return;
        }
        var after = cache.Build(target);
        MessageChange? change = entry switch
        {
            EditEntry or RedactionEntry when redacted is not MessageEntry && after.CurrentEditId != before.CurrentEditId => MessageChange.Edited,
            ReactionEntry reaction when !before.HasReaction(reaction.Key, reaction.User) && after.HasReaction(reaction.Key, reaction.User) => MessageChange.Reactions,
            RedactionEntry when redacted is MessageEntry && !before.IsDeleted => MessageChange.Deleted,
            RedactionEntry when redacted is ReactionEntry reaction && before.HasReaction(reaction.Key, reaction.User) && !after.HasReaction(reaction.Key, reaction.User) => MessageChange.Reactions,
            _ => null,
        };
        if (change == null)
            return;
        updates.Add(new MessageChanged(new MessageReference(room, target.EventId), after, change.Value));
        if (change != MessageChange.Reactions)
            AddThreadChange(room, target, updates);
    }

    /// <summary>Reports the thread root as changed when one of its replies was added, edited or deleted.</summary>
    private static void AddThreadChange(Room room, MessageEntry reply, List<Update> updates)
    {
        if (reply.ThreadRootId is not { } rootId)
            return;
        var root = room.Cache.FindMessage(rootId);
        updates.Add(new MessageChanged(new MessageReference(room, rootId), root == null ? null : room.Cache.Build(root), MessageChange.Thread));
    }

    /// <summary>Reads a page of history, newest event first, and returns the entries for a cache in the same order.</summary>
    internal List<TimelineEntry> ReadHistoryEntries(Room room, List<RoomEvent> events)
    {
        List<TimelineEntry> entries = [];
        foreach (var e in events)
        {
            try
            {
                if (e.StateKey == null && ReadEntry(room, e, live: false) is { } entry)
                    entries.Add(entry);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
            {
                Logger.LogWarning(ex, "Could not interpret event {Type} in the history of room {RoomId}.", e.Type, room.RoomId.Full);
            }
        }
        return entries;
    }

    /// <summary>
    /// Reads a room event that isn't state, decrypting it if needed. Returns null for events the cache doesn't keep.
    /// <paramref name="live"/> is true for events from the sync and false for history.
    /// </summary>
    private TimelineEntry? ReadEntry(Room room, RoomEvent e, bool live)
    {
        var isEncrypted = e.Type == "m.room.encrypted";
        if (e.Unsigned?.RedactedBecause != null)
        {
            if (isEncrypted || e.Type == "m.room.message")
                return CreateTombstone(room, e, isEncrypted);
            Logger.LogDebug("Skipping event {EventId} in room {RoomId}: it was deleted.", e.EventId, room.RoomId.Full);
            return null;
        }

        if (isEncrypted)
        {
            if (DecryptRoomEvent(room, e) is not { } decrypted)
                return null;
            e = decrypted;
        }

        if (e.Content is not { ValueKind: JsonValueKind.Object } content)
        {
            Logger.LogWarning("Received event {Type} without object content in room {RoomId}.", e.Type, room.RoomId.Full);
            return null;
        }

        switch (e.Type)
        {
            case "m.room.message":
                // Decrypted content stays out of the logs.
                var messageContent = ParseContent<RoomMessageContent>(content, withExamples: !isEncrypted);
                if (messageContent == null)
                    return null;
                return messageContent.RelatesTo?.RelType == "m.replace"
                    ? ReadEdit(room, e, messageContent, isEncrypted)
                    : ReadMessage(room, e, messageContent, isEncrypted);
            case "m.reaction":
                return ReadReaction(room, e, ParseContent<ReactionContent>(content, withExamples: !isEncrypted));
            case "m.room.redaction" when !isEncrypted:
                return ReadRedaction(room, e, ParseContent<RedactionContent>(content));
            default:
                if (live)
                    Diagnostics.ReportUnhandled(Logger, UnhandledKind.TimelineEvent, e.Type, isEncrypted ? null : e);
                return null;
        }
    }

    private MessageEntry? ReadMessage(Room room, RoomEvent e, RoomMessageContent content, bool isEncrypted)
    {
        if (GetRoomUser(room, e.Sender) is not { } sender)
            return null;
        var kind = ToMessageKind(content.MsgType);
        if (kind == MessageKind.Unknown)
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.MsgType, content.MsgType, isEncrypted ? null : e);
        ReportUnhandledRelation(content.RelatesTo, isEncrypted ? null : e);

        var relatesTo = content.RelatesTo;
        var threadRootId = relatesTo?.RelType == "m.thread" ? relatesTo.EventId : null;
        // A thread message carries a reply to the previous thread message as a fallback for clients without threads.
        var replyToId = threadRootId != null && relatesTo!.IsFallingBack == true ? null : relatesTo?.InReplyTo?.EventId;

        var message = new MessageEntry(e.EventId, sender, DateTimeOffset.FromUnixTimeMilliseconds(e.OriginServerTs),
            sender.User.UserId.Equals(CurrentUser), isEncrypted, replyToId, threadRootId,
            ToMessageContent(room, kind, content.Body, content.Format, content.FormattedBody, content.Mentions),
            IsDeleted: false, BundledEdit: null, BundledThread: null);

        if (e.Unsigned?.Relations?.Replace is { } bundledEdit && ReadBundledEdit(room, bundledEdit) is { } edit && edit.TargetId == message.EventId
            && TimelineCache.IsValidEdit(message, edit))
        {
            message = message with { BundledEdit = edit.Edit };
        }
        if (e.Unsigned?.Relations?.Thread is { } thread)
        {
            var latestReply = thread.LatestEvent is { } latest ? ReadEntry(room, latest, live: false) as MessageEntry : null;
            message = message with { BundledThread = new BundledThreadSummary(thread.Count, latestReply, thread.CurrentUserParticipated) };
        }
        return message;
    }

    /// <summary>A deleted message in history. Its content and relations are gone.</summary>
    private MessageEntry? CreateTombstone(Room room, RoomEvent e, bool isEncrypted)
    {
        if (GetRoomUser(room, e.Sender) is not { } sender)
            return null;
        return new MessageEntry(e.EventId, sender, DateTimeOffset.FromUnixTimeMilliseconds(e.OriginServerTs),
            sender.User.UserId.Equals(CurrentUser), isEncrypted, null, null, new MessageContent(MessageKind.Unknown, "", null, []),
            IsDeleted: true, BundledEdit: null, BundledThread: null);
    }

    private EditEntry? ReadBundledEdit(Room room, RoomEvent edit)
    {
        var isEncrypted = edit.Type == "m.room.encrypted";
        if (isEncrypted)
        {
            if (DecryptRoomEvent(room, edit) is not { } decrypted)
                return null;
            edit = decrypted;
        }
        if (edit.Type != "m.room.message" || edit.Content is not { ValueKind: JsonValueKind.Object } content ||
            ParseContent<RoomMessageContent>(content, withExamples: !isEncrypted) is not { RelatesTo.RelType: "m.replace" } editContent)
        {
            return null;
        }
        return ReadEdit(room, edit, editContent, isEncrypted);
    }

    private EditEntry? ReadEdit(Room room, RoomEvent e, RoomMessageContent content, bool isEncrypted)
    {
        if (content.RelatesTo?.EventId is not { } targetId || content.NewContent is not { } newContent)
        {
            Logger.LogDebug("Ignoring edit {EventId} in room {RoomId}: it has no target or no new content.", e.EventId, room.RoomId.Full);
            return null;
        }
        return new EditEntry(e.EventId, targetId, e.Sender, isEncrypted, new MessageEdit(e.EventId, DateTimeOffset.FromUnixTimeMilliseconds(e.OriginServerTs),
            ToMessageContent(room, ToMessageKind(newContent.MsgType), newContent.Body, newContent.Format, newContent.FormattedBody, newContent.Mentions)));
    }

    private ReactionEntry? ReadReaction(Room room, RoomEvent e, ReactionContent? content)
    {
        if (content?.RelatesTo is not { RelType: "m.annotation" } relation)
        {
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.Relation, content?.RelatesTo.RelType ?? "m.reaction without relation", e);
            return null;
        }
        return GetRoomUser(room, e.Sender) is { } user ? new ReactionEntry(e.EventId, relation.EventId, relation.Key, user) : null;
    }

    private RedactionEntry? ReadRedaction(Room room, RoomEvent e, RedactionContent? content)
    {
        if ((content?.Redacts ?? e.Redacts) is not { } redactedId)
        {
            Logger.LogWarning("Received redaction {EventId} without a target in room {RoomId}.", e.EventId, room.RoomId.Full);
            return null;
        }
        return GetRoomUser(room, e.Sender) is { } redactedBy ? new RedactionEntry(e.EventId, redactedId, redactedBy) : null;
    }

    private MessageContent ToMessageContent(Room room, MessageKind kind, string body, string? format, string? formattedBody, Mentions? mentions)
    {
        var html = format == "org.matrix.custom.html" ? formattedBody : null;
        var mentionedUsers = mentions?.UserIds?
            .Select(id => GetRoomUser(room, id))
            .OfType<RoomUser>()
            .ToList() ?? [];
        return new MessageContent(kind, body, html, mentionedUsers);
    }

    private static MessageKind ToMessageKind(string msgType) => msgType switch
    {
        "m.text" => MessageKind.Text,
        "m.notice" => MessageKind.Notice,
        "m.emote" => MessageKind.Emote,
        "m.image" => MessageKind.Image,
        "m.file" => MessageKind.File,
        "m.audio" => MessageKind.Audio,
        "m.video" => MessageKind.Video,
        _ => MessageKind.Unknown,
    };

    private RoomUser? GetRoomUser(Room room, string? userId)
    {
        if (!UserId.TryParse(userId, out var parsed) || parsed == null)
        {
            Logger.LogWarning("Received an event with invalid user ID {UserId} in room {RoomId}.", userId, room.RoomId.Full);
            return null;
        }
        return GetOrAddUser(GetOrAddUser(parsed), room);
    }

    /// <summary>Decrypts an m.room.encrypted event, or reports why it can't and returns null.</summary>
    internal RoomEvent? DecryptRoomEvent(Room room, RoomEvent e)
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

        // A reaction we can't decrypt isn't worth an issue.
        if (_reportMessages && !IsAnnotation(e))
        {
            ReportIssue(ClientIssueKind.DecryptionFailed,
                $"Could not decrypt event {e.EventId} from {e.Sender} in room {room.RoomId.Full}: {failure}.",
                room, e.Sender, e.EventId);
        }
        return null;
    }

    private static bool IsAnnotation(RoomEvent e)
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
        _updates.Writer.TryWrite(new ClientIssue(kind, message, room, user, eventId, exception));
    }

    private void HandleRoomEncryptionEvent(Room room, JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.Object })
        {
            Logger.LogWarning("Received encryption state without object content in room {RoomId}.", room.RoomId.Full);
            return;
        }

        var encryptionEvent = ParseContent<RoomEncryptionContent>(content.Value);
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

    private void HandleAccountDataReceived(MatrixId? roomId, List<BasicEvent> accountData)
    {
        var kind = roomId == null ? UnhandledKind.AccountData : UnhandledKind.RoomAccountData;
        foreach (var ev in accountData)
        {
            if (ev != null)
                Diagnostics.ReportUnhandled(Logger, kind, ev.Type, ev);
        }
    }

    // Edits, reactions, threads and replies are handled; other relation types of messages are not.
    private void ReportUnhandledRelation(RelatesTo? relatesTo, RoomEvent? example)
    {
        if (relatesTo?.RelType is { } relType && relType != "m.thread")
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.Relation, relType, example);
    }

    private MatrixDiagnostics Diagnostics => HttpClientParameters.Diagnostics;

    private T? ParseContent<T>(JsonElement content, bool withExamples = true)
    {
        var parsed = JsonSerializer.Deserialize<T>(content);
        Diagnostics.ReportUnknownFields<T>(Logger, content, withExamples);
        return parsed;
    }
}

