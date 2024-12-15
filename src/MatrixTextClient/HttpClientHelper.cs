using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                    if (response.Content != null && response.Content.Headers.ContentLength > 0)
                    {
                        using var errorStream = await response.Content.ReadAsStreamAsync();
                        var error = JsonSerializer.Deserialize<MatrixError>(errorStream);
                        if (error != null)
                        {
                            logger.LogError("{Path} failed with error: {ErrorCode}, {ErrorMessage}", path, error.ErrorCode, error.ErrorMessage);
                            throw new MatrixResponseException(error.ErrorCode, error.ErrorMessage);
                        }
                    }

                    logger.LogError("{Path} failed with status code {Status}, no further details provided.", path, status);
                    throw new HttpRequestException($"{path} failed with status code {status.ToString()}.");
                }

                if (response.Content == null || response.Content.Headers.ContentLength == 0)
                {
                    logger.LogError("Response content is empty for {Url}", baseUri);
                    throw new HttpRequestException($"Response content is empty for {baseUri}");
                }

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
