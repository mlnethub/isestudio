using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

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
    public Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.get", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/vocabulary/concepts")]
    public Task<IActionResult> DeleteConceptAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.delete_concept", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/concepts")]
    public Task<IActionResult> ListConceptsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_concepts", ReqGuid(id), ct);

    [HttpPatch("api/knowledge/{id:guid}/vocabulary/concepts")]
    public Task<IActionResult> UpdateConceptAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.update_concept", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/concepts")]
    public Task<IActionResult> CreateConceptAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.create_concept", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/export")]
    public Task<IActionResult> ExportAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.export", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/proposals")]
    public Task<IActionResult> ListProposalsAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_proposals", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/proposals/{proposal_id}/accept")]
    public Task<IActionResult> AcceptProposalAsync(Guid id, string proposal_id, CancellationToken ct)
        => InvokeAsync("vocabulary.accept_proposal", ReqGuid(ks: id, res: proposal_id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/proposals/{proposal_id}/reject")]
    public Task<IActionResult> RejectProposalAsync(Guid id, string proposal_id, CancellationToken ct)
        => InvokeAsync("vocabulary.reject_proposal", ReqGuid(ks: id, res: proposal_id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/resolve")]
    public Task<IActionResult> ResolveTermAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.resolve_term", ReqGuid(id), ct);

    [HttpDelete("api/knowledge/{id:guid}/vocabulary/schemes")]
    public Task<IActionResult> DeleteSchemeAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.delete_scheme", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/vocabulary/schemes")]
    public Task<IActionResult> ListSchemesAsync(Guid id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_schemes", ReqGuid(id), ct);

    [HttpPatch("api/knowledge/{id:guid}/vocabulary/schemes")]
    public Task<IActionResult> UpdateSchemeAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.update_scheme", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/schemes")]
    public Task<IActionResult> CreateSchemeAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.create_scheme", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/suggest")]
    public Task<IActionResult> SuggestTermsAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.suggest_terms", ReqGuidWithBody(body, id), ct);

    [HttpPost("api/knowledge/{id:guid}/vocabulary/sync")]
    public Task<IActionResult> SyncAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.sync", ReqGuidWithBody(body, id), ct);
}