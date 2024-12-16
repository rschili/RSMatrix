using MatrixTextClient.Responses;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MatrixTextClient
{
    public static class HttpClientHelper
    {
        public static async Task<T> GetJsonAsync<T>(IHttpClientFactory factory, string baseUri, string path, ILogger logger, HttpStatusCode expectedStatusCode = HttpStatusCode.OK, HttpMethod? method = null)
        {
            using var client = factory.CreateClient(Constants.HTTP_CLIENT_NAME);
            client.MaxResponseContentBufferSize = 1024 * 1024 * 2; // 2 MB;
            
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, string.Concat(baseUri, path));
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var response = await client.SendAsync(request);
            var status = response.StatusCode;
            if (status != expectedStatusCode)
            {
                if (!response.IsSuccessStatusCode && response.Content != null && response.Content.Headers.ContentLength > 0)
                {
                    using var errorStream = await response.Content.ReadAsStreamAsync();
                    var error = JsonSerializer.Deserialize<MatrixErrorResponse>(errorStream);
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
            var result = await JsonSerializer.DeserializeAsync<T>(contentStream);
            if (result != null)
                return result;

            logger.LogError("Failed to deserialize response from {Url}{Path}", baseUri, path);
            throw new HttpRequestException($"Failed to deserialize response from {baseUri}{path}");
        }

        internal static async Task<T1> PostJsonAsync<T1, T2>(IHttpClientFactory httpClientFactory, string baseUri, string path, T2 request, ILogger logger)
        {
            using var client = httpClientFactory.CreateClient(Constants.HTTP_CLIENT_NAME);
            client.MaxResponseContentBufferSize = 1024 * 1024 * 2; // 2 MB;  

            using var response = await client.PostAsJsonAsync(string.Concat(baseUri, path), request);
            var status = response.StatusCode;
            if (status != HttpStatusCode.OK)
            {
                if (!response.IsSuccessStatusCode && response.Content != null && response.Content.Headers.ContentLength > 0)
                {
                    using var errorStream = await response.Content.ReadAsStreamAsync();
                    var error = await JsonSerializer.DeserializeAsync<MatrixErrorResponse>(errorStream);
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
            var result = await JsonSerializer.DeserializeAsync<T1>(contentStream);
            if (result != null)
                return result;

            logger.LogError("Failed to deserialize response from {Url}{Path}", baseUri, path);
            throw new HttpRequestException($"Failed to deserialize response from {baseUri}{path}");
        }
    }


    /// <summary>
    /// Represents an error response from the Matrix Server
    /// </summary>
    public class MatrixResponseException : Exception
    {
        public string ErrorCode { get; }
        public string ErrorMessage { get; }

        public MatrixResponseException(string errorCode, string errorMessage)
            : base(errorMessage)
        {
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public override string ToString()
        {
            return $"Error Code: {ErrorCode}, Error Message: {ErrorMessage}";
        }
    }

}
