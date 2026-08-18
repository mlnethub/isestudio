using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/conflicts*</c> surface &mdash;
/// conflict detection, dismissal, resolution, reconciliation history.
/// </summary>
[ApiController]
[Authorize]
public sealed class ConflictsController : InternalControllerBase
{
    public ConflictsController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/conflicts")]
    public Task<IActionResult> ListAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("conflicts.list", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/conflicts/detect")]
    public Task<IActionResult> DetectAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("conflicts.detect", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/conflicts/{cid}")]
    public Task<IActionResult> GetContextAsync(long ks_id, string cid, CancellationToken ct)
        => InvokeAsync("conflicts.get_context", Req(ks: ks_id, res: cid), ct);

    [HttpPost("api/knowledge/{ks_id:long}/conflicts/{cid}/dismiss")]
    public Task<IActionResult> DismissAsync(long ks_id, string cid, CancellationToken ct)
        => InvokeAsync("conflicts.dismiss", Req(ks: ks_id, res: cid), ct);

    [HttpPost("api/knowledge/{ks_id:long}/conflicts/{cid}/reopen")]
    public Task<IActionResult> ReopenAsync(long ks_id, string cid, CancellationToken ct)
        => InvokeAsync("conflicts.reopen", Req(ks: ks_id, res: cid), ct);

    [HttpPost("api/knowledge/{ks_id:long}/conflicts/{cid}/resolve")]
    public Task<IActionResult> ResolveAsync(long ks_id, string cid, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("conflicts.resolve", ReqWithBody(body, ks: ks_id, res: cid), ct);

    [HttpGet("api/knowledge/{ks_id:long}/reconciliations")]
    public Task<IActionResult> ListReconciliationsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("conflicts.list_reconciliations", Req(ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/reconciliations/{rid}")]
    public Task<IActionResult> RevokeReconciliationAsync(long ks_id, string rid, CancellationToken ct)
        => InvokeAsync("conflicts.revoke_reconciliation", Req(ks: ks_id, res: rid), ct);

    [HttpPatch("api/knowledge/{ks_id:long}/reconciliations/{rid}")]
    public Task<IActionResult> EditReconciliationReasonAsync(long ks_id, string rid, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("conflicts.edit_reconciliation_reason", ReqWithBody(body, ks: ks_id, res: rid), ct);
}