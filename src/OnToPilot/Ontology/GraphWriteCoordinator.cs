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
/// Per-graph reader/writer serialization for the Oxigraph store. Each named
/// graph owns a <see cref="ReaderWriterLockSlim"/> that gives writers
/// exclusive access and lets multiple readers proceed in parallel; writers
/// on different graphs proceed concurrently. The lock is fair (FIFO) so a
/// write that loses the race against a held reader waits until the reader
/// exits — no read/write interleaving with an active writer.
/// </summary>
public sealed class GraphWriteCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _perGraph = new(StringComparer.Ordinal);
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(10);
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

        var rwLock = _perGraph.GetOrAdd(graphIri, _ => new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));

        // ReaderWriterLockSlim.WaitToWriteAsync does not exist; poll on a
        // short interval until the upgradeable-read slot is free, then take
        // the write lock. Two phases so a writer never runs concurrently
        // with a reader (or another writer) on the same graph.
        var deadline = DateTime.UtcNow + waitTimeout;
        bool upgraded = false;
        try
        {
            while (!upgraded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new GraphWriteConflictException(
                        $"Could not acquire write lock for graph '{graphIri}' within {waitTimeout}.");
                }

                // TryEnterUpgradeableReadLock blocks writers but lets readers
                // through. Once we hold the upgradeable read, we can safely
                // try to upgrade to a write lock. LockRecursionException
                // means the current thread already holds a lock on this
                // graph — surface as a conflict instead of deadlocking.
                bool tookUpgradeable;
                try
                {
                    tookUpgradeable = rwLock.TryEnterUpgradeableReadLock(TimeSpan.FromMilliseconds(50));
                }
                catch (LockRecursionException)
                {
                    throw new GraphWriteConflictException(
                        $"Graph '{graphIri}' is already locked by the current thread (nested CaptureAsync).");
                }

                if (tookUpgradeable)
                {
                    try
                    {
                        if (rwLock.TryEnterWriteLock(remaining(deadline)))
                        {
                            upgraded = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (!upgraded)
                        {
                            rwLock.ExitUpgradeableReadLock();
                        }
                    }
                }

                try
                {
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GraphWriteConflictException(
                        $"Could not acquire write lock for graph '{graphIri}' within {waitTimeout}.");
                }
            }
        }
        catch
        {
            if (upgraded)
            {
                rwLock.ExitWriteLock();
                rwLock.ExitUpgradeableReadLock();
            }
            throw;
        }

        return new GraphLease(this, rwLock, isWriter: true);
    }

    /// <summary>
    /// Acquire a shared read lease. Multiple readers may hold the lease
    /// concurrently; writers are blocked while any reader holds it.
    /// </summary>
    public async ValueTask<GraphLease> ReadLockAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Acquire read locks on every per-graph RWL we know about. The first
        // snapshot might miss a graph added in the middle of acquisition, but
        // the dictionary's GetOrAdd happens-before any read that would notice
        // it; in practice the only writers to add a new graph are callers
        // holding the lock for that new graph already, so they won't need a
        // new read lock. For safety we re-scan until no new entries appear.
        // LockRecursionException is treated as a no-op (same thread re-entry
        // is a no-op for readers — we already hold the read on that lock).
        var held = new List<ReaderWriterLockSlim>(_perGraph.Count + 1);
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
                        try
                        {
                            kv.Value.TryEnterReadLock(TimeSpan.FromMilliseconds(50));
                            held.Add(kv.Value);
                        }
                        catch (LockRecursionException)
                        {
                            // Same thread re-entry — skip, but don't double-add.
                        }
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
                l.ExitReadLock();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var l in _perGraph.Values)
        {
            l.Dispose();
        }
        _perGraph.Clear();
    }

    private static TimeSpan remaining(DateTime deadline) =>
        deadline - DateTime.UtcNow > TimeSpan.Zero ? deadline - DateTime.UtcNow : TimeSpan.Zero;
}

/// <summary>
/// Disposable lock handle returned by <see cref="GraphWriteCoordinator"/>.
/// Release is idempotent and safe to call from <c>await using</c>.
/// </summary>
public sealed class GraphLease : IAsyncDisposable
{
    private readonly GraphWriteCoordinator _owner;
    private readonly ReaderWriterLockSlim? _writeLock;
    private readonly List<ReaderWriterLockSlim>? _heldReadLocks;
    private readonly bool _isWriter;
    private int _released;

    internal GraphLease(
        GraphWriteCoordinator owner,
        ReaderWriterLockSlim? rwLock,
        bool isWriter,
        List<ReaderWriterLockSlim>? heldReadLocks = null)
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
            // Reverse order of acquisition: write → upgradeable read.
            _writeLock.ExitWriteLock();
            _writeLock.ExitUpgradeableReadLock();
        }
        else if (_heldReadLocks != null)
        {
            foreach (var l in _heldReadLocks)
            {
                l.ExitReadLock();
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}