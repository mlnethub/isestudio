using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/prompts*</c> surface &mdash;
/// per-knowledge-system prompt overrides (LLM instruction templates).
/// </summary>
[ApiController]
[Authorize]
public sealed class PromptsController : InternalControllerBase
{
    public PromptsController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/prompts")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => InvokeAsync("prompts.list", ReqGuid(id), ct);

    // FastAPI declares this endpoint as 204 No Content; the dispatcher
    // still has to be invoked (the work has to happen server-side) but
    // the controller intentionally returns NoContent() so the wire shape
    // matches the documented envelope exactly.
    [HttpPost("api/knowledge/{id:guid}/prompts/restore-all")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public async Task<IActionResult> RestoreAllAsync(Guid id, CancellationToken ct)
    {
        await Facade.InvokeAsync("prompts.restore_all", ReqGuid(id), ct).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("api/knowledge/{id:guid}/prompts/{prompt_key}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RestoreAsync(Guid id, string prompt_key, CancellationToken ct)
        => InvokeAsync("prompts.restore", ReqGuid(ks: id, res: prompt_key), ct);

    [HttpPut("api/knowledge/{id:guid}/prompts/{prompt_key}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> UpdateAsync(Guid id, string prompt_key, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("prompts.update", ReqGuidWithBody(body, id, res: prompt_key), ct);
}
