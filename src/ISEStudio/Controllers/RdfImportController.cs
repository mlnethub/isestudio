using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ISEStudio.Application.Integration;
using ISEStudio.Authorization;

namespace ISEStudio.Controllers;

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
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> ImportAsync(
        Guid id,
        [FromForm] IFormFile? file,
        [FromForm] string target = "auto",
        [FromForm] string strategy = "merge",
        [FromForm] string format = "auto",
        [FromForm(Name = "base_iri")] string? baseIri = null,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { detail = "file is required and must be non-empty" });
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var body = new Dictionary<string, object?>
        {
            ["file"] = buffer.ToArray(),
            ["filename"] = file.FileName,
            ["content_type"] = file.ContentType,
            ["target"] = target,
            ["strategy"] = strategy,
            ["format"] = format,
            ["base_iri"] = baseIri,
        };

        return await InvokeAsync("rdf.import", ReqGuidWithBody(body, id), ct).ConfigureAwait(false);
    }
}
