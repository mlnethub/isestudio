using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/prompts*</c> surface &mdash;
/// per-knowledge-system prompt overrides (LLM instruction templates).
/// </summary>
[ApiController]
[Authorize]
public sealed class PromptsController : InternalControllerBase
{
    public PromptsController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/prompts")]
    public Task<IActionResult> ListAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("prompts.list", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/prompts/restore-all")]
    public Task<IActionResult> RestoreAllAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("prompts.restore_all", Req(ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/prompts/{prompt_key}")]
    public Task<IActionResult> RestoreAsync(long ks_id, string prompt_key, CancellationToken ct)
        => InvokeAsync("prompts.restore", Req(ks: ks_id, res: prompt_key), ct);

    [HttpPut("api/knowledge/{ks_id:long}/prompts/{prompt_key}")]
    public Task<IActionResult> UpdateAsync(long ks_id, string prompt_key, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("prompts.update", ReqWithBody(body, ks: ks_id, res: prompt_key), ct);
}