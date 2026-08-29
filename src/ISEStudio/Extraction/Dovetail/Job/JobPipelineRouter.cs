using Dovetail;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;

namespace ISEStudio.Extraction.Dovetail.Job;

/// <summary>
/// Orchestrator-facing entry point for the Dovetail Job sub-DAG.
///
/// <para>Per Slice 5 Task 4 R14: routes a <see cref="JobInput"/> to the
/// appropriate per-kind pipeline (<see cref="TBoxOnlyJobPipeline"/>,
/// <see cref="ABoxOnlyJobPipeline"/>, <see cref="CombinedJobPipeline"/>),
/// folds the <see cref="JobInput"/> into <see cref="JobState"/>, runs the
/// 6-segment canonical chain, and projects the terminal
/// <see cref="TerminologyCarry"/> back into a <see cref="JobResult"/>.</para>
///
/// <para>The pipeline shape is
/// <c>IPipeline&lt;JobState, TerminologyCarry&gt;</c> (R13) — NOT the
/// brief's <c>IPipeline&lt;JobInput, JobResult&gt;</c> — because the
/// first segment's input is <see cref="JobState"/>, the carrier that
/// threads the per-job state through the chain.</para>
/// </summary>
public sealed class JobPipelineRouter
{
    private readonly TBoxOnlyJobPipeline _tboxOnly;
    private readonly ABoxOnlyJobPipeline _aboxOnly;
    private readonly CombinedJobPipeline _combined;

    public JobPipelineRouter(
        TBoxOnlyJobPipeline tboxOnly,
        ABoxOnlyJobPipeline aboxOnly,
        CombinedJobPipeline combined)
    {
        _tboxOnly = tboxOnly ?? throw new ArgumentNullException(nameof(tboxOnly));
        _aboxOnly = aboxOnly ?? throw new ArgumentNullException(nameof(aboxOnly));
        _combined = combined ?? throw new ArgumentNullException(nameof(combined));
    }

    /// <summary>
    /// Run the per-kind pipeline for <paramref name="input"/>. Returns the
    /// <see cref="JobResult"/> the orchestrator's completion handler
    /// persists. The router is the single place the
    /// <c>JobState → TerminologyCarry → JobResult</c> mapping lives.
    /// </summary>
    public async Task<JobResult> ExecuteAsync(JobInput input, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);

        var state = JobState.From(input);
        IPipeline<JobState, TerminologyCarry> pipeline = input.Kind switch
        {
            JobKind.TBoxOnly => _tboxOnly,
            JobKind.ABoxOnly => _aboxOnly,
            JobKind.Combined => _combined,
            _ => throw new ArgumentOutOfRangeException(
                nameof(input), $"Unsupported JobKind: {input.Kind}"),
        };

        var termCarry = await pipeline.ExecuteAsync(state, token).ConfigureAwait(false);
        return JobResult.FromJobState(termCarry.State);
    }
}
