using ISEStudio.Llm;

namespace ISEStudio.Tests.Llm;

/// <summary>
/// Verifies the cross-endpoint isolation, reentrancy, and permit accounting
/// of <see cref="EndpointCapacityCoordinator"/>. The coordinator is the
/// primitive that downstream code (chat / embedding extraction) calls to
/// avoid hammering any single provider endpoint.
///
/// <para>Reentrancy is tracked via <see cref="System.Threading.AsyncLocal{T}"/>
/// flowing with the <see cref="System.Threading.ExecutionContext"/>. Two
/// acquires on the same key from the same async flow (sequential awaits in
/// the same method, or child tasks started with <c>Task.Run</c> which
/// inherits the captured <see cref="System.Threading.ExecutionContext"/>)
/// re-enter the bucket without blocking.</para>
///
/// <para>An acquire is treated as a "different caller" — and therefore hits
/// the underlying <see cref="SemaphoreSlim"/> and blocks — only when its
/// <see cref="System.Threading.ExecutionContext"/> does not carry the
/// AsyncLocal state forward. In practice that means the acquire ran inside
/// an <see cref="System.Threading.ExecutionContext.SuppressFlow"/> scope,
/// or on a flow that was independently constructed (different request, a
/// brand-new task created from scratch, etc.).</para>
/// </summary>
public sealed class EndpointCapacityTests
{
    private const string Endpoint = "https://example.test/v1";

    private static EndpointCapacityCoordinator NewCoordinator() => new();

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Chat_and_embedding_use_separate_capacity_keys()
    {
        var capacity = NewCoordinator();

        await using var chat = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);
        await using var embedding = await capacity.AcquireAsync(new("embedding", Endpoint), 1, CancellationToken.None);

        Assert.NotNull(chat);
        Assert.NotNull(embedding);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Same_endpoint_same_caller_is_reentrant()
    {
        var capacity = NewCoordinator();

        // Both acquires happen on the same async task, so the second
        // acquire is detected as re-entrant by the AsyncLocal-tracked
        // depth and returns immediately.
        await using var outer = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);
        await using var inner = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);

        Assert.NotNull(outer);
        Assert.NotNull(inner);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Same_endpoint_reentrant_across_Task_Run()
    {
        // Production scenario: a request handler that internally fans
        // work out via Task.Run still counts as the same logical caller,
        // because Task.Run captures the caller's ExecutionContext and
        // the AsyncLocal re-entry state flows with it. The reentrancy
        // here prevents a request handler from deadlocking against
        // itself when it calls into chat/embedding from a child task.
        var capacity = NewCoordinator();

        await using var outer = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);

        var innerTask = Task.Run(async () =>
            await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None));

        // The child task inherits the AsyncLocal state, so the second
        // acquire is a re-entrant bump of the same caller's depth.
        var inner = await innerTask;
        Assert.NotNull(inner);

        await inner.DisposeAsync();
        await outer.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Same_endpoint_different_callers_block()
    {
        // Two requests running on truly disjoint ExecutionContext flows
        // (i.e. with AsyncLocal flow explicitly suppressed for the
        // second request) must serialise on the underlying semaphore.
        // This is the cross-request contention case: while the first
        // request holds the only permit, a second request cannot
        // acquire until the first releases.
        var capacity = NewCoordinator();

        var first = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);

        // Start the sub-task under SuppressFlow so the new caller has
        // no AsyncLocal state to inherit; it therefore has no re-entry
        // depth for the chat key and must wait on the semaphore.
        Task<IAsyncDisposable> blockedTask;
        using (ExecutionContext.SuppressFlow())
        {
            blockedTask = Task.Run(
                () => capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None).AsTask());
        }

        // Race the blocked task against a short timeout. If the
        // timeout wins, the task is still waiting on the semaphore —
        // which is the assertion we care about.
        var probe = Task.Delay(TimeSpan.FromMilliseconds(200));
        var winner = await Task.WhenAny(blockedTask, probe);
        Assert.True(
            ReferenceEquals(winner, probe),
            "Second caller should block while first holds the lease.");

        await first.DisposeAsync();
        var second = await blockedTask;
        await second.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Permit_count_is_enforced_with_disjoint_tasks()
    {
        // Two independent flows, each with their own suppressed
        // ExecutionContext, race for permits on a 2-permit bucket.
        // A (2 permits requested, 1 consumed) and B (1 permit
        // requested, 1 consumed) both succeed. C (2 permits requested)
        // then blocks because the bucket is saturated. After A and B
        // release, C completes.
        var capacity = NewCoordinator();

        var a = await capacity.AcquireAsync(new("chat", Endpoint), 2, CancellationToken.None);

        // B runs under SuppressFlow: with no AsyncLocal state, B is
        // not a re-entry of A and must consume a real permit.
        Task<IAsyncDisposable> bTask;
        using (ExecutionContext.SuppressFlow())
        {
            bTask = Task.Run(
                () => capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None).AsTask());
        }
        var b = await bTask;

        // C also runs under SuppressFlow and requests 2 permits; with
        // 2 of 2 permits held, C must block.
        Task<IAsyncDisposable> cTask;
        using (ExecutionContext.SuppressFlow())
        {
            cTask = Task.Run(
                () => capacity.AcquireAsync(new("chat", Endpoint), 2, CancellationToken.None).AsTask());
        }

        var probe = Task.Delay(TimeSpan.FromMilliseconds(200));
        var winner = await Task.WhenAny(cTask, probe);
        Assert.True(
            ReferenceEquals(winner, probe),
            "C should block while A and B each hold a permit.");

        await b.DisposeAsync();
        await a.DisposeAsync();

        // Now both permits are free; C must complete.
        var c = await cTask.WaitAsync(TimeSpan.FromSeconds(2));
        await c.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Different_endpoints_proceed_concurrently()
    {
        var capacity = NewCoordinator();

        await using var a = await capacity.AcquireAsync(new("chat", "https://a.test/v1"), 1, CancellationToken.None);
        await using var b = await capacity.AcquireAsync(new("chat", "https://b.test/v1"), 1, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Permit_count_is_enforced()
    {
        var capacity = NewCoordinator();

        await using var a = await capacity.AcquireAsync(new("chat", Endpoint), 2, CancellationToken.None);

        // taskB and taskC run under ExecutionContext.SuppressFlow so
        // they have no AsyncLocal state to inherit. Without
        // SuppressFlow, the sub-tasks would be treated as re-entrant
        // (Task.Run captures the caller's ExecutionContext, including
        // AsyncLocal values), and the test's "B consumes a permit"
        // assumption would not hold. SuppressFlow is the documented
        // way to model "disjoint caller" against this coordinator.
        Task<IAsyncDisposable> taskB;
        using (ExecutionContext.SuppressFlow())
        {
            taskB = Task.Run(() => capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None).AsTask());
        }
        var b = await taskB;

        Task<IAsyncDisposable> taskC;
        using (ExecutionContext.SuppressFlow())
        {
            taskC = Task.Run(() => capacity.AcquireAsync(new("chat", Endpoint), 2, CancellationToken.None).AsTask());
        }

        // Race taskC against a short timeout. If the timeout wins,
        // taskC is still waiting on the semaphore (A=1, B=1 → 0
        // free; taskC asks for 2). Using Task.WhenAny + Delay
        // (rather than Task.Yield) makes the check deterministic
        // instead of timing-dependent.
        var probe = Task.Delay(TimeSpan.FromMilliseconds(200));
        var winner = await Task.WhenAny(taskC, probe);
        Assert.True(
            ReferenceEquals(winner, probe),
            "All permits held; third caller with permits=2 should block.");

        await b.DisposeAsync();
        await a.DisposeAsync();
        var c = await taskC;
        await c.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Llm")]
    public async Task Dispose_releases_permits()
    {
        var capacity = NewCoordinator();

        var first = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);
        await first.DisposeAsync();

        var second = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);
        Assert.NotNull(second);
        await second.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Concurrency_limit_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LlmProviderConfig.ValidateConcurrencyLimit(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LlmProviderConfig.ValidateConcurrencyLimit(65));
    }
}