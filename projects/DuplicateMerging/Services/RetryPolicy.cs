using System.Net;
using System.Net.Http;

namespace DuplicateMerging.Services;

public sealed class RetryPolicy
{
    private readonly int _maxAttempts;

    public RetryPolicy(int maxAttempts)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _maxAttempts && IsTransient(ex))
            {
                lastException = ex;
                int delaySeconds = (int)Math.Pow(2, attempt - 1);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException ?? new InvalidOperationException("Retry operation failed without an exception.");
    }

    public static bool IsTransient(Exception ex)
    {
        if (ex is AggregateException aggregateEx)
        {
            return aggregateEx.Flatten().InnerExceptions.Any(IsTransient);
        }

        if (ex is HttpRequestException httpEx)
        {
            return httpEx.StatusCode is null ||
                   httpEx.StatusCode == HttpStatusCode.RequestTimeout ||
                   httpEx.StatusCode == HttpStatusCode.TooManyRequests ||
                   (int)httpEx.StatusCode >= 500;
        }

        if (ex is TaskCanceledException || ex is TimeoutException) return true;

        string message = ex.ToString();
        return message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("408", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporar", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("sending the request", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("HttpClient called failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("500", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("504", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeValidationError(Exception ex)
    {
        string message = ex.ToString();
        return message.Contains("400", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("validation", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("bad request", StringComparison.OrdinalIgnoreCase);
    }
}
