using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/releases*</c> + exports surface &mdash;
/// versioned releases, deployment, review, rollback, export jobs.
/// </summary>
[ApiController]
[Authorize]
public sealed class ReleasesController : InternalControllerBase
{
    public ReleasesController(IIntegrationApiFacade facade) : base(facade) { }

    // ---- exports ----

    [HttpGet("api/knowledge/{id:guid}/exports")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListExportsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("releases.list_exports", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/exports")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> CreateExportAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.create_export", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/exports/{job_id}")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> GetExportAsync(Guid id, string job_id, CancellationToken ct)
        => InvokeAsync("releases.get_export", ReqGuid(ks: id, res: job_id), ct);

    [HttpGet("api/knowledge/{id:guid}/exports/{job_id}/files/{filename}")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> DownloadExportFileAsync(Guid id, string job_id, string filename, CancellationToken ct)
        => InvokeAsync("releases.download_export_file", ReqGuid(ks: id, res: job_id, res2: filename), ct);

    // ---- releases ----

    [HttpGet("api/knowledge/{id:guid}/releases")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => InvokeAsync("releases.list", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> CreateAsync(Guid id, [FromBody] object body, CancellationToken ct)
        // The frontend ReleasePanel.createDraft (frontend/src/lib/api.ts:134)
        // always sends `{}` (an empty object) with Content-Type
        // application/json — matches the working vocabulary.create_scheme
        // surface, so [FromBody] object body is the right binding. The
        // dispatcher tolerates an empty body via its title/notes
        // defensive-read fallback so a future caller that omits the body
        // still degrades to a schema-compatible empty-payload wire
        // shape rather than 415-ing.
        => InvokeAsync("releases.create", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/releases/diff")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> DiffAsync(Guid id, CancellationToken ct)
        => InvokeAsync("releases.diff", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/releases/{release_id}")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> DeleteAsync(Guid id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.delete", ReqGuid(ks: id, res: release_id), ct);

    [HttpDelete("api/knowledge/{id:guid}/releases/{release_id}/deployment")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> StopDeploymentAsync(Guid id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.stop_deployment", ReqGuid(ks: id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/deployment")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> DeployAsync(Guid id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.deploy", ReqGuid(ks: id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/publish")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> PublishAsync(Guid id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.publish", ReqGuidWithBody(body, id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/review")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> ReviewAsync(Guid id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.review", ReqGuidWithBody(body, id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/rollback")]
    [KSRoleAuthorize(Minimum = KSRole.Owner)]
    public Task<IActionResult> RollbackAsync(Guid id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.rollback", ReqGuidWithBody(body, id, res: release_id), ct);
}
