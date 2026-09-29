using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Sources;

namespace ISEStudio.Controllers;

[ApiController]
[Authorize]
[Route("api/knowledge/{id:guid}/ingestion-sources")]
public sealed class IngestionSourcesController : ControllerBase
{
    private readonly SourceService _sources;

    public IngestionSourcesController(SourceService sources) => _sources = sources;

    [HttpGet]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public async Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => ToActionResult(await _sources.ListAsync(id, Actor(), ct).ConfigureAwait(false));

    [HttpGet("kinds")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public async Task<IActionResult> KindsAsync(Guid id, CancellationToken ct)
        => ToActionResult(await _sources.KindsAsync(id, Actor(), ct).ConfigureAwait(false));

    [HttpGet("{sourceId:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public async Task<IActionResult> GetAsync(Guid id, Guid sourceId, CancellationToken ct)
        => ToActionResult(await _sources.GetAsync(id, sourceId, Actor(), ct).ConfigureAwait(false));

    [HttpPost]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public async Task<IActionResult> CreateAsync(Guid id, [FromBody] SourceUpsertRequest request, CancellationToken ct)
        => ToActionResult(await _sources.CreateAsync(id, request, Actor(), ct).ConfigureAwait(false));

    [HttpPatch("{sourceId:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public async Task<IActionResult> UpdateAsync(
        Guid id, Guid sourceId, [FromBody] SourceUpsertRequest request, CancellationToken ct)
        => ToActionResult(await _sources.UpdateAsync(id, sourceId, request, Actor(), ct).ConfigureAwait(false));

    [HttpDelete("{sourceId:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public async Task<IActionResult> DeleteAsync(Guid id, Guid sourceId, CancellationToken ct)
        => ToActionResult(await _sources.DeleteAsync(id, sourceId, Actor(), ct).ConfigureAwait(false));

    [HttpPost("{sourceId:guid}/token/rotate")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public async Task<IActionResult> RotateTokenAsync(Guid id, Guid sourceId, CancellationToken ct)
        => ToTokenResult(await _sources.RotateTokenAsync(id, sourceId, Actor(), ct).ConfigureAwait(false));

    [HttpPost("{sourceId:guid}/token/reveal")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public async Task<IActionResult> RevealTokenAsync(Guid id, Guid sourceId, CancellationToken ct)
        => ToTokenResult(await _sources.RevealTokenAsync(id, sourceId, Actor(), ct).ConfigureAwait(false));

    private UserEntity Actor()
        => HttpContext.Items.TryGetValue("auth.user", out var value) && value is UserEntity actor
            ? actor
            : throw new UnauthorizedAccessException("Not authenticated");

    private IActionResult ToActionResult<T>(SourceMutationResult<T> result)
    {
        if (result.StatusCode == StatusCodes.Status204NoContent) return NoContent();
        if (result.Error is not null) return StatusCode(result.StatusCode, new { detail = result.Error });
        if (result.StatusCode == StatusCodes.Status201Created) return StatusCode(result.StatusCode, result.Value);
        return Ok(result.Value);
    }

    private IActionResult ToTokenResult(SourceMutationResult<SourceTokenOut> result)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return ToActionResult(result);
    }
}