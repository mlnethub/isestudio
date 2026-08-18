using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/extract*</c> surface &mdash;
/// extraction orchestration (TBox, ABox, combined) and job inspection.
/// </summary>
[ApiController]
[Authorize]
public sealed class ExtractionController : InternalControllerBase
{
    public ExtractionController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpPost("api/knowledge/{ks_id:long}/extract")]
    public Task<IActionResult> RunAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("extraction.run", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/extract-all")]
    public Task<IActionResult> RunCombinedAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("extraction.run_combined", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/extract-instances")]
    public Task<IActionResult> RunInstancesAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("extraction.run_instances", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/jobs")]
    public Task<IActionResult> ListJobsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("extraction.list_jobs", Req(ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/jobs/{job_id}")]
    public Task<IActionResult> GetJobAsync(long ks_id, string job_id, CancellationToken ct)
        => InvokeAsync("extraction.get_job", Req(ks: ks_id, res: job_id), ct);
}