using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

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

    [HttpGet("")]
    public Task<IActionResult> ListAsync(CancellationToken ct)
        => InvokeAsync("knowledge.list", Req(), ct);

    [HttpPost("")]
    public Task<IActionResult> CreateAsync([FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.create", ReqWithBody(body), ct);

    [HttpDelete("{ks_id:long}")]
    public Task<IActionResult> DeleteAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("knowledge.delete", Req(ks: ks_id), ct);

    [HttpGet("{ks_id:long}")]
    public Task<IActionResult> GetAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("knowledge.get", Req(ks: ks_id), ct);

    [HttpPatch("{ks_id:long}")]
    public Task<IActionResult> UpdateAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.update", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("{ks_id:long}/members")]
    public Task<IActionResult> ListMembersAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("knowledge.list_members", Req(ks: ks_id), ct);

    [HttpPost("{ks_id:long}/members")]
    public Task<IActionResult> AddMemberAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.add_member", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("{ks_id:long}/members/candidates")]
    public Task<IActionResult> GrantableUsersAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("knowledge.grantable_users", Req(ks: ks_id), ct);

    [HttpDelete("{ks_id:long}/members/{user_id}")]
    public Task<IActionResult> RemoveMemberAsync(long ks_id, string user_id, CancellationToken ct)
        => InvokeAsync("knowledge.remove_member", Req(ks: ks_id, res: user_id), ct);

    [HttpGet("{ks_id:long}/members/{user_id}/detail")]
    public Task<IActionResult> MemberDetailAsync(long ks_id, string user_id, CancellationToken ct)
        => InvokeAsync("knowledge.member_detail", Req(ks: ks_id, res: user_id), ct);

    [HttpGet("{ks_id:long}/review/counts")]
    public Task<IActionResult> ReviewCountsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("knowledge.review_counts", Req(ks: ks_id), ct);
}