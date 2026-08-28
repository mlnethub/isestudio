using ISEStudio.Application.Conflicts;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Conflicts;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Default in-process implementation of <see cref="IConflictApplicationService"/>.
/// Each method unpacks one <see cref="InternalRequest"/> envelope
/// (path / query / body / actor) and delegates to the underlying
/// <see cref="ConflictService"/>. The multi-step fanout for
/// <see cref="DetectAsync"/> is owned by
/// <see cref="ConflictDetectionOrchestrator"/>; this service just
/// forwards.
/// <para>
/// <b>Important non-goals.</b> This service does not own the
/// transport-level fallback envelopes
/// (<c>Array.Empty&lt;object&gt;()</c> for <c>list</c>/<c>detect</c>,
/// <c>EmptyConflict()</c> for <c>get_context</c>/<c>dismiss</c>/<c>reopen</c>,
/// <c>EmptyListResponse()</c> for <c>list_reconciliations</c>,
/// <c>EmptyReconciliation()</c> for <c>edit_reconciliation_reason</c>,
/// inline <c>{resolved_cid:Guid.Empty, open_conflicts:[], view:{}}</c>
/// for <c>resolve</c>, inline <c>{ok:false}</c>/<c>{deleted:0|1}</c>
/// for <c>revoke_reconciliation</c>). The dispatcher arm still
/// produces those shapes when this service returns <c>null</c> &mdash;
/// matching the abox pilot decision documented in
/// <c>docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md</c>
/// §2.5.
/// </para>
/// </summary>
public sealed class ConflictApplicationService : IConflictApplicationService
{
    private readonly ConflictService _conflicts;
    private readonly ConflictDetectionOrchestrator _detect;

    public ConflictApplicationService(
        ConflictService conflicts,
        ConflictDetectionOrchestrator detect)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(detect);
        _conflicts = conflicts;
        _detect = detect;
    }

    // ----------------------------------------------------------------------
    // IConflictApplicationService
    // ----------------------------------------------------------------------

    public Task<IReadOnlyList<ConflictOut>?> ListAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<IReadOnlyList<ConflictOut>?>(null);
        }
        var (status, ctype) = ReadConflictFilters(request);
        return _conflicts.ListAsync(
            request.KnowledgeSystemGuid.Value, status ?? "open", ctype, cancellationToken)!;
    }

    public async Task<IReadOnlyList<ConflictOut>?> DetectAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return null;
        }
        var rows = await _detect.DetectAsync(
            request.KnowledgeSystemGuid.Value, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public Task<ConflictContext?> GetContextAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var conflictId))
        {
            return Task.FromResult<ConflictContext?>(null);
        }
        return _conflicts.GetContextAsync(
            request.KnowledgeSystemGuid.Value, conflictId, cancellationToken);
    }

    public Task<ConflictOut?> DismissAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var conflictId))
        {
            return Task.FromResult<ConflictOut?>(null);
        }
        return _conflicts.DismissAsync(
            request.KnowledgeSystemGuid.Value, conflictId,
            request.Actor.UserId, cancellationToken);
    }

    public Task<ConflictOut?> ReopenAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var conflictId))
        {
            return Task.FromResult<ConflictOut?>(null);
        }
        return _conflicts.ReopenAsync(
            request.KnowledgeSystemGuid.Value, conflictId,
            request.Actor.UserId, cancellationToken);
    }

    public Task<ResolveConflictResponse?> ResolveAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var conflictId))
        {
            return Task.FromResult<ResolveConflictResponse?>(null);
        }
        var body = DeserializeBody<ResolveConflictRequest>(request);
        // Mirrors the dispatcher's old behaviour: a missing / empty body
        // throws InvalidOperationException (the dispatcher arm relied on
        // the thrown exception bubbling up to FastApiErrorMiddleware →
        // HTTP 400). We re-throw here so the wire shape stays identical.
        if (body is null || string.IsNullOrEmpty(body.ResolutionId))
        {
            throw new InvalidOperationException(
                "Request body with resolution_id is required for conflicts.resolve.");
        }
        return _conflicts.ResolveAsync(
            request.KnowledgeSystemGuid.Value, conflictId,
            body.ResolutionId, request.Actor.UserId, cancellationToken);
    }

    public Task<ReconciliationListResponse> ListReconciliationsAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult(EmptyReconciliationListResponse());
        }
        var q = QueryString(request, "q");
        var limit = QueryInt(request, "limit", 50);
        var offset = QueryInt(request, "offset", 0);
        return _conflicts.ListReconciliationsAsync(
            request.KnowledgeSystemGuid.Value, q, limit, offset, cancellationToken);
    }

    private static ReconciliationListResponse EmptyReconciliationListResponse() =>
        new(Array.Empty<ISEStudio.Application.Conflicts.ReconciliationOut>(), 0);

    public Task<Guid?> RevokeReconciliationAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var reconciliationId))
        {
            return Task.FromResult<Guid?>(null);
        }
        return _conflicts.RevokeReconciliationAsync(
            request.KnowledgeSystemGuid.Value, reconciliationId,
            request.Actor.UserId, cancellationToken);
    }

    public Task<(Guid Id, string Reason)?> EditReconciliationReasonAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var reconciliationId))
        {
            return Task.FromResult<(Guid Id, string Reason)?>(null);
        }
        var body = DeserializeBody<EditReconciliationReasonRequest>(request);
        var reason = body?.Reason ?? string.Empty;
        return _conflicts.EditReconciliationReasonAsync(
            request.KnowledgeSystemGuid.Value, reconciliationId,
            reason, request.Actor.UserId, cancellationToken);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Read the <c>status</c> + <c>ctype</c> query parameters the
    /// conflicts list endpoint understands. Mirrors the original
    /// <c>ReadConflictFilters</c> private static on the dispatcher.
    /// </summary>
    private static (string? status, string? ctype) ReadConflictFilters(InternalRequest request) =>
        (QueryString(request, "status"), QueryString(request, "ctype"));
}
