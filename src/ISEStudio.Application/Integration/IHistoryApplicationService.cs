using ISEStudio.Application.Foundation;
using ISEStudio.Application.History;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-side contract for the two <c>history.*</c> dispatcher
/// arms. Each method takes an <see cref="InternalRequest"/> envelope
/// (path / query / body / actor) and returns either the strongly-typed
/// DTO the dispatcher serialises, or <c>null</c> when the knowledge
/// system / event id can't be resolved.
///
/// <para>The dispatcher arm layer retains the schema-compatible empty
/// payload fallback envelopes (<c>EmptyListResponse()</c> /
/// <c>EmptyKnowledgeSystem()</c>) &mdash; the application service
/// returns <c>null</c> and the dispatcher substitutes the right
/// shape. See
/// <c>docs/superpowers/specs/2026-08-28-history-application-service.md</c>
/// §3.3 for the wrapper pattern.</para>
///
/// <para>The <c>history.rollback</c> arm is wrapped in
/// <c>RunWithExtractionGuardAsync</c> by the dispatcher switch arm,
/// matching the brief's "抽取进行中的修改返回 409" requirement &mdash;
/// the application service throws no guard of its own.</para>
/// </summary>
public interface IHistoryApplicationService
{
    /// <summary>
    /// <c>history.get</c> &mdash; every audit event for the bound
    /// knowledge system, paginated via <c>category</c> / <c>q</c> /
    /// <c>limit</c> / <c>offset</c> query parameters (newest first).
    /// Returns <c>null</c> when no KS id is bound (dispatcher maps to
    /// <c>EmptyListResponse()</c>).
    /// </summary>
    Task<HistoryResponseOut?> ListAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>history.rollback</c> &mdash; apply the reverse-delta of one
    /// audit event back onto the named graphs. Returns <c>null</c>
    /// when the KS id / event id can't be resolved (dispatcher maps
    /// to <c>EmptyKnowledgeSystem()</c>). On success returns the
    /// rolled-back count + the post-rollback ontology view + the
    /// freshly synced open-conflict list (typed envelope).
    /// </summary>
    Task<RollbackResponseOut?> RollbackAsync(InternalRequest request, CancellationToken cancellationToken);
}