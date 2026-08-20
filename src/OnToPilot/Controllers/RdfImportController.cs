using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/rdf/import</c> surface &mdash;
/// raw RDF ingestion into the workspace graph.
/// </summary>
[ApiController]
[Authorize]
public sealed class RdfImportController : InternalControllerBase
{
    public RdfImportController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpPost("api/knowledge/{id:guid}/rdf/import")]
    public Task<IActionResult> ImportAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("rdf.import", ReqGuidWithBody(body, id), ct);
}
