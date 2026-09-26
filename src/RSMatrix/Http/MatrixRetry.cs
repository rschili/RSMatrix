using System.Net;
using Microsoft.Extensions.Logging;

namespace RSMatrix.Http;

/// <summary>Retries within a session; never performs authentication.</summary>
internal static class MatrixRetry
{
    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        return exception switch
        {
            MatrixResponseException response => response.IsTransient,
            HttpRequestException http => http.StatusCode == null || IsTransientStatus(http.StatusCode),
            OperationCanceledException => true, // HTTP timeout, not caller cancellation
            _ => false
        };
    }

    internal static bool IsTransientStatus(HttpStatusCode? status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int?)status is >= 500 and <= 599;

    internal static TimeSpan GetDelay(int failureCount, TimeSpan? retryAfter = null)
    {
        // Cap our backoff, not the server's minimum wait. Jitter is only additive.
        var milliseconds = Math.Min(300_000, 5_000 * Math.Pow(2, Math.Clamp(failureCount, 0, 6)));
        var delay = TimeSpan.FromMilliseconds(milliseconds + Random.Shared.Next(0, 1000));
        return retryAfter is { } minimum && minimum > delay ? minimum : delay;
    }

    internal static async Task DelayAsync(HttpClientParameters parameters, TimeSpan delay)
    {
        // Task.Delay has a finite maximum. Don't truncate a long server-requested wait.
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            parameters.CancellationToken.ThrowIfCancellationRequested();
            var chunk = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
            await parameters.RetryDelayAsync(chunk, parameters.CancellationToken).ConfigureAwait(false);
            remaining -= chunk;
        }
        parameters.CancellationToken.ThrowIfCancellationRequested();
    }

    internal static async Task<T> ExecuteAsync<T>(HttpClientParameters parameters, string operation, Func<Task<T>> action)
    {
        var failureCount = 0;
        while (true)
        {
            parameters.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, parameters.CancellationToken))
            {
                var delay = GetDelay(failureCount, (ex as MatrixResponseException)?.RetryAfter);
                failureCount = Math.Min(failureCount + 1, 6);
                parameters.Logger.LogWarning(ex,
                    "{Operation} failed transiently. Retrying in {Delay}s with the existing Matrix session.",
                    operation, delay.TotalSeconds);
                await DelayAsync(parameters, delay).ConfigureAwait(false);
            }
        }
    }

    internal static Task ExecuteAsync(HttpClientParameters parameters, string operation, Func<Task> action)
        => ExecuteAsync(parameters, operation, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        });
}
