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

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.delete", ReqGuid(id), ct);

    [HttpGet("{id:guid}")]
    public Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.get", ReqGuid(id), ct);

    [HttpPatch("{id:guid}")]
    public Task<IActionResult> UpdateAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.update", ReqGuidWithBody(body, id), ct);

    [HttpGet("{id:guid}/members")]
    public Task<IActionResult> ListMembersAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.list_members", ReqGuid(id), ct);

    [HttpPost("{id:guid}/members")]
    public Task<IActionResult> AddMemberAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("knowledge.add_member", ReqGuidWithBody(body, id), ct);

    [HttpGet("{id:guid}/members/candidates")]
    public Task<IActionResult> GrantableUsersAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.grantable_users", ReqGuid(id), ct);

    [HttpDelete("{id:guid}/members/{user_id}")]
    public Task<IActionResult> RemoveMemberAsync(Guid id, string user_id, CancellationToken ct)
        => InvokeAsync("knowledge.remove_member", ReqGuid(id, res: user_id), ct);

    [HttpGet("{id:guid}/members/{user_id}/detail")]
    public Task<IActionResult> MemberDetailAsync(Guid id, string user_id, CancellationToken ct)
        => InvokeAsync("knowledge.member_detail", ReqGuid(id, res: user_id), ct);

    [HttpGet("{id:guid}/review/counts")]
    public Task<IActionResult> ReviewCountsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("knowledge.review_counts", ReqGuid(id), ct);
}
