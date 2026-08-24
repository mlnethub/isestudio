using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/history*</c> surface &mdash;
/// workspace audit log + per-event rollback.
/// </summary>
[ApiController]
[Authorize]
public sealed class HistoryController : InternalControllerBase
{
    public HistoryController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/history")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
        => InvokeAsync("history.get", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/history/{event_id}/rollback")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> RollbackAsync(Guid id, string event_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("history.rollback", ReqGuidWithBody(body, id, res: event_id), ct);
}
