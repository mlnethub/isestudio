namespace OnToPilot.Llm;

/// <summary>
/// Coordinates per-endpoint concurrency budgets for LLM / embedding traffic.
///
/// <para>The coordinator allocates one bucket per
/// <see cref="EndpointCapacityKey"/>. The bucket's capacity is the largest
/// <c>permits</c> value ever requested for that key; every successful
/// acquire consumes exactly one permit from the bucket. This matches the
/// per-endpoint concurrency limit configured via
/// <see cref="LlmProviderConfig.ConcurrencyLimit"/> while leaving the
/// caller free to ask for more than they intend to hold (a common idiom
/// when the caller wants to assert "this endpoint can serve at least
/// <c>N</c> things in flight").</para>
///
/// <para>Reentrancy: the same logical caller may acquire the same key any
/// number of times without blocking. The outermost lease is the one that
/// actually returns the permit to the bucket on dispose; inner leases
/// just bump the caller's re-entry counter.</para>
///
/// <para>The "logical caller" is tracked via an
/// <see cref="AsyncLocal{T}"/> that flows with the
/// <see cref="System.Threading.ExecutionContext"/>. Sequential
/// <c>await coordinator.AcquireAsync(...)</c> calls on the same async
/// task re-enter the bucket without blocking. <c>Task.Run</c> sub-tasks
/// inherit the caller's <see cref="System.Threading.ExecutionContext"/>
/// (and therefore the AsyncLocal state), so a request handler that
/// internally fans out via <c>Task.Run</c> still counts as the same
/// logical caller and will not deadlock against itself. Different
/// callers are different <see cref="System.Threading.ExecutionContext"/>
/// flows — for example, two HTTP requests handled on independent flows,
/// or a flow that explicitly used
/// <see cref="System.Threading.ExecutionContext.SuppressFlow"/>.</para>
/// </summary>
public sealed class EndpointCapacityCoordinator
{
    private readonly Dictionary<EndpointCapacityKey, Bucket> _buckets = new();
    private readonly object _bucketsLock = new();

    /// <summary>
    /// Per-caller re-entry counters, keyed by
    /// <see cref="EndpointCapacityKey"/>. Each caller's re-entry depth
    /// map is stored in this <see cref="AsyncLocal{T}"/>; the value
    /// flows with the <see cref="System.Threading.ExecutionContext"/>
    /// so sequential awaits on the same async task see the same
    /// state.
    /// </summary>
    private readonly AsyncLocal<ReentryState?> _reentry = new();

    /// <summary>
    /// Acquire one permit from the bucket identified by <paramref name="key"/>,
    /// blocking until a permit is available (unless the caller is already
    /// inside a lease on the same key, in which case this returns
    /// immediately and just bumps the re-entry depth).
    /// </summary>
    /// <param name="key">Capability + endpoint bucket to draw a permit from.</param>
    /// <param name="permits">
    /// The maximum number of permits the caller wants to hold concurrently
    /// on this bucket. The bucket's capacity is grown to accommodate the
    /// largest value ever requested. Each successful acquire consumes
    /// exactly one permit regardless of this value.
    /// </param>
    /// <param name="cancellationToken">Cancellation forwarded to the wait.</param>
    /// <remarks>
    /// <para>Reentrancy is tracked via the per-caller
    /// <see cref="AsyncLocal{T}"/> state that flows with the
    /// <see cref="System.Threading.ExecutionContext"/>. Two acquires on
    /// the same key from the same async flow — sequential awaits on the
    /// same async method, or a child task started with <c>Task.Run</c>
    /// (which inherits the captured
    /// <see cref="System.Threading.ExecutionContext"/>) — re-enter the
    /// bucket without blocking. This matches the production use case: a
    /// request handler that internally fans out via <c>Task.Run</c>
    /// must not deadlock against itself when chat and embedding
    /// extraction both call back into the coordinator.</para>
    ///
    /// <para>An acquire is treated as a "different caller" — and
    /// therefore hits the underlying <see cref="SemaphoreSlim"/> and
    /// blocks — only when its
    /// <see cref="System.Threading.ExecutionContext"/> does not carry
    /// the AsyncLocal state forward. In practice that means the acquire
    /// ran inside an
    /// <see cref="System.Threading.ExecutionContext.SuppressFlow"/>
    /// scope, or on a flow that was independently constructed
    /// (different request, a brand-new task created from scratch,
    /// etc.).</para>
    /// </remarks>
    public ValueTask<IAsyncDisposable> AcquireAsync(
        EndpointCapacityKey key,
        int permits,
        CancellationToken cancellationToken)
    {
        if (permits <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permits),
                permits,
                "Permits must be positive.");
        }
        permits = Math.Min(permits, LlmProviderConfig.MaxConcurrencyLimit);

        // Sync phase: must run on the caller's frame so the AsyncLocal
        // write below is visible to subsequent acquires on the same
        // logical task. Marking AcquireAsync `async` would push this
        // write into a captured ExecutionContext that the caller never
        // sees, which would break the reentrant test.
        var state = _reentry.Value;
        int reentryDepth;
        if (state is null)
        {
            // First call from this ExecutionContext; allocate a fresh
            // per-caller depth map. Subsequent re-entrant calls on the
            // same async task will see this same instance via the
            // AsyncLocal flow.
            state = new ReentryState();
            _reentry.Value = state;
            reentryDepth = state.IncrementIfMatch(key);
        }
        else
        {
            // Either a re-entrant acquire (depth > 0 on this key) or
            // a fresh acquire for a different key on the same caller.
            reentryDepth = state.IncrementIfMatch(key);
        }

        if (reentryDepth > 0)
        {
            // The outer lease on this key still holds the permit, so the
            // inner re-entry just bumps the depth.
            return new ValueTask<IAsyncDisposable>(new Lease(this, key, isReentrant: true));
        }

        var bucket = GetOrCreateBucket(key, permits);
        return AcquireOnSemaphoreAsync(bucket, state, key, cancellationToken);
    }

    /// <summary>
    /// Async tail: wait on the bucket's <see cref="SemaphoreSlim"/> and
    /// register the outer lease once a permit is acquired. Runs on the
    /// thread pool (via <c>ConfigureAwait(false)</c>) so AsyncLocal state
    /// is isolated from the caller's frame — which is exactly what we
    /// want, because the state was already published synchronously in
    /// <see cref="AcquireAsync"/>.
    /// </summary>
    private async ValueTask<IAsyncDisposable> AcquireOnSemaphoreAsync(
        Bucket bucket,
        ReentryState state,
        EndpointCapacityKey key,
        CancellationToken cancellationToken)
    {
        await bucket.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // We hold a permit; mark this as the outermost lease so the
        // matching dispose releases the permit. If a previous lease
        // that took a permit never released it (e.g. because the caller
        // crashed) the held counter can exceed the cap; that's still
        // safe because dispose always releases exactly one permit.
        state.RegisterOuterLease(key);
        Interlocked.Increment(ref bucket.Held);
        return new Lease(this, key, isReentrant: false);
    }

    private Bucket GetOrCreateBucket(EndpointCapacityKey key, int requestedPermits)
    {
        lock (_bucketsLock)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket(requestedPermits, new SemaphoreSlim(requestedPermits, requestedPermits));
                _buckets[key] = bucket;
            }
            else
            {
                // Grow the bucket to accommodate a larger request. We do
                // not shrink the bucket because that would invalidate
                // outstanding leases' Held counter.
                if (requestedPermits > bucket.Capacity)
                {
                    var newCap = requestedPermits;
                    var newGate = new SemaphoreSlim(newCap, newCap);
                    bucket.ReplaceGate(newGate, newCap);
                }
            }
            return bucket;
        }
    }

    /// <summary>
    /// Per-key bucket. Holds a SemaphoreSlim that gates acquires and a
    /// counter of how many permits have been consumed but not yet released.
    /// </summary>
    private sealed class Bucket
    {
        private int _capacity;
        public Bucket(int capacity, SemaphoreSlim gate)
        {
            _capacity = capacity;
            Gate = gate;
        }
        public SemaphoreSlim Gate { get; private set; }
        public int Capacity => _capacity;
        public int Held;

        public void ReplaceGate(SemaphoreSlim newGate, int newCapacity)
        {
            Gate = newGate;
            _capacity = newCapacity;
        }
    }

    /// <summary>
    /// Per-caller bookkeeping. One instance per distinct caller (per
    /// <see cref="System.Threading.ExecutionContext"/> flow), keyed by
    /// <see cref="EndpointCapacityKey"/>.
    /// </summary>
    private sealed class ReentryState
    {
        private readonly Dictionary<EndpointCapacityKey, int> _depths = new();

        /// <summary>
        /// If the caller already holds an outer lease on <paramref name="key"/>,
        /// increment the depth and return the new depth. Otherwise return 0
        /// (the caller is on the outermost level and must wait on the bucket).
        /// </summary>
        public int IncrementIfMatch(EndpointCapacityKey key)
        {
            if (_depths.TryGetValue(key, out var current) && current > 0)
            {
                _depths[key] = current + 1;
                return current + 1;
            }
            return 0;
        }

        public void RegisterOuterLease(EndpointCapacityKey key)
        {
            _depths[key] = 1;
        }

        /// <summary>
        /// Decrement the depth for <paramref name="key"/>. Returns
        /// <c>true</c> when the depth reached 0 (i.e. this dispose is
        /// the outermost one and must release the bucket permit).
        /// </summary>
        public bool Decrement(EndpointCapacityKey key)
        {
            if (!_depths.TryGetValue(key, out var current) || current <= 0)
            {
                return true;
            }
            if (current == 1)
            {
                _depths.Remove(key);
                return true;
            }
            _depths[key] = current - 1;
            return false;
        }
    }

    /// <summary>
    /// A single lease class handles both outermost and re-entrant acquires.
    /// On dispose we decrement the per-task depth and release the bucket
    /// permit only when the depth reaches 0 (i.e. on the outermost dispose).
    /// </summary>
    private sealed class Lease : IAsyncDisposable
    {
        private readonly EndpointCapacityCoordinator _owner;
        private readonly EndpointCapacityKey _key;
        private readonly bool _holdsPermit;
        private bool _disposed;

        internal Lease(
            EndpointCapacityCoordinator owner,
            EndpointCapacityKey key,
            bool isReentrant)
        {
            _owner = owner;
            _key = key;
            _holdsPermit = !isReentrant;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }
            _disposed = true;

            var state = _owner._reentry.Value;
            var isOutermost = state?.Decrement(_key) ?? true;

            if (_holdsPermit && isOutermost)
            {
                if (_owner._buckets.TryGetValue(_key, out var bucket))
                {
                    Interlocked.Decrement(ref bucket.Held);
                    bucket.Gate.Release();
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}