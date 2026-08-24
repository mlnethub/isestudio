using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/providers*</c> surface &mdash; LLM provider registration,
/// edit, deletion, and connectivity test.
/// </summary>
[ApiController]
[Route("api/providers")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class ProvidersController : InternalControllerBase
{
    public ProvidersController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("")]
    public Task<IActionResult> ListAsync(CancellationToken ct)
        => InvokeAsync("providers.list", Req(), ct);

    [HttpPost("")]
    public Task<IActionResult> CreateAsync([FromBody] object body, CancellationToken ct)
        => InvokeAsync("providers.create", ReqWithBody(body), ct);

    [HttpPost("test")]
    public Task<IActionResult> TestAsync([FromBody] object body, CancellationToken ct)
        => InvokeAsync("providers.test", ReqWithBody(body), ct);

    [HttpDelete("{pid}")]
    public Task<IActionResult> DeleteAsync(string pid, CancellationToken ct)
        => InvokeAsync("providers.delete", Req(res: pid), ct);

    [HttpPatch("{pid}")]
    public Task<IActionResult> UpdateAsync(string pid, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("providers.update", ReqWithBody(body, res: pid), ct);
}