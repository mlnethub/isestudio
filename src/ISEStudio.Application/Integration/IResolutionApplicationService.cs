using ISEStudio.Application.Foundation;
using ISEStudio.Application.Resolution;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-side contract for the five <c>resolution.*</c>
/// dispatcher arms. Each method takes an <see cref="InternalRequest"/>
/// envelope (path / query / body / actor) and returns either the
/// strongly-typed DTO the dispatcher serialises, or <c>null</c> when
/// the knowledge system / resolution row id can't be resolved.
///
/// <para>The dispatcher arm layer retains the schema-compatible empty
/// payload fallback envelopes (<c>EmptyListResponse()</c> /
/// <c>EmptyResolutionDecision()</c> / <c>{revoked: 0}</c>) &mdash; the
/// application service returns <c>null</c> and the dispatcher
/// substitutes the right shape. See
/// <c>docs/superpowers/specs/2026-08-28-resolution-application-service.md</c>
/// §3.3 for the wrapper pattern.</para>
///
/// <para>The three mutation arms (<c>resolution.resolve</c> /
/// <c>resolution.revoke_decision</c> /
/// <c>resolution.edit_decision_reason</c>) are wrapped in
/// <c>RunWithExtractionGuardAsync</c> by the dispatcher switch arm,
/// matching the brief's "抽取进行中的修改返回 409" requirement &mdash;
/// the application service throws no guard of its own.</para>
///
/// <para>The two read arms (<c>resolution.get_queue</c> /
/// <c>resolution.list_decisions</c>) parse the same
/// <c>q</c> / <c>limit</c> / <c>offset</c> query tuple via the
/// <see cref="ResolutionApplicationService.ReadResolutionPaging"/>
/// helper that lives on the implementation.</para>
/// </summary>
public interface IResolutionApplicationService
{
    /// <summary>
    /// <c>resolution.get_queue</c> &mdash; every queued resolution
    /// row for the bound knowledge system, paginated via
    /// <c>q</c> / <c>limit</c> / <c>offset</c> query parameters.
    /// Returns <c>null</c> when no KS id is bound (dispatcher maps to
    /// <c>EmptyListResponse()</c>).
    /// </summary>
    Task<ResolutionQueueEnvelope?> ListQueueAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>resolution.list_decisions</c> &mdash; every resolved /
    /// dismissed decision for the bound knowledge system, paginated
    /// via the same <c>q</c> / <c>limit</c> / <c>offset</c> query
    /// tuple. Returns <c>null</c> when no KS id is bound (dispatcher
    /// maps to <c>EmptyListResponse()</c>).
    /// </summary>
    Task<ResolutionDecisionsEnvelope?> ListDecisionsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>resolution.resolve</c> &mdash; apply a single resolution
    /// decision (<c>match</c> / <c>new</c> / etc.) to the resolution
    /// row identified by <c>request.ResourceId</c>. Returns
    /// <c>null</c> when the KS id / resource id / row id can't be
    /// resolved (dispatcher maps to <c>EmptyResolutionDecision()</c>).
    /// </summary>
    Task<ResolutionDecisionOut?> ResolveAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>resolution.revoke_decision</c> &mdash; revert a previously
    /// applied resolution decision. Returns the revoked resolution
    /// row's Guid PK on success (dispatcher projects to
    /// <c>{revoked: "guid"}</c>); returns <c>null</c> when the KS id /
    /// resource id / row id can't be resolved OR when the underlying
    /// <see cref="ResolutionService.RevokeAsync"/> returns false
    /// (no-op). The dispatcher maps the <c>null</c> case to
    /// <c>{revoked: 0}</c>.
    /// </summary>
    Task<Guid?> RevokeDecisionAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>resolution.edit_decision_reason</c> &mdash; patch the
    /// <c>reason</c> field on a previously applied decision. Returns
    /// <c>null</c> when the KS id / resource id / row id can't be
    /// resolved (dispatcher maps to <c>EmptyResolutionDecision()</c>).
    /// </summary>
    Task<ResolutionDecisionOut?> EditDecisionReasonAsync(InternalRequest request, CancellationToken cancellationToken);
}