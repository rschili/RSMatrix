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
    public record HttpClientParameters
    {
        public IHttpClientFactory Factory { get; init; }
        public string BaseUri { get; set; }
        public string? BearerToken { get; set; }
        public ILogger Logger { get; init; }

        public HttpClientParameters(IHttpClientFactory factory, string baseUri, string? bearerToken, ILogger logger)
        {
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            BearerToken = bearerToken;
        }
    }


    public static class HttpClientHelper
    {
        public static async Task<TResponse> SendAsync<TResponse>(HttpClientParameters parameters, string path, HttpMethod? method = null, HttpContent? content = null)
        {
            ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(path));
            using var client = parameters.Factory.CreateClient(Constants.HTTP_CLIENT_NAME);
            client.MaxResponseContentBufferSize = 1024 * 1024 * 2; // 2 MB;
            
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, string.Concat(parameters.BaseUri, path));
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
            if (!string.IsNullOrWhiteSpace(parameters.BearerToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", parameters.BearerToken);

            if(content != null)
                request.Content = content;

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (!response.IsSuccessStatusCode && response.Content != null && response.Content.Headers.ContentLength > 0)
                {
                    using var errorStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var error = JsonSerializer.Deserialize<MatrixErrorResponse>(errorStream);
                    if (error != null)
                    {
                        parameters.Logger.LogError("{Path} failed with error: {ErrorCode}, {ErrorMessage}", path, error.ErrorCode, error.ErrorMessage);
                        throw new MatrixResponseException(error.ErrorCode, error.ErrorMessage);
                    }
                }

                parameters.Logger.LogError("{Path} failed with status code {Status}, no further details provided.", path, response.StatusCode);
                throw new HttpRequestException($"{path} failed with status code {response.StatusCode.ToString()}.");
            }

            if (response.Content == null || response.Content.Headers.ContentLength == 0)
            {
                parameters.Logger.LogError("Response content is empty for {Url}", request.RequestUri);
                throw new HttpRequestException($"Response content is empty for {request.RequestUri}");
            }

            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TResponse>(contentStream).ConfigureAwait(false);
            if (result != null)
                return result;

            parameters.Logger.LogError("Failed to deserialize response from {Url}", request.RequestUri);
            throw new HttpRequestException($"Failed to deserialize response from {request.RequestUri}.");
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
