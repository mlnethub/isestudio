using System.Collections.Concurrent;

namespace OnToPilot.Ontology;

/// <summary>
/// Raised when a caller cannot acquire a per-graph write or read lease within
/// the configured wait window. Maps cleanly to HTTP 409 at the API layer.
/// </summary>
public sealed class GraphWriteConflictException : Exception
{
    public GraphWriteConflictException(string message) : base(message) { }
    public GraphWriteConflictException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Per-graph writer serialization plus a global read/write lock for the
/// Oxigraph store. The implementation uses:
/// <list type="bullet">
///   <item>A per-graph <see cref="SemaphoreSlim"/> to serialize writers on
///         the same graph while letting writers on different graphs proceed
///         in parallel.</item>
///   <item>An atomic reader counter + draining barrier: writers wait until the
///         active reader count reaches zero before proceeding. Readers can be
///         re-entered from the same thread (so a controller that wants to
///         both read and acquire the write lock can hold a read lease while
///         building the diff).</item>
/// </list>
/// </summary>
public sealed class GraphWriteCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writers = new(StringComparer.Ordinal);
    private int _readersActive;
    private bool _disposed;

    /// <summary>
    /// Acquire an exclusive write lease for the named graph, waiting up to
    /// <paramref name="waitTimeout"/> before throwing a
    /// <see cref="GraphWriteConflictException"/>.
    /// </summary>
    public async ValueTask<GraphLease> AcquireAsync(
        string graphIri,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(graphIri);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sem = _writers.GetOrAdd(graphIri, _ => new SemaphoreSlim(1, 1));

        bool acquired;
        try
        {
            acquired = await sem.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GraphWriteConflictException(
                $"Could not acquire write lock for graph '{graphIri}' within {waitTimeout}.");
        }

        if (!acquired)
        {
            throw new GraphWriteConflictException(
                $"Could not acquire write lock for graph '{graphIri}' within {waitTimeout}.");
        }

        // Drain all readers before returning the writer lease. We poll every
        // 10ms; this is cheap (one volatile read per poll) and keeps the
        // implementation free of any cross-thread signaling bookkeeping.
        var deadline = DateTime.UtcNow + waitTimeout;
        while (Volatile.Read(ref _readersActive) > 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                sem.Release();
                throw new GraphWriteConflictException(
                    $"Could not acquire write lock for graph '{graphIri}' within {waitTimeout} (readers active).");
            }
            try
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sem.Release();
                throw new GraphWriteConflictException(
                    $"Could not acquire write lock for graph '{graphIri}' within {waitTimeout} (cancelled).");
            }
        }

        return new GraphLease(this, sem, isWriter: true);
    }

    /// <summary>
    /// Acquire a shared read lease. Multiple readers may hold the lease
    /// concurrently; writers are blocked while any reader holds it.
    /// </summary>
    public ValueTask<GraphLease> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _readersActive);
        return new ValueTask<GraphLease>(new GraphLease(this, semaphore: null, isWriter: false));
    }

    internal void ReleaseReader()
    {
        Interlocked.Decrement(ref _readersActive);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sem in _writers.Values)
        {
            sem.Dispose();
        }
        _writers.Clear();
    }
}

/// <summary>
/// Disposable lock handle returned by <see cref="GraphWriteCoordinator"/>.
/// Release is idempotent and safe to call from <c>await using</c>.
/// </summary>
public sealed class GraphLease : IAsyncDisposable
{
    private readonly GraphWriteCoordinator _owner;
    private readonly SemaphoreSlim? _writerSemaphore;
    private readonly bool _isWriter;
    private int _released;

    internal GraphLease(GraphWriteCoordinator owner, SemaphoreSlim? semaphore, bool isWriter)
    {
        _owner = owner;
        _writerSemaphore = semaphore;
        _isWriter = isWriter;
    }

    public bool IsWriter => _isWriter;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;

        if (_isWriter)
        {
            _writerSemaphore?.Release();
        }
        else
        {
            _owner.ReleaseReader();
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
