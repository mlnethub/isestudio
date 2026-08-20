using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/resolution*</c> surface &mdash;
/// structured TBox conflict resolution queue.
/// </summary>
[ApiController]
[Authorize]
public sealed class ResolutionController : InternalControllerBase
{
    public ResolutionController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/resolution/decisions")]
    public Task<IActionResult> ListDecisionsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("resolution.list_decisions", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/resolution/decisions/{res_id}")]
    public Task<IActionResult> RevokeDecisionAsync(Guid id, string res_id, CancellationToken ct)
        => InvokeAsync("resolution.revoke_decision", ReqGuid(ks: id, res: res_id), ct);

    [HttpPatch("api/knowledge/{id:guid}/resolution/decisions/{res_id}")]
    public Task<IActionResult> EditDecisionReasonAsync(Guid id, string res_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("resolution.edit_decision_reason", ReqGuidWithBody(body, id, res: res_id), ct);

    [HttpGet("api/knowledge/{id:guid}/resolution/queue")]
    public Task<IActionResult> GetQueueAsync(Guid id, CancellationToken ct)
        => InvokeAsync("resolution.get_queue", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/resolution/{res_id}/resolve")]
    public Task<IActionResult> ResolveAsync(Guid id, string res_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("resolution.resolve", ReqGuidWithBody(body, id, res: res_id), ct);
}