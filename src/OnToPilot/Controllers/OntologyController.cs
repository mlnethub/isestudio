using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/ontology*</c> surface &mdash; TBox
/// inspection, structured edits, export, reset, provenance, sources.
/// </summary>
[ApiController]
[Authorize]
public sealed class OntologyController : InternalControllerBase
{
    public OntologyController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/ontology")]
    public Task<IActionResult> GetAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("ontology.get", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/ontology/edit")]
    public Task<IActionResult> EditAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("ontology.edit", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/ontology/export")]
    public Task<IActionResult> ExportAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("ontology.export", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/ontology/reset")]
    public Task<IActionResult> ResetAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("ontology.reset", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/provenance")]
    public Task<IActionResult> ProvenanceAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("ontology.provenance", Req(ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/sources")]
    public Task<IActionResult> SourcesAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("ontology.sources", Req(ks: ks_id), ct);
}