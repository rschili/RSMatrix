using System.Net.Http;
using System.Text.Json;

namespace MatrixTextClient
{
    public class MatrixService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly HttpClient _httpClient; // no need to dispose because the factory/host will handle it

        public MatrixService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            _httpClient = _httpClientFactory.CreateClient("MatrixServiceHttpClient");
        }

        public async Task<MatrixConnection> ConnectAsync(string url)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var contentStream =
            await response.Content.ReadAsStreamAsync();

            return await JsonSerializer.DeserializeAsync<MatrixConnection>(contentStream);
        }

        public DateTimeOffset ConvertMillisecondsToDateTimeOffset(long milliseconds)
        {
            var dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return dateTimeOffset;
        }
    }

    public class MatrixConnection
    {
        public string Content { get; set; }
    }


}
