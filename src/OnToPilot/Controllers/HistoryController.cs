using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/history*</c> surface &mdash;
/// workspace audit log + per-event rollback.
/// </summary>
[ApiController]
[Authorize]
public sealed class HistoryController : InternalControllerBase
{
    public HistoryController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/history")]
    public Task<IActionResult> GetAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("history.get", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/history/{event_id}/rollback")]
    public Task<IActionResult> RollbackAsync(long ks_id, string event_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("history.rollback", ReqWithBody(body, ks: ks_id, res: event_id), ct);
}