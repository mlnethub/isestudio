using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/abox*</c> surface &mdash;
/// instance-level assertions, individual CRUD, SHACL validation, fix.
/// </summary>
[ApiController]
[Authorize]
public sealed class ABoxController : InternalControllerBase
{
    public ABoxController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpPost("api/knowledge/{ks_id:long}/abox/assertions")]
    public Task<IActionResult> AddAssertionAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.add_assertion", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/abox/assertions/delete")]
    public Task<IActionResult> RemoveAssertionAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.remove_assertion", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/abox/classes")]
    public Task<IActionResult> ListClassesAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("abox.list_classes", Req(ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/abox/individual")]
    public Task<IActionResult> GetIndividualAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("abox.get_individual", Req(ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/abox/individuals")]
    public Task<IActionResult> ListIndividualsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("abox.list_individuals", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/abox/individuals")]
    public Task<IActionResult> CreateIndividualAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.create_individual", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/abox/individuals/delete")]
    public Task<IActionResult> DeleteIndividualAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.delete_individual", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/abox/reset")]
    public Task<IActionResult> ResetAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.reset", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/abox/validate")]
    public Task<IActionResult> ValidateAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("abox.validate", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/abox/validate/fix")]
    public Task<IActionResult> FixAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("abox.fix_violation", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/validation/decisions")]
    public Task<IActionResult> ListDecisionsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("abox.list_validation_decisions", Req(ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/validation/decisions/{did}")]
    public Task<IActionResult> RevokeDecisionAsync(long ks_id, string did, CancellationToken ct)
        => InvokeAsync("abox.revoke_validation_decision", Req(ks: ks_id, res: did), ct);
}