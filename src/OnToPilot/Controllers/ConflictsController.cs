using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/conflicts*</c> surface &mdash;
/// conflict detection, dismissal, resolution, reconciliation history.
/// </summary>
[ApiController]
[Authorize]
public sealed class ConflictsController : InternalControllerBase
{
    public ConflictsController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/conflicts")]
    public Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => InvokeAsync("conflicts.list", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/conflicts/detect")]
    public Task<IActionResult> DetectAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("conflicts.detect", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/conflicts/{cid}")]
    public Task<IActionResult> GetContextAsync(Guid id, string cid, CancellationToken ct)
        => InvokeAsync("conflicts.get_context", ReqGuid(id, res: cid), ct);

    [HttpPost("api/knowledge/{id:guid}/conflicts/{cid}/dismiss")]
    public Task<IActionResult> DismissAsync(Guid id, string cid, CancellationToken ct)
        => InvokeAsync("conflicts.dismiss", ReqGuid(id, res: cid), ct);

    [HttpPost("api/knowledge/{id:guid}/conflicts/{cid}/reopen")]
    public Task<IActionResult> ReopenAsync(Guid id, string cid, CancellationToken ct)
        => InvokeAsync("conflicts.reopen", ReqGuid(id, res: cid), ct);

    [HttpPost("api/knowledge/{id:guid}/conflicts/{cid}/resolve")]
    public Task<IActionResult> ResolveAsync(Guid id, string cid, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("conflicts.resolve", ReqGuidWithBody(body, id, res: cid), ct);

    [HttpGet("api/knowledge/{id:guid}/reconciliations")]
    public Task<IActionResult> ListReconciliationsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("conflicts.list_reconciliations", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/reconciliations/{rid}")]
    public Task<IActionResult> RevokeReconciliationAsync(Guid id, string rid, CancellationToken ct)
        => InvokeAsync("conflicts.revoke_reconciliation", ReqGuid(id, res: rid), ct);

    [HttpPatch("api/knowledge/{id:guid}/reconciliations/{rid}")]
    public Task<IActionResult> EditReconciliationReasonAsync(Guid id, string rid, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("conflicts.edit_reconciliation_reason", ReqGuidWithBody(body, id, res: rid), ct);
}
