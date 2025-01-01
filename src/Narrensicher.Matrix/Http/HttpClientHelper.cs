using Narrensicher.Matrix.Models;
using Microsoft.Extensions.Logging;
using System.Net.Mime;
using System.Text.Json;
using System.Web;

namespace Narrensicher.Matrix.Http;
public record HttpClientParameters
{
    public IHttpClientFactory Factory { get; init; }
    public string BaseUri { get; set; }
    public string? BearerToken { get; set; }
    public ILogger Logger { get; init; }
    public LeakyBucketRateLimiter? RateLimiter { get; set; }

    public CancellationToken CancellationToken { get; init; }

    public HttpClientParameters(IHttpClientFactory factory, string baseUri, string? bearerToken, ILogger logger, CancellationToken cancellationToken)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
        BearerToken = bearerToken;
    }
}


public static class HttpClientHelper
{
    public static async Task<TResponse> SendAsync<TResponse>(HttpClientParameters parameters, string path, HttpMethod? method = null, HttpContent? content = null, bool ignoreRateLimit = false)
    {
        using var response = await SendRequestAsync(parameters, path, method, content, ignoreRateLimit).ConfigureAwait(false);
        using var contentStream = await response.Content.ReadAsStreamAsync(parameters.CancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<TResponse>(contentStream, cancellationToken: parameters.CancellationToken).ConfigureAwait(false);
        if (result != null)
            return result;

        parameters.Logger.LogError("Failed to deserialize response from {Url}", response.RequestMessage.RequestUri);
        throw new HttpRequestException($"Failed to deserialize response from {response.RequestMessage.RequestUri}.");
    }

    public static async Task SendAsync(HttpClientParameters parameters, string path, HttpMethod? method = null, HttpContent? content = null, bool ignoreRateLimit = false)
    {
        await SendRequestAsync(parameters, path, method, content, ignoreRateLimit).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpClientParameters parameters, string path, HttpMethod? method, HttpContent? content, bool ignoreRateLimit)
    {
        //Rate limiter. System.Threading.RateLimiting considered, but we don't want timers and disposable objects.
        var rateLimiter = parameters.RateLimiter;
        if (!ignoreRateLimit && rateLimiter != null && !rateLimiter.Leak())
        {
            parameters.Logger.LogWarning("Rate limit exceeded. Request to {Path} will be delayed.", path);
            throw new HttpRequestException("Rate limit exceeded.");
        }

        var cancellationToken = parameters.CancellationToken;
        ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));
        ArgumentException.ThrowIfNullOrEmpty(path, nameof(path));
        parameters.Logger.LogInformation("Sending request to {Path}", path);
        using var client = parameters.Factory.CreateClient("MatrixClient");
        client.MaxResponseContentBufferSize = 1024 * 1024 * 2; // 2 MB;

        var request = new HttpRequestMessage(method ?? HttpMethod.Get, string.Concat(parameters.BaseUri, path));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (!string.IsNullOrWhiteSpace(parameters.BearerToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", parameters.BearerToken);

        if (content != null)
            request.Content = content;

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        parameters.Logger.LogInformation("Request to {Path} completed with status code {Status}", path, response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            if (response.Content != null && response.Content.Headers.ContentLength > 0)
            {
                using var errorStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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

        return response;
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

public static class HttpParameterHelper
{
    public static string AppendParameters(string path, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ArgumentNullException.ThrowIfNull(path, nameof(path));
        if (parameters == null)
            return path;

        //var formattedParameters = string.Join("&", parameters.Select(kvp => $"{HttpUtility.UrlEncode(kvp.Key)}={HttpUtility.UrlEncode(kvp.Value)}"));
        var formattedParameters = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        if (string.IsNullOrWhiteSpace(formattedParameters))
            return path;

        return $"{path}?{formattedParameters}";
    }
}

