using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal settings surface &mdash; global app configuration (LLM
/// defaults, language, available models) plus the <c>/api/models</c>
/// listing used by the admin UI.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
public sealed class SettingsController : InternalControllerBase
{
    public SettingsController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/models")]
    public Task<IActionResult> ListModelsAsync(CancellationToken ct)
        => InvokeAsync("settings.list_models", Req(), ct);

    [HttpGet("api/settings")]
    public Task<IActionResult> GetAsync(CancellationToken ct)
        => InvokeAsync("settings.get", Req(), ct);

    [HttpPut("api/settings")]
    public Task<IActionResult> UpdateAsync([FromBody] object body, CancellationToken ct)
        => InvokeAsync("settings.update", ReqWithBody(body), ct);
}