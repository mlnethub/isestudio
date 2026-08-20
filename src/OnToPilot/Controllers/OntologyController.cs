using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/ontology*</c> surface &mdash; TBox
/// inspection, structured edits, export, reset, provenance, sources.
/// </summary>
[ApiController]
[Authorize]
public sealed class OntologyController : InternalControllerBase
{
    public OntologyController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/ontology")]
    public Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
        => InvokeAsync("ontology.get", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/ontology/edit")]
    public Task<IActionResult> EditAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("ontology.edit", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/ontology/export")]
    public Task<IActionResult> ExportAsync(Guid id, CancellationToken ct)
        => InvokeAsync("ontology.export", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/ontology/reset")]
    public Task<IActionResult> ResetAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("ontology.reset", ReqGuidWithBody(body, id), ct);

    [HttpGet("api/knowledge/{id:guid}/provenance")]
    public Task<IActionResult> ProvenanceAsync(Guid id, CancellationToken ct)
        => InvokeAsync("ontology.provenance", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/sources")]
    public Task<IActionResult> SourcesAsync(Guid id, CancellationToken ct)
        => InvokeAsync("ontology.sources", ReqGuid(id), ct);
}
