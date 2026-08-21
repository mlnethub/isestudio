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

    /// <summary>
    /// Constructor for the brief's "抽取进行中的修改返回 409" path. The
    /// middleware surfaces <see cref="JobId"/> in the
    /// <c>{"detail": { "error": "...", "job_id": "..." }}</c> envelope
    /// so clients can poll the job row that blocked the mutation.
    /// </summary>
    public GraphWriteConflictException(string message, Guid jobId) : base(message)
    {
        JobId = jobId;
    }

    /// <summary>The extraction job whose in-flight status blocked the mutation.</summary>
    public Guid? JobId { get; }
}

/// <summary>
/// Raised when a delete is refused because some other row still references
/// the target (typical case: a knowledge system or system config still
/// points at a provider row). Maps to HTTP 409 with a plain-string
/// <c>{"detail": "..."}</c> envelope at the API layer — distinct from
/// <see cref="GraphWriteConflictException"/>, which carries the structured
/// <c>{"detail": { "error": "...", "job_id": "..." }}</c> shape mandated
/// by the brief's extraction-in-progress rule.
/// </summary>
/// <remarks>
/// <para>The exception is intentionally generic over the referenced
/// resource kind so future callers (vocabulary schemes, ABox
/// individuals, etc.) can reuse it without inventing a sibling type per
/// case.</para>
/// </remarks>
public sealed class ResourceInUseException : Exception
{
    public ResourceInUseException(string message) : base(message) { }
    public ResourceInUseException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Per-graph reader/writer serialization for the Oxigraph store. Each named
/// graph owns an async reader/writer gate that gives writers
/// exclusive access and lets multiple readers proceed in parallel; writers
/// on different graphs proceed concurrently. There is no read/write
/// interleaving with an active writer.
/// </summary>
public sealed class GraphWriteCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, AsyncGraphLock> _perGraph = new(StringComparer.Ordinal);
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

        var graphLock = _perGraph.GetOrAdd(graphIri, _ => new AsyncGraphLock());
        var deadline = DateTime.UtcNow + waitTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (graphLock.TryAcquireWrite(out var stateChanged))
            {
                return new GraphLease(this, graphLock, isWriter: true);
            }

            var wait = Remaining(deadline);
            if (wait <= TimeSpan.Zero)
            {
                throw WriteTimeout(graphIri, waitTimeout);
            }
            try
            {
                await stateChanged.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw WriteTimeout(graphIri, waitTimeout);
            }
        }
    }

    /// <summary>
    /// Acquire a shared read lease. Multiple readers may hold the lease
    /// concurrently; writers are blocked while any reader holds it.
    /// </summary>
    public async ValueTask<GraphLease> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Acquire read locks on every per-graph gate we know about. The first
        // snapshot might miss a graph added in the middle of acquisition, but
        // the dictionary's GetOrAdd happens-before any read that would notice
        // it; in practice the only writers to add a new graph are callers
        // holding the lock for that new graph already, so they won't need a
        // new read lock. For safety we re-scan until no new entries appear.
        // LockRecursionException is treated as a no-op (same thread re-entry
        // is a no-op for readers — we already hold the read on that lock).
        var held = new List<AsyncGraphLock>(_perGraph.Count + 1);
        try
        {
            int lastCount;
            do
            {
                lastCount = _perGraph.Count;
                foreach (var kv in _perGraph)
                {
                    if (!held.Contains(kv.Value))
                    {
                        while (!kv.Value.TryAcquireRead(out var stateChanged))
                        {
                            await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        held.Add(kv.Value);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            } while (_perGraph.Count > lastCount);

            return new GraphLease(this, rwLock: null, isWriter: false, heldReadLocks: held);
        }
        catch
        {
            foreach (var l in held)
            {
                l.ReleaseRead();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _perGraph.Clear();
    }

    private static TimeSpan Remaining(DateTime deadline) =>
        deadline - DateTime.UtcNow > TimeSpan.Zero ? deadline - DateTime.UtcNow : TimeSpan.Zero;

    private static GraphWriteConflictException WriteTimeout(string graphIri, TimeSpan waitTimeout) =>
        new($"Could not acquire write lock for graph '{graphIri}' within {waitTimeout}.");
}

internal sealed class AsyncGraphLock
{
    private readonly object _sync = new();
    private int _readers;
    private bool _writer;
    private TaskCompletionSource _stateChanged = NewSignal();

    public bool TryAcquireWrite(out Task stateChanged)
    {
        lock (_sync)
        {
            if (!_writer && _readers == 0)
            {
                _writer = true;
                stateChanged = Task.CompletedTask;
                return true;
            }
            stateChanged = _stateChanged.Task;
            return false;
        }
    }

    public bool TryAcquireRead(out Task stateChanged)
    {
        lock (_sync)
        {
            if (!_writer)
            {
                _readers++;
                stateChanged = Task.CompletedTask;
                return true;
            }
            stateChanged = _stateChanged.Task;
            return false;
        }
    }

    public void ReleaseWrite()
    {
        lock (_sync)
        {
            if (!_writer) throw new SynchronizationLockException("The write lock is not held.");
            _writer = false;
            SignalStateChanged();
        }
    }

    public void ReleaseRead()
    {
        lock (_sync)
        {
            if (_readers == 0) throw new SynchronizationLockException("The read lock is not held.");
            _readers--;
            if (_readers == 0) SignalStateChanged();
        }
    }

    private void SignalStateChanged()
    {
        var signal = _stateChanged;
        _stateChanged = NewSignal();
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Disposable lock handle returned by <see cref="GraphWriteCoordinator"/>.
/// Release is idempotent and safe to call from <c>await using</c>.
/// </summary>
public sealed class GraphLease : IAsyncDisposable
{
    private readonly GraphWriteCoordinator _owner;
    private readonly AsyncGraphLock? _writeLock;
    private readonly List<AsyncGraphLock>? _heldReadLocks;
    private readonly bool _isWriter;
    private int _released;

    internal GraphLease(
        GraphWriteCoordinator owner,
        AsyncGraphLock? rwLock,
        bool isWriter,
        List<AsyncGraphLock>? heldReadLocks = null)
    {
        _owner = owner;
        _writeLock = rwLock;
        _isWriter = isWriter;
        _heldReadLocks = heldReadLocks;
    }

    public bool IsWriter => _isWriter;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;

        if (_isWriter && _writeLock != null)
        {
            _writeLock.ReleaseWrite();
        }
        else if (_heldReadLocks != null)
        {
            foreach (var l in _heldReadLocks)
            {
                l.ReleaseRead();
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}