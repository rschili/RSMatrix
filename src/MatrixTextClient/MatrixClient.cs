using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MatrixTextClient.Requests;
using MatrixTextClient.Responses;

namespace MatrixTextClient
{
    public sealed class MatrixClient : IDisposable
    {
        public IHttpClientFactory HttpClientFactory { get; }
        public ILogger Logger { get; }
        public UserId UserId { get; }
        public string DeviceId { get; }
        public IList<SpecVersion> SupportedSpecVersions { get; }
        public static SpecVersion CurrentSpecVersion { get; } = new SpecVersion(1, 12, null, null);
        internal string BearerToken { get; }
        internal string BaseUri { get; }

        internal MatrixClient(IHttpClientFactory httpClientFactory,
            UserId userId,
            string bearerToken,
            string baseUri,
            string deviceId,
            IList<SpecVersion> supportedSpecVersions,
            ILogger? logger = null)
        {
            HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            Logger = logger ?? NullLogger<MatrixClient>.Instance;
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            DeviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            SupportedSpecVersions = supportedSpecVersions ?? throw new ArgumentNullException(nameof(supportedSpecVersions));
            BearerToken = bearerToken ?? throw new ArgumentNullException(nameof(bearerToken));
            BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        }

        public static async Task<MatrixClient> ConnectAsync(string userId, string password, string deviceId, IHttpClientFactory httpClientFactory, ILogger? logger = null)
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

            var baseUri = $"https://{parsedUserId.Server}";
            if (!Uri.IsWellFormedUriString(baseUri, UriKind.Absolute))
            {
                logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", baseUri);
                throw new ArgumentException("The server address seems invalid, it should be a well formed Uri.", nameof(userId));
            }
            logger.LogInformation("Connecting to {Url}", baseUri);
            var wkUri = await FetchWellKnownUriAsync(baseUri, httpClientFactory, logger).ConfigureAwait(false);
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

            if (!Uri.IsWellFormedUriString(baseUri, UriKind.Absolute))
            {
                logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", baseUri);
                throw new InvalidOperationException("The resolved base uri seems invalid, it should be a well formed Uri.");
            }

            var versions = await FetchSupportedSpecVersionsAsync(baseUri, httpClientFactory, logger).ConfigureAwait(false);
            if (versions.Versions == null || versions.Versions.Count == 0)
            {
                logger.LogError("Failed to load supported spec versions from server.");
                throw new InvalidOperationException("Failed to load supported spec versions from server.");
            }

            var parsedVersions = versions.Versions.Select(v => SpecVersion.TryParse(v, out var cv) ? cv! : throw new FormatException($"Failed to parse version number {v}"))
                .OrderBy(v => v, SpecVersion.Comparer.Instance).ToList();

            if (!parsedVersions.Contains(CurrentSpecVersion))
                logger.LogWarning("The server does not support the spec version which was used to implement this library ({ExpectedVersion}), so errors may occur. Supported versions by the server are: {SupportedVersions}",
                    CurrentSpecVersion.VersionString, string.Join(',', parsedVersions));

            var authFlows = await FetchSupportedAuthFlowsAsync(baseUri, httpClientFactory, logger).ConfigureAwait(false);
            if(!authFlows.Contains(Constants.PASSWORD_LOGIN_FLOW))
            {
                logger.LogError("The server does not support password based authentication. Supported types: {Types}", string.Join(", ", authFlows));
                throw new InvalidOperationException("The server does not support password based authentication.");
            }

            var loginResponse = await PasswordLoginAsync(baseUri, httpClientFactory, userId, password, deviceId, logger).ConfigureAwait(false);

            var client = new MatrixClient(httpClientFactory, parsedUserId, loginResponse.AccessToken, baseUri, deviceId, parsedVersions, logger);
            return client;
        }

        /// <summary>
        /// Fetches the well-known URI from the server
        /// This is a low level method, it is implicitly called by ConnectAsync
        /// </summary>
        public static async Task<WellKnownUriResponse> FetchWellKnownUriAsync(string serverUri, IHttpClientFactory httpClientFactory, ILogger logger)
        {
            ArgumentException.ThrowIfNullOrEmpty(serverUri, nameof(serverUri));
            ArgumentNullException.ThrowIfNull(httpClientFactory, nameof(httpClientFactory));
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));

            return await HttpClientHelper.GetJsonAsync<WellKnownUriResponse>(httpClientFactory, serverUri, "/.well-known/matrix/client", logger).ConfigureAwait(false);
        }

        public static async Task<SpecVersionsResponse> FetchSupportedSpecVersionsAsync(string baseUri, IHttpClientFactory httpClientFactory, ILogger logger)
        {
            ArgumentException.ThrowIfNullOrEmpty(baseUri, nameof(baseUri));
            ArgumentNullException.ThrowIfNull(httpClientFactory, nameof(httpClientFactory));
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            return await HttpClientHelper.GetJsonAsync<SpecVersionsResponse>(httpClientFactory, baseUri, "/_matrix/client/versions", logger).ConfigureAwait(false);
        }

        public static async Task<List<string>> FetchSupportedAuthFlowsAsync(string baseUri, IHttpClientFactory httpClientFactory, ILogger logger)
        {
            ArgumentException.ThrowIfNullOrEmpty(baseUri, nameof(baseUri));
            ArgumentNullException.ThrowIfNull(httpClientFactory, nameof(httpClientFactory));
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            var response = await HttpClientHelper.GetJsonAsync<AuthFlowsResponse>(httpClientFactory, baseUri, "/_matrix/client/v3/login", logger, HttpStatusCode.OK, HttpMethod.Get).ConfigureAwait(false);
            if(response != null)
                return response.Flows.Select(f => f.Type).ToList();

            return new List<string>();
        }

        public static async Task<LoginResponse> PasswordLoginAsync(string baseUri, IHttpClientFactory httpClientFactory, string userId, string password, string deviceId, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory, nameof(httpClientFactory));
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentException.ThrowIfNullOrEmpty(baseUri, nameof(baseUri));
            ArgumentException.ThrowIfNullOrEmpty(userId, nameof(userId));
            ArgumentException.ThrowIfNullOrEmpty(password, nameof(password));
            ArgumentException.ThrowIfNullOrEmpty(deviceId, nameof(deviceId));
            var request = new LoginRequest
            {
                Identifier = new Identifier
                {
                    Type = "m.id.user",
                    User = userId
                },
                Password = password,
                DeviceId = deviceId,
                InitialDeviceDisplayName = "MatrixTextClient Device",
                Type = "m.login.password"
            };
            return await HttpClientHelper.PostJsonAsync<LoginResponse, LoginRequest>(httpClientFactory, baseUri, "/_matrix/client/v3/login", request, logger).ConfigureAwait(false);
        }

        public void Dispose()
        {
            //Stop Sync loop if running
        }

        public void BeginSyncLoop()
        {
            // Start Sync loop
        }
    }

}
