using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/releases*</c> + exports surface &mdash;
/// versioned releases, deployment, review, rollback, export jobs.
/// </summary>
[ApiController]
[Authorize]
public sealed class ReleasesController : InternalControllerBase
{
    public ReleasesController(IIntegrationApiFacade facade) : base(facade) { }

    // ---- exports ----

    [HttpGet("api/knowledge/{ks_id:long}/exports")]
    public Task<IActionResult> ListExportsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("releases.list_exports", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/exports")]
    public Task<IActionResult> CreateExportAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.create_export", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/exports/{job_id}")]
    public Task<IActionResult> GetExportAsync(long ks_id, string job_id, CancellationToken ct)
        => InvokeAsync("releases.get_export", Req(ks: ks_id, res: job_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/exports/{job_id}/files/{filename}")]
    public Task<IActionResult> DownloadExportFileAsync(long ks_id, string job_id, string filename, CancellationToken ct)
        => InvokeAsync("releases.download_export_file", Req(ks: ks_id, res: job_id, res2: filename), ct);

    // ---- releases ----

    [HttpGet("api/knowledge/{ks_id:long}/releases")]
    public Task<IActionResult> ListAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("releases.list", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/releases")]
    public Task<IActionResult> CreateAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.create", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/releases/diff")]
    public Task<IActionResult> DiffAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("releases.diff", Req(ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/releases/{release_id}")]
    public Task<IActionResult> DeleteAsync(long ks_id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.delete", Req(ks: ks_id, res: release_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/releases/{release_id}/deployment")]
    public Task<IActionResult> StopDeploymentAsync(long ks_id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.stop_deployment", Req(ks: ks_id, res: release_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/releases/{release_id}/deployment")]
    public Task<IActionResult> DeployAsync(long ks_id, string release_id, CancellationToken ct)
        => InvokeAsync("releases.deploy", Req(ks: ks_id, res: release_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/releases/{release_id}/publish")]
    public Task<IActionResult> PublishAsync(long ks_id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.publish", ReqWithBody(body, ks: ks_id, res: release_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/releases/{release_id}/review")]
    public Task<IActionResult> ReviewAsync(long ks_id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.review", ReqWithBody(body, ks: ks_id, res: release_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/releases/{release_id}/rollback")]
    public Task<IActionResult> RollbackAsync(long ks_id, string release_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("releases.rollback", ReqWithBody(body, ks: ks_id, res: release_id), ct);
}