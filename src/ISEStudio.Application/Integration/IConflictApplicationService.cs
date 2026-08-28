using ISEStudio.Application.Conflicts;
using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application service for the nine <c>conflicts.*</c> operations the
/// internal REST contract exposes. Each method unpacks one
/// <see cref="InternalRequest"/> (path / query / body / actor), delegates
/// to the underlying domain service (and, for <see cref="DetectAsync"/>,
/// the <c>ConflictDetectionOrchestrator</c>), and returns the
/// strongly-typed DTO the dispatcher should serialise &mdash; or
/// <c>null</c> when the operation has no body.
/// <para>
/// <b>detect fanout.</b> <see cref="DetectAsync"/> fans out to the
/// <c>ConflictAgent</c> triage pass + the <c>StructureAgent</c>
/// isolated-class attach pass after the deterministic detector returns
/// (mirrors <c>backend/app/api/conflicts.py::detect</c> + the
/// <c>resolve_open_conflicts_bg</c> + <c>structure_agent.attach_isolated_bg</c>
/// side-effects). The whole fanout is owned by
/// <see cref="ConflictDetectionOrchestrator"/>; this service delegates
/// to it as a single op so the dispatcher arm stays one line.
/// </para>
/// </summary>
public interface IConflictApplicationService
{
    /// <summary>
    /// <c>conflicts.list</c> &mdash; open conflict rows for one KS,
    /// optionally narrowed by <c>status</c> + <c>ctype</c> query
    /// parameters. Returns <c>null</c> when the KS id is missing.
    /// </summary>
    Task<IReadOnlyList<ConflictOut>?> ListAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>conflicts.detect</c> &mdash; run the deterministic detector
    /// plus the agentic triage + isolated-class attach passes (see the
    /// class doc). The dispatcher still owns the
    /// <c>{items:[],total:0}</c> empty-list envelope when this returns
    /// <c>null</c>; service-level null means "no KS bound".
    /// </summary>
    Task<IReadOnlyList<ConflictOut>?> DetectAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>conflicts.get_context</c> &mdash; one conflict + its evidence
    /// bundles. Returns <c>null</c> when the resource id is missing or
    /// doesn't parse; dispatcher maps to the empty envelope.
    /// </summary>
    Task<ConflictContext?> GetContextAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>conflicts.dismiss</c> &mdash; mark one conflict as dismissed.</summary>
    Task<ConflictOut?> DismissAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>conflicts.reopen</c> &mdash; re-open a previously dismissed conflict.</summary>
    Task<ConflictOut?> ReopenAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>conflicts.resolve</c> &mdash; apply a resolution id from the
    /// conflict's payload. Body deserialised via
    /// <see cref="ResolveConflictRequest"/>; returns <c>null</c> when
    /// the body is missing / malformed (dispatcher surfaces a 4xx via
    /// the <c>InvalidOperationException</c> fallback the helper used to
    /// throw).
    /// </summary>
    Task<ResolveConflictResponse?> ResolveAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>conflicts.list_reconciliations</c> &mdash; paginated
    /// reconciliation memory rows. <paramref name="request"/> query
    /// carries <c>q</c> + <c>limit</c> + <c>offset</c>.
    /// </summary>
    Task<ReconciliationListResponse> ListReconciliationsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>conflicts.revoke_reconciliation</c> &mdash; drop one
    /// reconciliation memory row. Returns <c>null</c> when the resource
    /// id is missing or doesn't parse (dispatcher maps to the
    /// <c>{ok:false}</c> fallback envelope).
    /// </summary>
    Task<Guid?> RevokeReconciliationAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>conflicts.edit_reconciliation_reason</c> &mdash; update the
    /// human-readable reason on one reconciliation row. Body
    /// deserialised via <see cref="EditReconciliationReasonRequest"/>.
    /// Returns <c>null</c> when the resource id is missing or doesn't
    /// parse. Mirrors <see cref="ConflictService.EditReconciliationReasonAsync"/>'s
    /// <c>(Guid Id, string Reason)?</c> shape so the dispatcher can
    /// project the <c>{id, reason}</c> wire shape without materialising
    /// the full <see cref="ReconciliationOut"/> record (which the Python
    /// backend doesn't expose on this endpoint).
    /// </summary>
    Task<(Guid Id, string Reason)?> EditReconciliationReasonAsync(InternalRequest request, CancellationToken cancellationToken);
}
