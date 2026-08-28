using ISEStudio.Application.Foundation;
using ISEStudio.Application.History;
using ISEStudio.Application.Integration;
using ISEStudio.Ontology;

namespace ISEStudio.Integration;

/// <summary>
/// Implementation of <see cref="IHistoryApplicationService"/>.
/// Unpacks each <see cref="InternalRequest"/> (path / query / body /
/// actor), delegates to <see cref="HistoryService"/> for both arms,
/// and returns the strongly-typed DTO the dispatcher serialises.
///
/// <para>The extraction guard (<c>RunWithExtractionGuardAsync</c>) and
/// the schema-compatible empty payload fallback envelopes
/// (<c>EmptyListResponse()</c> / <c>EmptyKnowledgeSystem()</c>) all
/// live on the dispatcher arm layer &mdash; the application service
/// is a thin envelope-unpacking shim.</para>
///
/// <para>The query tuple (<c>category</c> / <c>q</c> /
/// <c>limit</c> / <c>offset</c>) is parsed via
/// <see cref="InternalRequestHelpers.QueryString"/> so the
/// dispatcher-side helper stays the single source of truth for query
/// string parsing (unlike the resolution slice which copies the
/// paging helper into the application service &mdash; history reads
/// the four keys individually instead of returning a tuple, so the
/// shared helper is the cleaner fit).</para>
/// </summary>
public sealed class HistoryApplicationService : IHistoryApplicationService
{
    private readonly HistoryService _history;

    public HistoryApplicationService(HistoryService history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
    }

    public async Task<HistoryResponseOut?> ListAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var cat = InternalRequestHelpers.QueryString(request, "category");
        var q = InternalRequestHelpers.QueryString(request, "q");
        var limit = int.TryParse(InternalRequestHelpers.QueryString(request, "limit"), out var l) ? l : 50;
        var offset = int.TryParse(InternalRequestHelpers.QueryString(request, "offset"), out var o) ? o : 0;
        return await _history.ListHistoryAsync(
            request.KnowledgeSystemGuid.Value, request.Actor,
            cat, q, limit, offset, ct).ConfigureAwait(false);
    }

    public async Task<RollbackResponseOut?> RollbackAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null || string.IsNullOrEmpty(request.ResourceId))
            return null;
        if (!Guid.TryParse(request.ResourceId, out var eventId))
        {
            // Same 404 contract as the dispatcher version: a malformed
            // event id is a KeyNotFoundException so the FastApiErrorMiddleware
            // surfaces it as HTTP 404 (not a silent empty envelope).
            throw new KeyNotFoundException("History event not found");
        }
        return await _history.RollbackAsync(
            request.KnowledgeSystemGuid.Value, eventId, request.Actor, ct)
            .ConfigureAwait(false);
    }
}