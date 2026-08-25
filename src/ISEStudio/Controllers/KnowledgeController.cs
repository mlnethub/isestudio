using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ISEStudio.Application.Integration;
using ISEStudio.Authorization;

namespace ISEStudio.Controllers;

/// <summary>
/// Internal <c>/api/knowledge</c> surface &mdash; knowledge-system
/// administration (list/create/get/update/delete + membership).
/// </summary>
[ApiController]
[Route("api/knowledge")]
[Authorize]
public sealed class KnowledgeController : InternalControllerBase
{
    public KnowledgeController(IIntegrationApiFacade facade) : base(facade) { }

    // NOTE: ListAsync / CreateAsync carry no [KSRoleAuthorize] — their
    // routes have no {id} parameter (global KS list + KS creation), so the
    // filter's route-based KS resolution cannot apply. See the RBAC
    // coverage matrix task-2 report.

    [HttpGet("")]
    public Task<IActionResult> ListAsync(CancellationToken ct)
        => InvokeAsync("knowledge.list", Req(), ct);

    [HttpPost("")]
    public Task<IActionResult> CreateAsync([FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.create", ReqWithBody(body), ct);

    [HttpDelete("{id:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.delete", ReqGuid(id), ct);

    [HttpGet("{id:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.get", ReqGuid(id), ct);

    [HttpPatch("{id:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> UpdateAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.update", ReqGuidWithBody(body, id), ct);

    [HttpGet("{id:guid}/members")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListMembersAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.list_members", ReqGuid(id), ct);

    [HttpPost("{id:guid}/members")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> AddMemberAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.add_member", ReqGuidWithBody(body, id), ct);

    [HttpGet("{id:guid}/members/candidates")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> GrantableUsersAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.grantable_users", ReqGuid(id), ct);

    [HttpDelete("{id:guid}/members/{user_id}")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> RemoveMemberAsync(Guid id, string user_id, CancellationToken ct)
        => InvokeAsync("knowledge.remove_member", ReqGuid(id, res: user_id), ct);

    [HttpGet("{id:guid}/members/{user_id}/detail")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> MemberDetailAsync(Guid id, string user_id, CancellationToken ct)
        => InvokeAsync("knowledge.member_detail", ReqGuid(id, res: user_id), ct);

    [HttpGet("{id:guid}/review/counts")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ReviewCountsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.review_counts", ReqGuid(id), ct);

    /// <summary>
    /// One-shot repair endpoint that recomputes the cached
    /// <c>ClassCount / PropertyCount / AxiomCount</c> columns from the
    /// live TBox graph. Mirrors Python's
    /// <c>backend/app/mcp_server.py:634</c> call to
    /// <c>refresh_ks_stats</c>. Useful for backfilling KSes that were
    /// created before the mutation-time stats refresh was wired in.
    /// </summary>
    [HttpPost("{id:guid}/refresh_stats")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RefreshStatsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.refresh_stats", ReqGuid(id), ct);
}
