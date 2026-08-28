using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Knowledge;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the twelve <c>knowledge.*</c> dispatcher
/// arms (12/13 slice). Unpacks the <see cref="InternalRequest"/>
/// envelope (body DTOs, KnowledgeSystemGuid, ResourceId user Guid),
/// delegates to the scoped <see cref="KnowledgeService"/>, and returns
/// the wire DTO or <c>null</c> for the dispatcher's per-arm fallback.
/// Missing body throws <see cref="InvalidOperationException"/> exactly
/// like the pre-split helpers.
/// </summary>
public sealed class KnowledgeApplicationService : IKnowledgeApplicationService
{
    private readonly KnowledgeService _knowledge;

    public KnowledgeApplicationService(KnowledgeService knowledge)
    {
        _knowledge = knowledge;
    }

    public async Task<object?> ListAsync(
        InternalRequest request, CancellationToken ct)
    {
        var rows = await _knowledge.ListAsync(request.Actor, ct).ConfigureAwait(false);
        return (object?)rows;
    }

    public async Task<object?> CreateAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<CreateKnowledgeSystemRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for knowledge.create.");
        var row = await _knowledge.CreateAsync(body, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)row;
    }

    public async Task<object?> DeleteAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var deleted = await _knowledge.DeleteAsync(
                request.KnowledgeSystemGuid.Value, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)new { deleted = deleted ?? Guid.Empty };
    }

    public async Task<object?> GetAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var row = await _knowledge.GetAsync(
                request.KnowledgeSystemGuid.Value, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)row;
    }

    public async Task<object?> UpdateAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<UpdateKnowledgeSystemRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for knowledge.update.");
        if (request.KnowledgeSystemGuid is null) return null;
        var row = await _knowledge.UpdateAsync(
                request.KnowledgeSystemGuid.Value, body, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)row;
    }

    public async Task<object?> ListMembersAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var rows = await _knowledge.ListMembersAsync(
                request.KnowledgeSystemGuid.Value, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)rows;
    }

    public async Task<object?> AddMemberAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<AddMemberRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for knowledge.add_member.");
        if (request.KnowledgeSystemGuid is null) return null;
        var rows = await _knowledge.AddMemberAsync(
                request.KnowledgeSystemGuid.Value, body, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)rows;
    }

    public async Task<object?> GrantableUsersAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var query = request.Query is not null && request.Query.TryGetValue("q", out var q)
            ? q : null;
        var rows = await _knowledge.GrantableUsersAsync(
                request.KnowledgeSystemGuid.Value, query, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)rows;
    }

    public async Task<object?> RemoveMemberAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var userId))
        {
            return null;
        }
        var removed = await _knowledge.RemoveMemberAsync(
                request.KnowledgeSystemGuid.Value, userId, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)new { removed = removed ?? Guid.Empty };
    }

    public async Task<object?> MemberDetailAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var userId))
        {
            return null;
        }
        var detail = await _knowledge.MemberDetailAsync(
                request.KnowledgeSystemGuid.Value, userId, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)detail;
    }

    public async Task<object?> ReviewCountsAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var counts = await _knowledge.ReviewCountsAsync(
                request.KnowledgeSystemGuid.Value, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)counts;
    }

    public async Task<object?> RefreshStatsAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var row = await _knowledge.RefreshStatsAsync(
                request.KnowledgeSystemGuid.Value, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)new { refreshed = true, item = row };
    }
}
