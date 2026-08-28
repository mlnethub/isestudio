using ISEStudio.Extraction;
using ISEStudio.Ontology;

namespace ISEStudio.Conflicts;

/// <summary>
/// Orchestrator for <c>conflicts.detect</c>. Owns the multi-step side
/// effect chain that used to live in
/// <c>InternalOperationDispatcher.InvokeConflictDetectAsync</c>:
/// <list type="number">
/// <item>deterministic <see cref="ConflictService.DetectAsync"/>;</item>
/// <item><see cref="ConflictAgent.TriageAsync"/> &mdash; attaches
/// recommendation payloads to open duplicate / predicate_specialization
/// conflicts (never auto-applies, mirrors
/// <c>backend/app/api/conflicts.py::resolve_open_conflicts_bg</c>);</item>
/// <item><see cref="StructureAgent.AttachIsolatedAsync"/> &mdash; attaches
/// classes the LLM left unrooted under a broader kind (mirrors
/// <c>structure_agent.attach_isolated_bg</c>).</item>
/// </list>
/// <para>
/// The 2026-08-28 cross-slice decision (see
/// <c>docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md</c>
/// §6.3) says: when a dispatcher helper has a "svc.X &rarr; agent / structure"
/// fanout, hoist it into a <c>&lt;Slice&gt;XxxOrchestrator</c> so the
/// application service surface stays a single line per op. This class is
/// the first such orchestrator.
/// </para>
/// <para>
/// Both agent calls self-gate on the relevant agentic_* setting + the
/// KS's <c>extraction_active</c> state, and they swallow every LLM
/// error, so the orchestrator never throws &mdash; the
/// <see cref="ConflictService.DetectAsync"/> rows returned to the
/// caller are the pre-triage snapshot, matching the Python semantics.
/// </para>
/// </summary>
public sealed class ConflictDetectionOrchestrator
{
    private readonly ConflictService _conflicts;
    private readonly IServiceProvider _services;

    public ConflictDetectionOrchestrator(
        ConflictService conflicts,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(services);
        _conflicts = conflicts;
        _services = services;
    }

    /// <summary>
    /// Run the deterministic detector, then chain the two agent passes
    /// when they're registered. Mirrors the original
    /// <c>_services.GetService(typeof(ConflictAgent)) as ConflictAgent</c>
/// null-degrade path so the contract-test factory (which doesn't
/// register the agents) keeps working without modification.
    /// </summary>
    public async Task<IReadOnlyList<Application.Conflicts.ConflictOut>> DetectAsync(
        Guid knowledgeSystemId,
        CancellationToken cancellationToken)
    {
        var rows = await _conflicts.DetectAsync(knowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);

        var agent = _services.GetService(typeof(ConflictAgent)) as ConflictAgent;
        if (agent is not null)
        {
            await agent.TriageAsync(knowledgeSystemId, cancellationToken)
                .ConfigureAwait(false);
        }

        var structure = _services.GetService(typeof(StructureAgent)) as StructureAgent;
        if (structure is not null)
        {
            await structure.AttachIsolatedAsync(knowledgeSystemId, model: null, cancellationToken)
                .ConfigureAwait(false);
        }

        return rows;
    }
}
