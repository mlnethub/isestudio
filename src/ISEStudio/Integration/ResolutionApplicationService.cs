using System.Text.Json;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Application.Resolution;
using ISEStudio.EntityResolution;
using ISEStudio.Infrastructure.Persistence;

namespace ISEStudio.Integration;

/// <summary>
/// Implementation of <see cref="IResolutionApplicationService"/>.
/// Unpacks each <see cref="InternalRequest"/> (path / query / body /
/// actor), delegates to <see cref="ResolutionService"/> for all five
/// arms, and returns the strongly-typed DTO the dispatcher serialises.
///
/// <para>The extraction guard
/// (<c>RunWithExtractionGuardAsync</c>) and the schema-compatible
/// empty payload fallback envelopes
/// (<c>EmptyListResponse()</c> / <c>EmptyResolutionDecision()</c> /
/// <c>{revoked: 0}</c>) all live on the dispatcher arm layer &mdash;
/// the application service is a thin envelope-unpacking shim.</para>
///
/// <para>The three mutation arms resolve
/// <c>request.ResourceId</c> (the public id of the resolution row)
/// to the Guid primary key via
/// <see cref="ResolutionService.ResolveResRowGuidAsync"/>, the same
/// pattern the dispatcher used inline. The static helper is reused
/// rather than re-implemented so the resolution / legacy-id lookup
/// rules stay in one place.</para>
/// </summary>
public sealed class ResolutionApplicationService : IResolutionApplicationService
{
    private readonly ResolutionService _resolution;
    private readonly ISEStudioDbContext _db;

    public ResolutionApplicationService(
        ResolutionService resolution,
        ISEStudioDbContext db)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(db);
        _resolution = resolution;
        _db = db;
    }

    // -----------------------------------------------------------------
    // resolution.get_queue / resolution.list_decisions — read arms
    // -----------------------------------------------------------------

    public async Task<ResolutionQueueEnvelope?> ListQueueAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var (q, limit, offset) = ReadResolutionPaging(request);
        return await _resolution.ListQueueAsync(
            request.KnowledgeSystemGuid.Value, q, limit, offset, request.Actor, ct)
            .ConfigureAwait(false);
    }

    public async Task<ResolutionDecisionsEnvelope?> ListDecisionsAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var (q, limit, offset) = ReadResolutionPaging(request);
        return await _resolution.ListDecisionsAsync(
            request.KnowledgeSystemGuid.Value, q, limit, offset, request.Actor, ct)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // resolution.resolve / resolution.revoke_decision /
    // resolution.edit_decision_reason — mutation arms
    // -----------------------------------------------------------------

    public async Task<ResolutionDecisionOut?> ResolveAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null || string.IsNullOrEmpty(request.ResourceId))
            return null;
        var body = DeserializeBody<ResolutionResolveIn>(request);
        var rowId = await ResolutionService.ResolveResRowGuidAsync(
            _db, request.KnowledgeSystemGuid.Value, request.ResourceId, ct)
            .ConfigureAwait(false);
        if (rowId is null) return null;
        return await _resolution.ResolveAsync(
            request.KnowledgeSystemGuid.Value, rowId.Value,
            body?.Action ?? string.Empty, body?.IndividualIri,
            request.Actor, ct).ConfigureAwait(false);
    }

    public async Task<Guid?> RevokeDecisionAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null || string.IsNullOrEmpty(request.ResourceId))
            return null;
        var rowId = await ResolutionService.ResolveResRowGuidAsync(
            _db, request.KnowledgeSystemGuid.Value, request.ResourceId, ct)
            .ConfigureAwait(false);
        if (rowId is null) return null;
        // Phase 3: legacy_id 列已退役; return Guid PK as the revoked
        // identifier. Wire shape changes from int64 to guid string.
        // The no-op case (RevokeAsync returned false) collapses into
        // the same null envelope as the not-found case — the dispatcher
        // distinguishes by mapping null to `{revoked: 0}` and a Guid
        // to `{revoked: guid.ToString()}`.
        var ok = await _resolution.RevokeAsync(
            request.KnowledgeSystemGuid.Value, rowId.Value, request.Actor, ct)
            .ConfigureAwait(false);
        return ok ? rowId : null;
    }

    public async Task<ResolutionDecisionOut?> EditDecisionReasonAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null || string.IsNullOrEmpty(request.ResourceId))
            return null;
        var body = DeserializeBody<ResolutionEditReasonIn>(request);
        var rowId = await ResolutionService.ResolveResRowGuidAsync(
            _db, request.KnowledgeSystemGuid.Value, request.ResourceId, ct)
            .ConfigureAwait(false);
        if (rowId is null) return null;
        return await _resolution.EditReasonAsync(
            request.KnowledgeSystemGuid.Value, rowId.Value,
            body?.Reason, request.Actor, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Local helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Parse the <c>q</c> / <c>limit</c> / <c>offset</c> query tuple
    /// used by both resolution read arms. Defaults:
    /// <c>q=null</c>, <c>limit=50</c>, <c>offset=0</c>. The 50-row
    /// page matches the Python <c>resolution.py</c> default and the
    /// 1..200 clamp mirrors the service-layer cap.
    /// </summary>
    private static (string? Query, int Limit, int Offset) ReadResolutionPaging(
        InternalRequest request)
    {
        string? q = null;
        int limit = 50;
        int offset = 0;
        if (request.Query is not null)
        {
            if (request.Query.TryGetValue("q", out var qv) && !string.IsNullOrEmpty(qv))
                q = qv;
            if (request.Query.TryGetValue("limit", out var lv)
                && int.TryParse(lv, out var lp)) limit = lp;
            if (request.Query.TryGetValue("offset", out var ov)
                && int.TryParse(ov, out var op)) offset = op;
        }
        return (q, limit, offset);
    }

    private static T? DeserializeBody<T>(InternalRequest request) where T : class
    {
        if (request.Body is null) return null;
        if (!request.Body.TryGetValue("_", out var raw) || raw is null) return null;
        if (raw is T typed) return typed;
        if (raw is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(),
                InternalRequestHelpers.DeserializeOptions);
        }
        return null;
    }
}