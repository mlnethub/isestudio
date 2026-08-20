using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

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

    [HttpPost("api/knowledge/{id:guid}/abox/assertions")]
    public Task<IActionResult> AddAssertionAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.add_assertion", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/assertions/delete")]
    public Task<IActionResult> RemoveAssertionAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.remove_assertion", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/classes")]
    public Task<IActionResult> ListClassesAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.list_classes", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/individual")]
    public Task<IActionResult> GetIndividualAsync(
        Guid id,
        [FromQuery(Name = "iri")] string? iri,
        CancellationToken ct)
        => InvokeAsync("abox.get_individual", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/individuals")]
    public Task<IActionResult> ListIndividualsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.list_individuals", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/individuals")]
    public Task<IActionResult> CreateIndividualAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.create_individual", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/individuals/delete")]
    public Task<IActionResult> DeleteIndividualAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.delete_individual", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/reset")]
    public Task<IActionResult> ResetAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.reset", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/abox/validate")]
    public Task<IActionResult> ValidateAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.validate", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/abox/validate/fix")]
    public Task<IActionResult> FixAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.fix_violation", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/validation/decisions")]
    public Task<IActionResult> ListDecisionsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("abox.list_validation_decisions", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/validation/decisions/{did}")]
    public Task<IActionResult> RevokeDecisionAsync(Guid id, string did, CancellationToken ct)
        => InvokeAsync("abox.revoke_validation_decision", ReqGuid(id, res: did), ct);
}
