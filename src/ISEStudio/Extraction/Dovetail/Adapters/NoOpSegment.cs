using Dovetail;

namespace ISEStudio.Extraction.Dovetail.Adapters;

/// <summary>
/// Always returns <paramref name="factory"/> result. Used for placeholder
/// registration when a feature is disabled at startup.
/// </summary>
public sealed class NoOpSegment<TIn, TOut>(Func<TIn, TOut> factory) : IPipelineSegment<TIn, TOut>
{
    private readonly Func<TIn, TOut> _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken) =>
        Task.FromResult(_factory(input));
}

/// <summary>
/// 3-arity <see cref="NoOpSegment{TIn, TOut}"/>: ignores both the pipeline
/// input and the predecessor carry and folds a static
/// <see cref="Func{T1, TOut}"/> over the predecessor. Used by the Slice 5
/// Job pipelines (R7 canonical chain) to substitute a no-op step for a
/// skipped phase slot at positions 2..N where the segment signature is
/// <c>IPipelineSegment&lt;JobState, TPredecessor, TCarry&gt;</c>. The 2-arity
/// <see cref="NoOpSegment{TIn, TOut}"/> serves position 1 (the
/// pipeline-entry slot where there is no predecessor).
/// </summary>
public sealed class NoOpSegment<TIn, T1, TOut>(Func<T1, TOut> factory)
    : IPipelineSegment<TIn, T1, TOut>
{
    private readonly Func<T1, TOut> _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<TOut> ExecuteAsync(TIn input, T1 predecessor, CancellationToken cancellationToken) =>
        Task.FromResult(_factory(predecessor));
}

/// <summary>
/// 3-arity chain adapter: wraps a 2-arity <see cref="IPipelineSegment{TIn, TOut}"/>
/// (the Task 3 segment shape) and adapts it into a 3-arity
/// <see cref="IPipelineSegment{TIn, T1, TOut}"/> slot in the canonical Job
/// pipeline. The adapter routes either the pipeline input or the
/// predecessor carry's <c>State</c> (when <paramref name="carryStateMapper"/>
/// is supplied) into the inner segment's <c>TIn</c> argument, preserving
/// the chain semantics without modifying the inner segment's signature.
///
/// <para>Used by the Slice 5 Job pipeline slots 2..N: each
/// <see cref="ISEStudio.Extraction.Dovetail.Job.Steps.XxxStep"/> stays
/// 2-arity (Task 3 R3 minimum commitment), and the pipeline wraps it in
/// <see cref="ChainAdapter{TIn, T1, TOut}"/> with a
/// <c>carry =&gt; carry.State</c> mapper so downstream steps observe the
/// post-previous-step <see cref="ISEStudio.Extraction.Dovetail.Job.JobState"/>
/// — the same shape <c>CombinedRunnerAsync</c> threads through manually.</para>
/// </summary>
public sealed class ChainAdapter<TIn, T1, TOut>(
    IPipelineSegment<TIn, TOut> inner,
    Func<T1, TIn>? carryStateMapper = null) : IPipelineSegment<TIn, T1, TOut>
{
    private readonly IPipelineSegment<TIn, TOut> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<T1, TIn>? _carryStateMapper = carryStateMapper;

    public Task<TOut> ExecuteAsync(TIn input, T1 predecessor, CancellationToken cancellationToken)
    {
        var innerInput = _carryStateMapper is not null
            ? _carryStateMapper(predecessor)
            : input;
        return _inner.ExecuteAsync(innerInput, cancellationToken);
    }
}
