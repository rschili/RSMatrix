using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;

namespace MatrixTextClient
{
    public static class HttpClientHelper
    {
        public static async Task<JsonDocument> GetJsonAsync(IHttpClientFactory factory, string baseUri, string path, ILogger logger)
        {
            using var client = factory.CreateClient(Constants.HTTP_CLIENT_NAME);
            client.MaxResponseContentBufferSize = 1024 * 1024 * 2; // 2 MB;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, string.Concat(baseUri, path));
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

                using var response = await client.SendAsync(request);
                var status = response.StatusCode;
                if (status != HttpStatusCode.OK)
                {
                    logger.LogError("Initial connect failed. Expected status 200 for {Url}, but got: {StatusCode}", baseUri, status);
                    throw new HttpRequestException($"Failed to connect to {baseUri}, status code: {status}");
                }

                logger.LogInformation("Response for path {Path} has media type {MediaType}", path, response.Content.Headers.ContentType?.MediaType);
                /*if (response.Content.Headers.ContentType?.MediaType?.Equals(MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase) ?? false)
                {
                    logger.LogError("Unexpected content type. Expected 'application/json' but got: {ContentType}", response.Content.Headers.ContentType?.MediaType ?? "undefined");
                    throw new HttpRequestException($"Unexpected content type: {response.Content.Headers.ContentType?.MediaType ?? "undefined"}");
                }*/

                using var contentStream = await response.Content.ReadAsStreamAsync();
                var jsonDocument = await JsonDocument.ParseAsync(contentStream);

                return jsonDocument;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to query path {Path} from {Url}", path, baseUri);
                throw new Exception("Failed to query server.", ex);
            }
        }
    }
}
