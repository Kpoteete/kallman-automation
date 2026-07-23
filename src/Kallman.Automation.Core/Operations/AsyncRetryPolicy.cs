namespace Kallman.Automation.Core.Operations;

public sealed class AsyncRetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;

    public AsyncRetryPolicy(int maxAttempts = 3, TimeSpan? baseDelay = null)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _maxAttempts = maxAttempts;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
    }

    public async Task<T> ExecuteReadAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _maxAttempts && isTransient(ex))
            {
                double jitter = Random.Shared.NextDouble() * 0.5;
                TimeSpan delay = TimeSpan.FromMilliseconds(
                    _baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (1 + jitter));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<T> ExecuteReconciledWriteAsync<T>(
        Func<CancellationToken, Task<T>> write,
        Func<CancellationToken, Task<T?>> reconcile,
        Func<T?, bool> committed,
        Func<Exception, bool> isUncertain,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await write(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (isUncertain(ex))
        {
            T? existing = await reconcile(cancellationToken).ConfigureAwait(false);
            if (committed(existing) && existing is not null) return existing;
            throw;
        }
    }
}
