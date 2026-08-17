using OnToPilot.Llm;

namespace OnToPilot.Tests.Llm;

/// <summary>
/// Verifies the cross-endpoint isolation, reentrancy, and permit accounting
/// of <see cref="EndpointCapacityCoordinator"/>. The coordinator is the
/// primitive that downstream code (chat / embedding extraction) calls to
/// avoid hammering any single provider endpoint.
///
/// <para>Reentrancy is tracked via <see cref="System.Threading.AsyncLocal{T}"/>
/// flowing with the <see cref="System.Threading.ExecutionContext"/>.
/// Sequential acquires from the same async task re-enter the bucket without
/// blocking; acquires from a different task (e.g. via <c>Task.Run</c>) hit
/// the underlying <see cref="SemaphoreSlim"/> and block until permits
/// free up.</para>
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
    public async Task Same_endpoint_different_callers_block()
    {
        var capacity = NewCoordinator();

        await using var a = await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);

        // Run the second acquire on a separate task so the AsyncLocal
        // state from caller A does not flow into caller B.
        var taskB = Task.Run(async () => await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None));
        await Task.Yield(); // give taskB a chance to enter WaitAsync

        Assert.False(taskB.IsCompleted, "Second caller should block while first holds the lease.");

        await a.DisposeAsync();
        var b = await taskB;
        await b.DisposeAsync();
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

        // taskB asks for 1 permit on a separate task: must complete because
        // the bucket still has 1 permit free after A consumed 1 of 2.
        var taskB = Task.Run(async () => await capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None));
        var b = await taskB;

        // taskC asks for 2 permits on a separate task: must block because
        // 2 permits are now held (A=1, B=1) and the cap is 2.
        var taskC = Task.Run(async () => await capacity.AcquireAsync(new("chat", Endpoint), 2, CancellationToken.None));
        await Task.Yield();
        Assert.False(taskC.IsCompleted, "All permits held; third caller with permits=2 should block.");

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
