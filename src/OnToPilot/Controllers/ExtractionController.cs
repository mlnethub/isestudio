using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/extract*</c> surface &mdash;
/// extraction orchestration (TBox, ABox, combined) and job inspection.
/// </summary>
[ApiController]
[Authorize]
public sealed class ExtractionController : InternalControllerBase
{
    public ExtractionController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpPost("api/knowledge/{id:guid}/extract")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RunAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("extraction.run", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/extract-all")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RunCombinedAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("extraction.run_combined", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/extract-instances")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RunInstancesAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("extraction.run_instances", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/jobs")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> ListJobsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("extraction.list_jobs", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/jobs/{job_id}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> GetJobAsync(Guid id, string job_id, CancellationToken ct)
        => InvokeAsync("extraction.get_job", ReqGuid(ks: id, res: job_id), ct);
}
