using RSMatrix.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Mime;
using System.Text.Json;
using RSFlowControl;

namespace RSMatrix.Http;
internal record HttpClientParameters
{
    public HttpClient HttpClient { get; init; }
    public string BaseUri { get; set; }
    public string? BearerToken { get; set; }
    public ILogger Logger { get; init; }
    public LeakyBucket? RateLimiter { get; set; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Set when the owning client is disposed; new requests throw <see cref="ObjectDisposedException"/>.</summary>
    public bool IsDisposed { get; set; }

    public MatrixDiagnostics Diagnostics { get; init; } = MatrixDiagnostics.Off;

    // Per-client test seam: production delays are cancellable, tests need not sleep.
    internal Func<TimeSpan, CancellationToken, Task> RetryDelayAsync { get; init; }
        = (delay, token) => Task.Delay(delay, token);

    // Transaction IDs must be unique per device, also across restarts. A client restarted with the same
    // device within the same second must not reuse an ID, or the server answers with the earlier result.
    private readonly string _txnPrefix = Guid.NewGuid().ToString("N")[..12];
    internal uint _txnId = 0;

    internal string NextTransactionId() => $"{_txnPrefix}-{Interlocked.Increment(ref _txnId)}";

    public HttpClientParameters(HttpClient httpClient, string baseUri, string? bearerToken, ILogger logger, CancellationToken cancellationToken)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
        BearerToken = bearerToken;
    }
}


internal static class HttpClientHelper
{
    /// <param name="cancellationToken">Cancels this request in addition to the client's own token.</param>
    public static async Task<TResponse> SendAsync<TResponse>(HttpClientParameters parameters, string path, HttpMethod? method = null, HttpContent? content = null,
        bool ignoreRateLimit = false, CancellationToken cancellationToken = default)
    {
        using var linked = Link(parameters, cancellationToken);
        cancellationToken = linked?.Token ?? parameters.CancellationToken;
        using var response = await SendRequestAsync(parameters, path, method, content, ignoreRateLimit, cancellationToken).ConfigureAwait(false);
        TResponse? result;
        var diagnostics = parameters.Diagnostics;
        if (diagnostics.Mode == DiagnosticsMode.Off)
        {
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            result = await JsonSerializer.DeserializeAsync<TResponse>(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            diagnostics.LogBody(parameters.Logger, "Response", path, body);
            result = JsonSerializer.Deserialize<TResponse>(body);
            if (result != null)
            {
                using var document = JsonDocument.Parse(body);
                diagnostics.ReportUnknownFields<TResponse>(parameters.Logger, document.RootElement);
            }
        }
        if (result != null)
            return result;

        parameters.Logger.LogError("Failed to deserialize response from {Url}", path);
        throw new HttpRequestException($"Failed to deserialize response from {path}.");
    }

    public static async Task SendAsync(HttpClientParameters parameters, string path, HttpMethod? method = null, HttpContent? content = null, bool ignoreRateLimit = false)
    {
        using var response = await SendRequestAsync(parameters, path, method, content, ignoreRateLimit, parameters.CancellationToken).ConfigureAwait(false);
        if (parameters.Diagnostics.Mode == DiagnosticsMode.Verbose)
        {
            var body = await response.Content.ReadAsByteArrayAsync(parameters.CancellationToken).ConfigureAwait(false);
            parameters.Diagnostics.LogBody(parameters.Logger, "Response", path, body);
        }
    }

    private static CancellationTokenSource? Link(HttpClientParameters parameters, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(parameters.CancellationToken, cancellationToken) : null;

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpClientParameters parameters, string relativePath, HttpMethod? method, HttpContent? content,
        bool ignoreRateLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ObjectDisposedException.ThrowIf(parameters.IsDisposed, typeof(MatrixClient));
        var fullPath = string.Concat(parameters.BaseUri, relativePath);
        var requestMethod = method ?? HttpMethod.Get;
        var debugPath = $"{requestMethod} {fullPath}";

        // Each retry needs a fresh HttpRequestMessage, but exactly the same payload and
        // URL (including txnId). Never reconstruct a message via PutMessageAsync here.
        using var originalContent = content;
        var body = content == null ? null : await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentHeaders = content?.Headers.Select(h => KeyValuePair.Create(h.Key, h.Value.ToArray())).ToArray();
        if (body != null)
            parameters.Diagnostics.LogBody(parameters.Logger, "Request", debugPath, body);
        var client = parameters.HttpClient;
        var rateLimitCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Local throttling waits instead of turning a full bucket into a failed request.
            while (!ignoreRateLimit && parameters.RateLimiter is { } limiter && !limiter.Leak())
                await MatrixRetry.DelayAsync(parameters, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            using var request = new HttpRequestMessage(requestMethod, fullPath);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
            if (!string.IsNullOrWhiteSpace(parameters.BearerToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", parameters.BearerToken);
            if (body != null)
            {
                request.Content = new ByteArrayContent(body);
                foreach (var header in contentHeaders!)
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            parameters.Logger.LogInformation("Sending request {Path}", debugPath);
            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            parameters.Logger.LogInformation("Request '{Path}' completed with status code {Status}", debugPath, response.StatusCode);
            if (response.IsSuccessStatusCode)
                return response; // caller owns/disposes successful responses, including empty ones

            MatrixResponseException error;
            using (response)
                error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

            if (error.IsRateLimited)
            {
                var delay = MatrixRetry.GetDelay(rateLimitCount, error.RetryAfter);
                rateLimitCount = Math.Min(rateLimitCount + 1, 6);
                parameters.Logger.LogWarning(
                    "{Path} was rate limited (HTTP {Status}, {ErrorCode}). Server retry delay: {RetryAfter}; retrying in {Delay}s.",
                    debugPath, (int?)error.StatusCode, error.ErrorCode, error.RetryAfter, delay.TotalSeconds);
                await MatrixRetry.DelayAsync(parameters, delay).ConfigureAwait(false);
                continue;
            }

            // Callers often expect "not found", for example for account data that isn't set.
            parameters.Logger.Log(error.ErrorCode == "M_NOT_FOUND" ? LogLevel.Information : LogLevel.Error,
                "{Path} failed (HTTP {Status}): {ErrorCode}, {ErrorMessage}",
                debugPath, (int?)error.StatusCode, error.ErrorCode, error.ErrorMessage);
            throw error;
        }
    }

    private static async Task<MatrixResponseException> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        MatrixErrorResponse? error = null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                error = JsonSerializer.Deserialize<MatrixErrorResponse>(text);
            }
            catch (JsonException)
            {
                // Proxies can return HTML or empty/non-Matrix errors. Preserve status
                // and Retry-After regardless; don't replace a 429/503 with a JSON error.
            }
        }

        var retryAfterHeader = response.Headers.RetryAfter;
        var retryAfter = retryAfterHeader?.Delta;
        if (retryAfter == null && retryAfterHeader?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        if (retryAfter == null && error?.RetryAfterMs is >= 0)
        {
            var milliseconds = Math.Min(error.RetryAfterMs.Value, (long)TimeSpan.MaxValue.TotalMilliseconds);
            retryAfter = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
        }
        if (retryAfter < TimeSpan.Zero)
            retryAfter = TimeSpan.Zero;

        return new MatrixResponseException(
            error?.ErrorCode ?? (response.StatusCode == HttpStatusCode.TooManyRequests ? "M_LIMIT_EXCEEDED" : "M_UNKNOWN"),
            error?.ErrorMessage ?? response.ReasonPhrase ?? "Matrix request failed.",
            response.StatusCode, retryAfter, error?.SoftLogout ?? false)
        {
            ResponseBody = text
        };
    }
}

/// <summary>
/// Represents an error response from the Matrix Server
/// </summary>
public class MatrixResponseException : Exception
{
    public string ErrorCode { get; }
    public string ErrorMessage { get; }
    public HttpStatusCode? StatusCode { get; }
    /// <summary>Server-requested minimum wait. Retry-After takes precedence over retry_after_ms.</summary>
    public TimeSpan? RetryAfter { get; }
    public bool SoftLogout { get; }
    /// <summary>The JSON error body, for answers that carry more than the error code, like authentication challenges.</summary>
    internal string? ResponseBody { get; init; }
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests || ErrorCode == "M_LIMIT_EXCEEDED";
    public bool IsTransient => IsRateLimited || MatrixRetry.IsTransientStatus(StatusCode);

    public MatrixResponseException(string errorCode, string errorMessage)
        : this(errorCode, errorMessage, null, null, false)
    {
    }

    public MatrixResponseException(string errorCode, string errorMessage, HttpStatusCode? statusCode,
        TimeSpan? retryAfter = null, bool softLogout = false)
        : base($"{errorCode}: {errorMessage} (HTTP {(int?)statusCode}, Retry-After: {retryAfter})")
    {
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        SoftLogout = softLogout;
    }
}

internal static class HttpParameterHelper
{
    public static string AppendParameters(string path, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ArgumentNullException.ThrowIfNull(path, nameof(path));
        if (parameters == null)
            return path;

        // Keys and values must be percent-encoded, otherwise parameters containing characters
        // that are reserved in query strings (e.g. '&', '=', '#', '+', ' ') break the request.
        // Uri.EscapeDataString is used instead of HttpUtility.UrlEncode because the latter
        // encodes spaces as '+', which is only correct for form data, not for query strings.
        var formattedParameters = string.Join("&", parameters.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        if (string.IsNullOrWhiteSpace(formattedParameters))
            return path;

        return $"{path}?{formattedParameters}";
    }
}

