using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.IntegrationTests;

/// <summary>Real HTTP fixture, restricted to the explicitly selected local server.</summary>
internal sealed class LocalHomeserver : IAsyncDisposable
{
    internal const string Password = "rsmatrix-test-password";
    /// <summary>Matches registration_shared_secret in Homeserver/homeserver.yaml.</summary>
    private const string RegistrationSharedSecret = "rsmatrix-local-test-registration-only";
    private readonly CancellationTokenSource _lifetime;
    private readonly ConcurrentBag<string> _tokens = [];
    private readonly List<MatrixClient> _clients = [];
    /// <summary>The recovery key of each user's cross-signing identity, by user localpart.</summary>
    internal ConcurrentDictionary<string, string> RecoveryKeys { get; } = new();
    private readonly LocalTransport _transport;
    internal Uri BaseUri { get; }
    internal CancellationToken CancellationToken => _lifetime.Token;

    internal LocalHomeserver()
    {
        BaseUri = GetBaseUri(Environment.GetEnvironmentVariable("RSMATRIX_INTEGRATION_URL"));
        _lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _transport = new LocalTransport(BaseUri, _tokens,
            new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    }

    internal static Uri GetBaseUri(string? address)
    {
        // Solution/IDE test runs must not need a homeserver. The integration Make
        // targets explicitly opt in; an invalid configured URL is still an error.
        TUnit.Core.Skip.When(address is null,
            "Live homeserver tests are opt-in. Run make test-integration-disposable, " +
            "or set RSMATRIX_INTEGRATION_URL for an existing local test server.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || !uri.IsLoopback || uri.AbsolutePath != "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidOperationException(
                "Live tests require RSMATRIX_INTEGRATION_URL=http://127.0.0.1:8008 (loopback HTTP only). " +
                "Run make matrix-up && make test-integration, or make test-integration-disposable.");
        }
        return uri;
    }

    private HttpClient CreateHttpClient() => new(_transport, disposeHandler: false)
    {
        // Synapse long-polls /sync for up to 60 seconds. The fixture's lifetime
        // token is the overall deadline; do not accidentally time out each poll.
        Timeout = TimeSpan.FromSeconds(75)
    };

    internal HttpClientParameters Parameters(string? token = null, CancellationToken? cancellationToken = null)
        => new(CreateHttpClient(), BaseUri.AbsoluteUri.TrimEnd('/'), token, NullLogger.Instance,
            cancellationToken ?? CancellationToken);

    internal async Task<HttpClientParameters> LoginAsync(string user)
    {
        var parameters = Parameters();
        var login = await MatrixHelper.PasswordLoginAsync(parameters, $"@{user}:localhost",
            Password, $"RSMATRIX-{Guid.NewGuid():N}", null);
        parameters.BearerToken = login.AccessToken
            ?? throw new InvalidOperationException("Synapse returned no access token.");
        return parameters;
    }

    /// <summary>
    /// Registers a new user with a unique name starting with <paramref name="prefix"/> and returns its localpart.
    /// Encryption tests use new users, so cross-signing identities from earlier runs don't get in the way.
    /// </summary>
    internal async Task<string> RegisterUserAsync(string prefix)
    {
        var localpart = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
        var parameters = Parameters();
        var nonce = (await RequestAsync(parameters, "/_synapse/admin/v1/register", HttpMethod.Get)).GetProperty("nonce").GetString()!;
        var mac = HMACSHA1.HashData(Encoding.UTF8.GetBytes(RegistrationSharedSecret),
            Encoding.UTF8.GetBytes($"{nonce}\0{localpart}\0{Password}\0notadmin"));
        var registered = await RequestAsync(parameters, "/_synapse/admin/v1/register", HttpMethod.Post,
            new { nonce, username = localpart, password = Password, admin = false, mac = Convert.ToHexStringLower(mac) });
        if (registered.TryGetProperty("access_token", out var token) && token.GetString() is { } value)
            _tokens.Add(value);
        return localpart;
    }

    /// <summary>Logs in a client without a store, which the fixture disposes at the end.</summary>
    internal Task<MatrixClient> LoginClientAsync(string user)
        => LoginClientAsync(user, null, deviceId: $"RSMATRIX-{Guid.NewGuid():N}");

    /// <summary>
    /// Logs in a client, which the fixture disposes at the end. With a store and <paramref name="crossSign"/>, the device
    /// gets signed: with the user's key from <see cref="RecoveryKeys"/>, or by setting up an identity for a user without one.
    /// </summary>
    internal async Task<MatrixClient> LoginClientAsync(string user, string? storeDirectory, string? deviceId = null,
        bool crossSign = true, string? recoveryKey = null, OptionalUpdates optionalUpdates = OptionalUpdates.All)
    {
        if (crossSign)
            recoveryKey ??= RecoveryKeys.GetValueOrDefault(user);
        var client = await MatrixClient.LoginAsync($"@{user}:localhost", Password, CancellationToken, deviceId, null,
            storeDirectory, BaseUri, CreateLogger(user, deviceId ?? "new"), recoveryKey, CreateHttpClient(),
            (delay, token) => Task.Delay(delay, token), optionalUpdates);
        return await TrackAsync(user, client, crossSign);
    }

    /// <summary>Resumes the user's session from the store. Signs the device like <see cref="LoginClientAsync(string, string?, string?, bool, string?)"/>.</summary>
    internal async Task<MatrixClient> ResumeClientAsync(string user, string storeDirectory, bool crossSign = true,
        string? recoveryKey = null, string? password = null)
    {
        if (crossSign)
            recoveryKey ??= RecoveryKeys.GetValueOrDefault(user);
        var session = MatrixClient.GetStoredSession(storeDirectory, $"@{user}:localhost")
            ?? throw new InvalidOperationException($"{storeDirectory} holds no session of {user}.");
        var client = await MatrixClient.ResumeAsync(session, CancellationToken, password,
            CreateLogger(user, session.DeviceId), recoveryKey, CreateHttpClient(), (delay, token) => Task.Delay(delay, token));
        return await TrackAsync(user, client, crossSign);
    }

    private static ILogger? CreateLogger(string user, string deviceId)
        => TestLogger.Create($"{user}/{deviceId[..Math.Min(8, deviceId.Length)]}");

    private async Task<MatrixClient> TrackAsync(string user, MatrixClient client, bool crossSign)
    {
        _clients.Add(client);
        if (crossSign && client.EncryptionState == EncryptionState.NoIdentity)
            RecoveryKeys[user] = await client.SetUpCrossSigningAsync(Password);
        return client;
    }

    internal static Task<JsonElement> RequestAsync(HttpClientParameters parameters, string path,
        HttpMethod method, object? body = null)
        => HttpClientHelper.SendAsync<JsonElement>(parameters, path, method,
            body is null ? null : JsonContent.Create(body));

    public async ValueTask DisposeAsync()
    {
        // Disposing stops sync but intentionally does not log out a real client.
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Sync cleanup: {ex.GetType().Name}: {ex.Message}");
            }
        }
        await _lifetime.CancelAsync();
        // Cleanup uses a new token, independent of the cancelled sync lifetime.
        foreach (var token in _tokens.Distinct())
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await HttpClientHelper.SendAsync(Parameters(token, cleanup.Token),
                    "/_matrix/client/v3/logout", HttpMethod.Post, JsonContent.Create(new { }));
            }
            catch (MatrixResponseException ex) when (ex.ErrorCode == "M_UNKNOWN_TOKEN")
            {
                // The logout test has already revoked this token.
            }
            catch (Exception ex)
            {
                // Do not mask an assertion failure with a secondary cleanup failure.
                Console.Error.WriteLine($"Logout cleanup: {ex.GetType().Name}: {ex.Message}");
            }
        }
        _transport.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Writes the client's log to stderr when RSMATRIX_TEST_LOG names a minimum level, like Warning or Debug.</summary>
    private sealed class TestLogger(string name, LogLevel minimum) : ILogger
    {
        public static ILogger? Create(string name)
            => Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("RSMATRIX_TEST_LOG"), out var minimum)
                ? new TestLogger(name, minimum) : null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                Console.Error.WriteLine($"[{logLevel}] {name}: {formatter(state, exception)}{(exception == null ? "" : $" ({exception.Message})")}");
        }
    }

    internal sealed class LocalTransport(Uri baseUri, ConcurrentBag<string> tokens,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri ||
                uri.GetLeftPart(UriPartial.Authority) != baseUri.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("Integration tests refuse non-fixture HTTP destinations.");

            var response = await base.SendAsync(request, cancellationToken);
            // Retain issued tokens only in memory so even partial Connect failures
            // can log out their sessions. At this point HttpClient has NOT buffered
            // the response yet: reading its raw stream would consume it before the
            // outer HttpClient and library get it. Buffer first, within the same
            // 2 MiB limit as HttpClientHelper, and parse a copy of the buffered text.
            if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/_matrix/client/v3/login" &&
                response.IsSuccessStatusCode)
            {
                try
                {
                    await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellationToken);
                    var login = JsonSerializer.Deserialize<JsonElement>(
                        await response.Content.ReadAsStringAsync(cancellationToken));
                    if (login.TryGetProperty("access_token", out var token) && token.GetString() is { } value)
                        tokens.Add(value);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            return response;
        }
    }
}
