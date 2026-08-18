namespace OnToPilot.IntegrationTests.Deployment;

/// <summary>
/// Tiny retry helper used by the container smoke test. The production
/// backend can take 30-90 seconds to publish <c>/api/health</c> on first
/// boot (NuGet restore, EF Core warmup, MinIO S3 client handshake), so
/// the test wraps the request in a deadline-aware loop instead of
/// waiting for a single fixed sleep.
///
/// <para>Returns the first successful response (any 2xx) so the caller
/// can assert the exact status it expects; on timeout the last
/// exception is re-thrown so the test failure message points at the
/// real cause (connection refused, 503, etc.) rather than a generic
/// "timed out".</para>
/// </summary>
internal static class Retry
{
    /// <summary>
    /// Invoke <paramref name="operation"/> every 500 ms until it returns
    /// successfully or <paramref name="timeout"/> elapses. The deadline
    /// is measured from the first call, so a slow first attempt does not
    /// steal budget from later retries.
    /// </summary>
    public static async Task<T> UntilSuccessAsync<T>(
        Func<Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                var result = await operation().ConfigureAwait(false);
                if (result is HttpResponseMessage response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return result;
                    }
                    // Drain the response body before disposing so the
                    // socket can be reused (HttpClient defaults).
                    try { response.Dispose(); } catch { /* best effort */ }
                }
                else
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        throw new TimeoutException(
            $"Operation did not succeed within {timeout} ({attempt} attempts). "
            + "Last error: " + (lastError?.Message ?? "<none>"),
            lastError);
    }
}
