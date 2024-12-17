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
using System.Threading;

namespace MatrixTextClient
{
    public sealed class MatrixClient
    {
        public ILogger Logger => HttpClientParameters.Logger;
        public UserId UserId { get; }
        public IList<SpecVersion> SupportedSpecVersions { get; }
        public static SpecVersion CurrentSpecVersion { get; } = new SpecVersion(1, 12, null, null);
        internal HttpClientParameters HttpClientParameters { get; private set; }
        public Capabilities ServerCapabilities { get; private set; }

        public delegate Task EventReceivedHandler(MatrixClient client, MatrixEvent matrixEvent);

        /// <summary>
        /// Gets the filter applied to the connection. At first it is null. Set it using SetFilterAsync method.
        /// </summary>
        public Filter? Filter { get; private set; }

        internal MatrixClient(HttpClientParameters parameters,
            UserId userId,
            IList<SpecVersion> supportedSpecVersions,
            Capabilities capabilities)
        {
            HttpClientParameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            SupportedSpecVersions = supportedSpecVersions ?? throw new ArgumentNullException(nameof(supportedSpecVersions));
            ServerCapabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
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
            HttpClientParameters httpClientParameters = new(httpClientFactory, baseUri, null, logger);
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

            var parsedVersions = versions.Versions.Select(v => SpecVersion.TryParse(v, out var cv) ? cv! : throw new FormatException($"Failed to parse version number {v}"))
                .OrderBy(v => v, SpecVersion.Comparer.Instance).ToList();

            if (!parsedVersions.Contains(CurrentSpecVersion))
                logger.LogWarning("The server does not support the spec version which was used to implement this library ({ExpectedVersion}), so errors may occur. Supported versions by the server are: {SupportedVersions}",
                    CurrentSpecVersion.VersionString, string.Join(',', parsedVersions));

            var authFlows = await MatrixHelper.FetchSupportedAuthFlowsAsync(httpClientParameters).ConfigureAwait(false);
            if(!authFlows.Contains(Constants.PASSWORD_LOGIN_FLOW))
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

            var serverCapabilities = await MatrixHelper.FetchCapabilitiesAsync(httpClientParameters).ConfigureAwait(false);
            var client = new MatrixClient(httpClientParameters, parsedUserId, parsedVersions, serverCapabilities.Capabilities);
            return client;
        }

        public async Task<Filter> SetFilterAsync(Filter filter)
        {
            Logger.LogInformation("Setting filter new filter");
            var filterResponse = await MatrixHelper.PostFilterAsync(HttpClientParameters, UserId, filter).ConfigureAwait(false);
            var filterId = filterResponse.FilterId;
            if(string.IsNullOrEmpty(filterId))
            {
                Logger.LogError("Failed to set filter.");
                throw new InvalidOperationException("Failed to set filter.");
            }

            var updatedFilter = await MatrixHelper.GetFilterAsync(HttpClientParameters, UserId, filterId);
            if (updatedFilter != null)
            {
                updatedFilter.FilterId = filterId;
                Filter = updatedFilter;
                return updatedFilter;
            }

            Logger.LogError("Failed to get filter after setting it.");
            throw new InvalidOperationException("Failed to get filter after setting it.");
        }

        /// <summary>
        /// Acknowledge the awesome name of this method.
        /// Starts a sync loop which receives events from the server.
        /// Task runs until the cancellationToken is cancelled.
        /// </summary>
        /// <param name="cancellationToken">required, token to stop the loop</param>
        /// <param name="handler">optional handler for incoming events, default writes events to logger</param>
        /// <returns></returns>
        public async Task SyncAsync(CancellationToken cancellationToken, EventReceivedHandler? handler = null)
        {
            if (handler == null)
                handler = DefaultEventReceivedHandler;

            var request = new SyncRequest
            {
                FullState = false,
                SetPresence = Presence.Online,
                Timeout = 60000
            };

            var response = await MatrixHelper.GetSyncAsync(HttpClientParameters, request, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            
        }

        public static Task DefaultEventReceivedHandler(MatrixClient client, MatrixEvent matrixEvent)
        {
            client.HttpClientParameters.Logger.LogInformation("Received event of type {Type}", matrixEvent.Type);
            return Task.CompletedTask;
        }
    }

}
