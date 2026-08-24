using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/vocabulary*</c> surface &mdash;
/// SKOS vocabulary management (concepts, schemes, proposals, sync).
/// </summary>
[ApiController]
[Authorize]
public sealed class VocabularyController : InternalControllerBase
{
    public VocabularyController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/vocabulary")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.get", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/vocabulary/concepts")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> DeleteConceptAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.delete_concept", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/concepts")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListConceptsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_concepts", ReqGuid(id), ct);

    [HttpPatch("api/knowledge/{id:guid}/vocabulary/concepts")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> UpdateConceptAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.update_concept", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/concepts")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> CreateConceptAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.create_concept", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/export")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ExportAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.export", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/proposals")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListProposalsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_proposals", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/proposals/{proposal_id}/accept")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> AcceptProposalAsync(Guid id, string proposal_id, CancellationToken ct)
        => InvokeAsync("vocabulary.accept_proposal", ReqGuid(ks: id, res: proposal_id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/proposals/{proposal_id}/reject")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> RejectProposalAsync(Guid id, string proposal_id, CancellationToken ct)
        => InvokeAsync("vocabulary.reject_proposal", ReqGuid(ks: id, res: proposal_id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/resolve")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ResolveTermAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.resolve_term", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/vocabulary/schemes")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> DeleteSchemeAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.delete_scheme", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/schemes")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListSchemesAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_schemes", ReqGuid(id), ct);

    [HttpPatch("api/knowledge/{id:guid}/vocabulary/schemes")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> UpdateSchemeAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.update_scheme", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/schemes")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> CreateSchemeAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.create_scheme", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/suggest")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> SuggestTermsAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.suggest_terms", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/sync")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> SyncAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.sync", ReqGuidWithBody(body, id), ct);
}
