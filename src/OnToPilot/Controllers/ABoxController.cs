using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/abox*</c> surface &mdash;
/// instance-level assertions, individual CRUD, SHACL validation, fix.
/// </summary>
[ApiController]
[Authorize]
public sealed class ABoxController : InternalControllerBase
{
    public ABoxController(IIntegrationApiFacade facade) : base(facade) { }

    // NOTE: no [KSRoleAuthorize] on AddAssertionAsync / CreateIndividualAsync —
    // the existing HTTP contract pins a role=None soft-fail (200 with an empty
    // envelope) for those two routes, and the filter hard-403s role=None. See
    // the RBAC coverage matrix task-2 report.

    [HttpPost("api/knowledge/{id:guid}/abox/assertions")]
    public Task<IActionResult> AddAssertionAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.add_assertion", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/assertions/delete")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RemoveAssertionAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.remove_assertion", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/classes")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListClassesAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.list_classes", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/individual")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> GetIndividualAsync(
        Guid id,
        [FromQuery(Name = "iri")] string? iri,
        CancellationToken ct)
        => InvokeAsync("abox.get_individual", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/individuals")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListIndividualsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.list_individuals", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/individuals")]
    public Task<IActionResult> CreateIndividualAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.create_individual", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/individuals/delete")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> DeleteIndividualAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.delete_individual", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/reset")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> ResetAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.reset", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/validate")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ValidateAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.validate", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/validate/fix")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> FixAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.fix_violation", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/validation/decisions")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListDecisionsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.list_validation_decisions", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/validation/decisions/{did}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RevokeDecisionAsync(Guid id, string did, CancellationToken ct)
        => InvokeAsync("abox.revoke_validation_decision", ReqGuid(id, res: did), ct);
}
