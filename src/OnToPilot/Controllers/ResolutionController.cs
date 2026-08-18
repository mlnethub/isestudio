using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/resolution*</c> surface &mdash;
/// structured TBox conflict resolution queue.
/// </summary>
[ApiController]
[Authorize]
public sealed class ResolutionController : InternalControllerBase
{
    public ResolutionController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/resolution/decisions")]
    public Task<IActionResult> ListDecisionsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("resolution.list_decisions", Req(ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/resolution/decisions/{res_id}")]
    public Task<IActionResult> RevokeDecisionAsync(long ks_id, string res_id, CancellationToken ct)
        => InvokeAsync("resolution.revoke_decision", Req(ks: ks_id, res: res_id), ct);

    [HttpPatch("api/knowledge/{ks_id:long}/resolution/decisions/{res_id}")]
    public Task<IActionResult> EditDecisionReasonAsync(long ks_id, string res_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("resolution.edit_decision_reason", ReqWithBody(body, ks: ks_id, res: res_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/resolution/queue")]
    public Task<IActionResult> GetQueueAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("resolution.get_queue", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/resolution/{res_id}/resolve")]
    public Task<IActionResult> ResolveAsync(long ks_id, string res_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("resolution.resolve", ReqWithBody(body, ks: ks_id, res: res_id), ct);
}