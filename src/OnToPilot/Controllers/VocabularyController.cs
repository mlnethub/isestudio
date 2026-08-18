using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/vocabulary*</c> surface &mdash;
/// SKOS vocabulary management (concepts, schemes, proposals, sync).
/// </summary>
[ApiController]
[Authorize]
public sealed class VocabularyController : InternalControllerBase
{
    public VocabularyController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/vocabulary")]
    public Task<IActionResult> GetAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("vocabulary.get", Req(ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/vocabulary/concepts")]
    public Task<IActionResult> DeleteConceptAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.delete_concept", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/vocabulary/concepts")]
    public Task<IActionResult> ListConceptsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_concepts", Req(ks: ks_id), ct);

    [HttpPatch("api/knowledge/{ks_id:long}/vocabulary/concepts")]
    public Task<IActionResult> UpdateConceptAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.update_concept", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/vocabulary/concepts")]
    public Task<IActionResult> CreateConceptAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.create_concept", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/vocabulary/export")]
    public Task<IActionResult> ExportAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("vocabulary.export", Req(ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/vocabulary/proposals")]
    public Task<IActionResult> ListProposalsAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_proposals", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/vocabulary/proposals/{proposal_id}/accept")]
    public Task<IActionResult> AcceptProposalAsync(long ks_id, string proposal_id, CancellationToken ct)
        => InvokeAsync("vocabulary.accept_proposal", Req(ks: ks_id, res: proposal_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/vocabulary/proposals/{proposal_id}/reject")]
    public Task<IActionResult> RejectProposalAsync(long ks_id, string proposal_id, CancellationToken ct)
        => InvokeAsync("vocabulary.reject_proposal", Req(ks: ks_id, res: proposal_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/vocabulary/resolve")]
    public Task<IActionResult> ResolveTermAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("vocabulary.resolve_term", Req(ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/vocabulary/schemes")]
    public Task<IActionResult> DeleteSchemeAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.delete_scheme", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/vocabulary/schemes")]
    public Task<IActionResult> ListSchemesAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("vocabulary.list_schemes", Req(ks: ks_id), ct);

    [HttpPatch("api/knowledge/{ks_id:long}/vocabulary/schemes")]
    public Task<IActionResult> UpdateSchemeAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.update_scheme", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/vocabulary/schemes")]
    public Task<IActionResult> CreateSchemeAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.create_scheme", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/vocabulary/suggest")]
    public Task<IActionResult> SuggestTermsAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.suggest_terms", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/vocabulary/sync")]
    public Task<IActionResult> SyncAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("vocabulary.sync", ReqWithBody(body, ks: ks_id), ct);
}