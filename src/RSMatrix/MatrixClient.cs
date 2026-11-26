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
    private Channel<ReceivedTextMessage> MessageChannel { get; set; }

    /// <summary>
    /// The channel to receive messages from the server.
    /// The channel is unbounded, so it will not block the sender.
    /// The channel completes when the client is disposed and faults on a terminal sync error.
    /// Transient failures are retried with the existing session.
    /// </summary>
    public ChannelReader<ReceivedTextMessage> Messages => MessageChannel.Reader;

    internal const int IssueChannelCapacity = 100;
    private readonly Channel<ClientIssue> _issueChannel = Channel.CreateBounded<ClientIssue>(
        new BoundedChannelOptions(IssueChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Problems the client ran into while it keeps running, like messages it could not decrypt.
    /// Reading is optional. The channel keeps the latest 100 issues and drops older ones.
    /// It completes when the client stops.
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

    /// <summary>
    /// When true, the client will automatically join rooms when invited.
    /// Default is false.
    /// </summary>
    public bool AutoJoinOnInvite { get; set; } = false;

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
    /// Logs in with the password and creates a new session. The task finishes when the client is connected and ready to
    /// receive messages through <see cref="Messages"/>. Dispose the client to disconnect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failures before the login, like a wrong homeserver or password, throw right away, since they more likely point to
    /// a wrong configuration than to an outage. Once logged in, transient failures (network errors, unavailable server,
    /// rate limits) are retried until the client is disposed.
    /// </para>
    /// <para>
    /// If the server ends the session but allows logging in again (soft logout), the client logs in again with the
    /// password and continues where it stopped. If the server deleted the device, <see cref="Messages"/> faults with
    /// <see cref="SessionEndedException"/>. Other terminal errors fault <see cref="Messages"/> as well.
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
    /// offline are delivered after resuming. The task finishes when the client is connected and ready to receive
    /// messages through <see cref="Messages"/>. Dispose the client to disconnect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transient failures (network errors, unavailable server, rate limits) are retried until the client is disposed.
    /// </para>
    /// <para>
    /// If the server ended the session but allows logging in again (soft logout), the client logs in again with
    /// <paramref name="password"/> and the same device, and continues where it stopped. Without a password, ResumeAsync
    /// throws <see cref="PasswordRequiredException"/>, and <see cref="Messages"/> faults with it when that happens
    /// while the client runs. If the server deleted the device, for example after a password change, the store
    /// discards the session and the device's encryption keys, and ResumeAsync throws <see cref="SessionEndedException"/>
    /// (or <see cref="Messages"/> faults with it); log in again with <see cref="LoginAsync"/>.
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
        return client;
    }

    private static string ToBaseUri(Uri homeserver) => homeserver.AbsoluteUri.TrimEnd('/');

    /// <summary>
    /// Stops syncing and waits until the client has stopped: running requests are cancelled, the session and keys
    /// are saved, <see cref="Messages"/> and <see cref="Issues"/> complete, and the store directory is released.
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
            _sessionStore?.Dispose();
            if (!MessageChannel.Writer.TryComplete(failure))
                Logger.LogError("Sync ended, but failed to complete message channel.");
            _issueChannel.Writer.TryComplete();
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

    private LeakyBucket _receiptRateLimiter = new LeakyBucket(1, 30);

    private async Task HandleSyncResponseAsync(SyncResponse response)
    {
        if (response == null)
            return;

        var cancellationToken = HttpClientParameters.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

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

        if (response.Rooms?.Leaves is { Count: > 0 } leaves)
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.SyncSection, "rooms.leave", leaves.First().Value);
        if (response.Rooms?.Knocks is { Count: > 0 } knocks)
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.SyncSection, "rooms.knock", knocks.First().Value);

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

            PresenceEvent? parsedPresence;
            try
            {
                parsedPresence = ParseContent<PresenceEvent>(ev.Content.Value);
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
        foreach (var e in events.Where(ev => ev != null))
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.EphemeralEvent, e.Type, e);
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
                var nameEvent = ParseContent<RoomNameEvent>(e.Content!.Value);
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
                var parsed = ParseContent<CanonicalAliasEvent>(e.Content!.Value);
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
    }

    private void HandleRoomMemberEvent(Room room, ClientEventWithoutRoomID e)
    {
        var roomMember = ParseContent<RoomMemberEvent>((JsonElement)e.Content!);
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
                if (ev != null && ev.Type is not "m.room.member" and not "m.room.encryption")
                    Diagnostics.ReportUnhandled(Logger, UnhandledKind.InviteStateEvent, ev.Type, ev);
                if (ev?.Type == "m.room.member" && ev.StateKey == CurrentUser.Full)
                {
                    try
                    {
                        var memberEvent = ParseContent<RoomMemberEvent>(ev.Content);
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
                    Diagnostics.ReportUnhandled(Logger, UnhandledKind.TimelineEvent, e.Type, e);
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
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.TimelineEvent, decrypted.Type);
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

        var encryptionEvent = ParseContent<RoomEncryptionEvent>(content.Value);
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

        // Decrypted content stays out of the logs.
        var messageEvent = ParseContent<RoomMessageEvent>((JsonElement)e.Content, withExamples: !isEncrypted);
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
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.MsgType, messageEvent.MsgType, isEncrypted ? null : e);
            return;
        }
        ReportUnhandledRelation(messageEvent.RelatesTo, isEncrypted ? null : e);

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
        var kind = roomId == null ? UnhandledKind.AccountData : UnhandledKind.RoomAccountData;
        foreach (var ev in accountData)
        {
            if (ev != null)
                Diagnostics.ReportUnhandled(Logger, kind, ev.Type, ev);
        }
    }

    // Threads are handled; replies and every other relation type are not delivered yet.
    private void ReportUnhandledRelation(RoomMessageRelatesTo? relatesTo, ClientEventWithoutRoomID? example)
    {
        if (relatesTo == null)
            return;
        if (relatesTo.RelType is { } relType && relType != "m.thread")
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.Relation, relType, example);
        // A thread message carries the reply as a fallback for clients without threads.
        if (relatesTo.InReplyTo != null && !(relatesTo.RelType == "m.thread" && relatesTo.IsFallingBack == true))
            Diagnostics.ReportUnhandled(Logger, UnhandledKind.Relation, "m.in_reply_to", example);
    }

    private MatrixDiagnostics Diagnostics => HttpClientParameters.Diagnostics;

    private T? ParseContent<T>(JsonElement content, bool withExamples = true)
    {
        var parsed = JsonSerializer.Deserialize<T>(content);
        Diagnostics.ReportUnknownFields<T>(Logger, content, withExamples);
        return parsed;
    }
}

