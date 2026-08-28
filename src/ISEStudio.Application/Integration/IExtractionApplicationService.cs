using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-side contract for the five <c>extraction.*</c>
/// dispatcher arms. Each method takes an <see cref="InternalRequest"/>
/// envelope (path / query / body / actor) and returns either the wire
/// DTO the dispatcher serialises, or <c>null</c> when the knowledge
/// system / job id can't be resolved.
///
/// <para>The dispatcher arm layer retains the schema-compatible empty
/// payload fallback envelopes (<c>Array.Empty&lt;object&gt;()</c> /
/// <c>EmptyExtractionJob()</c>) &mdash; the application service returns
/// <c>null</c> and the dispatcher substitutes the right shape.</para>
///
/// <para>All three run* arms (<c>extraction.run</c> /
/// <c>extraction.run_combined</c> / <c>extraction.run_instances</c>)
/// share <see cref="RunAsync"/>; the dispatcher passes the literal
/// operation name as <paramref name="runKind"/> so the orchestrator's
/// matching <c>Start*Async</c> entry point is selected. The
/// extraction guard (<c>RunWithExtractionGuardAsync</c>) wraps the
/// call at the switch arm layer, matching the brief's "抽取进行中
/// 的修改返回 409" requirement &mdash; the application service throws
/// no guard of its own.</para>
///
/// <para>Why every method returns <c>object?</c>: the wire DTO
/// (<c>ExtractionJobOut</c>) lives in <c>ISEStudio.Extraction</c>
/// because its <c>From(ExtractionJobEntity)</c> projection depends on
/// <c>ISEStudio.Infrastructure.Persistence.Entities</c>. The
/// <c>ISEStudio.Application</c> project is the zero-ProjectReference
/// contracts layer; promoting <c>ExtractionJobOut</c> here would force
/// a circular reference back through <c>ISEStudio.Infrastructure</c>.
/// See <c>docs/superpowers/specs/2026-08-28-extraction-application-service.md</c>
/// §2.1 for the rationale and the alternative considered.</para>
/// </summary>
public interface IExtractionApplicationService
{
    /// <summary>
    /// Shared body for the three <c>extraction.run*</c> arms. Picks
    /// the matching <see cref="ExtractionOrchestrator.Start*Async"/>
    /// entry point by <paramref name="runKind"/> and projects the
    /// freshly created job row to the wire shape.
    /// </summary>
    /// <remarks>
    /// The body deserialiser accepts both the
    /// <see cref="Extraction.ExtractionRequest"/> shape (caller-supplied
    /// <c>knowledge_system_id</c>, <c>blob_sha</c>, <c>file_name</c>,
    /// <c>provider</c>, <c>model</c>, <c>endpoint</c>) and the
    /// frontend-flavoured <c>{chunk_ids, model}</c> shape (the
    /// <c>/extract-all</c> route). Returns <c>null</c> when the body
    /// is missing or the knowledge system can't be resolved.
    /// </remarks>
    Task<object?> RunAsync(
        InternalRequest request,
        string runKind,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>extraction.list_jobs</c> &mdash; every extraction job for
    /// the bound knowledge system, newest first. Returns <c>null</c>
    /// when no KS id is bound (dispatcher maps to
    /// <c>Array.Empty&lt;object&gt;()</c>).
    /// </summary>
    Task<object?> ListJobsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>extraction.get_job</c> &mdash; one extraction job by id.
    /// Returns <c>null</c> when the KS id is unbound, the job id is
    /// missing / unparsable, or the row is owned by a different KS
    /// (dispatcher maps to <c>EmptyExtractionJob()</c>).
    /// </summary>
    Task<object?> GetJobAsync(InternalRequest request, CancellationToken cancellationToken);
}