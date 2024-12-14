using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Threading.Tasks;

namespace MatrixTextClient
{
    public record ConnectionParameters(string UserId, string Password, string DeviceId);

    public sealed class MatrixService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;

        public MatrixService(IHttpClientFactory httpClientFactory, ILogger<MatrixService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }



        public async Task<MatrixConnection> ConnectAsync(ConnectionParameters parameters)
        {
            if (!UserId.TryParse(parameters.UserId, out var userId) || userId == null)
            {
                _logger.LogError("The user id '{UserId}' seems invalid, it should look like : '@user:server'.", parameters.UserId);
                throw new ArgumentException("The user id seems invalid, it should be a well formed user id.", nameof(parameters.UserId));
            }

            var baseUri = $"https://{userId.Server}";
            _logger.LogInformation("Connecting to {Url}", baseUri);

            if (!Uri.IsWellFormedUriString(baseUri, UriKind.Absolute))
            {
                _logger.LogError("The server address '{Url}' seems invalid, it should look like : 'https://matrix.org'.", baseUri);
                throw new ArgumentException("The sever address seems invalid, it should be a well formed Uri.", nameof(parameters.UserId));
            }

            using var jsonResponse = await HttpClientHelper.GetJsonAsync(_httpClientFactory, baseUri, "/.well-known/matrix/client", _logger);
            _logger.LogInformation("Received JSON response: {JsonResponse}", jsonResponse.RootElement.GetRawText());

            var matrixConnection = new MatrixConnection
            {
                Content = jsonResponse.ToString() ?? "",
            };

            return matrixConnection;
        }
    }
}
