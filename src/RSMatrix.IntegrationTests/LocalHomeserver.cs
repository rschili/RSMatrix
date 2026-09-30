using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Http;

namespace RSMatrix.IntegrationTests;

/// <summary>Real HTTP fixture, restricted to the explicitly selected local server.</summary>
internal sealed class LocalHomeserver : IHttpClientFactory, IAsyncDisposable
{
    internal const string Password = "rsmatrix-test-password";
    private readonly CancellationTokenSource _lifetime;
    private readonly ConcurrentBag<string> _tokens = [];
    private readonly List<MatrixTextClient> _clients = [];
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

    public HttpClient CreateClient(string name) => new(_transport, disposeHandler: false)
    {
        // Synapse long-polls /sync for up to 60 seconds. The fixture's lifetime
        // token is the overall deadline; do not accidentally time out each poll.
        Timeout = TimeSpan.FromSeconds(75)
    };

    internal HttpClientParameters Parameters(string? token = null, CancellationToken? cancellationToken = null)
        => new(this, BaseUri.AbsoluteUri.TrimEnd('/'), token, NullLogger.Instance,
            cancellationToken ?? CancellationToken);

    internal async Task<HttpClientParameters> LoginAsync(string user)
    {
        var parameters = Parameters();
        var login = await MatrixHelper.PasswordLoginAsync(parameters, $"@{user}:localhost",
            Password, $"RSMATRIX-{Guid.NewGuid():N}");
        parameters.BearerToken = login.AccessToken
            ?? throw new InvalidOperationException("Synapse returned no access token.");
        return parameters;
    }

    internal async Task<MatrixTextClient> ConnectAsync(string user)
    {
        var client = await MatrixTextClient.ConnectAsync($"@{user}:localhost", Password,
            $"RSMATRIX-{Guid.NewGuid():N}", this, CancellationToken);
        _clients.Add(client);
        return client;
    }

    internal static Task<JsonElement> RequestAsync(HttpClientParameters parameters, string path,
        HttpMethod method, object? body = null)
        => HttpClientHelper.SendAsync<JsonElement>(parameters, path, method,
            body is null ? null : JsonContent.Create(body));

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        // Cancellation stops sync but intentionally does not log out a real client.
        // Drain buffered events so ChannelReader.Completion can actually complete.
        foreach (var client in _clients)
        {
            try
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await foreach (var unused in client.Messages.ReadAllAsync(shutdown.Token)) { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Sync cleanup: {ex.GetType().Name}: {ex.Message}");
            }
        }
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

    internal sealed class LocalTransport(Uri baseUri, ConcurrentBag<string> tokens,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // v1.4 has no explicit homeserver-URL overload and begins discovery at
            // https://<user-domain>. Redirect ONLY that request to real Synapse;
            // no API responses are faked and TLS verification is never disabled.
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsoluteUri == "https://localhost/.well-known/matrix/client")
            {
                request.RequestUri = new Uri(baseUri, "/.well-known/matrix/client");
            }

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
