using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

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
    public Task<IActionResult> ListExportsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("releases.list_exports", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/exports")]
    public Task<IActionResult> CreateExportAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.create_export", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/exports/{job_id}")]
    public Task<IActionResult> GetExportAsync(Guid id, string job_id, CancellationToken ct)
        => InvokeAsync("releases.get_export", ReqGuid(ks: id, res: job_id), ct);

    [HttpGet("api/knowledge/{id:guid}/exports/{job_id}/files/{filename}")]
    public Task<IActionResult> DownloadExportFileAsync(Guid id, string job_id, string filename, CancellationToken ct)
        => InvokeAsync("releases.download_export_file", ReqGuid(ks: id, res: job_id, res2: filename), ct);

    // ---- releases ----

    [HttpGet("api/knowledge/{id:guid}/releases")]
    public Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => InvokeAsync("releases.list", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases")]
    public Task<IActionResult> CreateAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.create", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/releases/diff")]
    public Task<IActionResult> DiffAsync(Guid id, CancellationToken ct)
        => InvokeAsync("releases.diff", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/releases/{release_id}")]
    public Task<IActionResult> DeleteAsync(Guid id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.delete", ReqGuid(ks: id, res: release_id), ct);

    [HttpDelete("api/knowledge/{id:guid}/releases/{release_id}/deployment")]
    public Task<IActionResult> StopDeploymentAsync(Guid id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.stop_deployment", ReqGuid(ks: id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/deployment")]
    public Task<IActionResult> DeployAsync(Guid id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.deploy", ReqGuid(ks: id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/publish")]
    public Task<IActionResult> PublishAsync(Guid id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.publish", ReqGuidWithBody(body, id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/review")]
    public Task<IActionResult> ReviewAsync(Guid id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.review", ReqGuidWithBody(body, id, res: release_id), ct);

    [HttpPost("api/knowledge/{id:guid}/releases/{release_id}/rollback")]
    public Task<IActionResult> RollbackAsync(Guid id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.rollback", ReqGuidWithBody(body, id, res: release_id), ct);
}