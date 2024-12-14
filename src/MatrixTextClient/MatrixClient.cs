using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MatrixTextClient
{
    /// <summary>
    /// Represents the connection parameters required to connect to the Matrix service.
    /// </summary>
    /// <param name="UserId">The user ID in the format '@user:server'.</param>
    /// <param name="Password">The password for the user.</param>
    /// <param name="DeviceId">The device ID for the connection.</param>
    public record ConnectionParameters(string UserId, string Password, string DeviceId);

    public sealed class MatrixClient : IDisposable
    {
        public IHttpClientFactory HttpClientFactory { get; }
        public ILogger Logger { get; }
        public UserId UserId { get; }
        public string DeviceId { get; }
        internal string BearerToken { get; }
        internal string BaseUri { get; }

        internal MatrixClient(IHttpClientFactory httpClientFactory, UserId userId, string bearerToken, string baseUri, string deviceId, ILogger? logger = null)
        {
            HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            Logger = logger ?? NullLogger<MatrixClient>.Instance;
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            DeviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
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

            using var jsonResponse = await HttpClientHelper.GetJsonAsync(httpClientFactory, baseUri, "/.well-known/matrix/client", logger);

            if (!jsonResponse.RootElement.TryGetProperty("m.homeserver", out var homeServerElement) ||
               !homeServerElement.TryGetProperty("base_url", out var baseUrlElement))
            {
                logger.LogError("The JSON response does not contain a valid 'm.homeserver' object with a 'base_url' property.");
                throw new InvalidOperationException("The JSON response does not contain a valid 'm.homeserver' object with a 'base_url' property.");
            }
            if(baseUrlElement.ValueKind != JsonValueKind.String)
            {
                logger.LogError("The 'base_url' property is not a string.");
                throw new InvalidOperationException("The 'base_url' property is not a string.");
            }
            baseUri = baseUrlElement.GetString();
            logger.LogInformation("Resolved Base URL: {BaseUrl}", baseUri);
            if (!Uri.IsWellFormedUriString(baseUri, UriKind.Absolute))
            {
                logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", baseUri);
                throw new InvalidOperationException("The resolved base uri seems invalid, it should be a well formed Uri.");
            }

            // Example response:
            //"m.homeserver": {
            //    "base_url": "https://matrix-client.matrix.org" <-- This is the base URL we want to connect to

            var client = new MatrixClient(httpClientFactory, parsedUserId, password, baseUri, deviceId, logger);
            return client;
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
