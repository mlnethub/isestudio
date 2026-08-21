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
    public async Task<IActionResult> ExportAsync(Guid id, [FromQuery] string fmt = "turtle", CancellationToken ct = default)
    {
        var payload = await Facade.InvokeAsync("ontology.export", ReqGuid(id), ct).ConfigureAwait(false);
        // Return the RDF as raw text with the matching media type — NOT a
        // JSON-quoted string — so the frontend's Blob download is valid
        // RDF. Mirrors Python's Response(content, media_type=...) (ontology.py:63).
        if (payload is string rdf)
        {
            return Content(rdf, MediaTypeForExport(fmt));
        }
        return payload is null ? Ok(new { ok = true }) : Ok(payload);
    }

    private static string MediaTypeForExport(string fmt) => fmt.Trim().ToLowerInvariant() switch
    {
        "turtle" or "ttl" => "text/turtle",
        "ntriples" or "nt" or "n-triples" => "application/n-triples",
        "nquads" or "n-quads" or "nq" => "application/n-quads",
        "trig" => "application/trig",
        _ => "text/plain",
    };

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
