using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;
using OnToPilot.Authorization;
using OnToPilot.Documents;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/documents*</c> surface &mdash;
/// document upload, listing, parsing, chunking, contribution, impact,
/// move, delete.
///
/// <para>Ten of the eleven operations go through the standard
/// <see cref="IIntegrationApiFacade"/> envelope so the dispatcher can
/// apply the usual extraction-active guard. The one exception is
/// <see cref="UploadAsync"/>: <c>multipart/form-data</c> doesn't fit the
/// JSON body envelope the facade carries, so that route bypasses the
/// facade and calls <see cref="DocumentService.UploadAsync"/> directly.
/// The dispatcher still has a <c>documents.upload</c> arm that throws
/// <see cref="NotSupportedException"/> as a defensive guard so any
/// in-process caller of the facade for that operation name fails loud
/// rather than silently returning a placeholder.</para>
/// </summary>
[ApiController]
[Authorize]
public sealed class DocumentsController : InternalControllerBase
{
    private readonly DocumentService _documents;

    public DocumentsController(IIntegrationApiFacade facade, DocumentService documents)
        : base(facade)
    {
        _documents = documents;
    }

    [HttpGet("api/knowledge/{id:guid}/documents")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => InvokeAsync("documents.list", ReqGuid(id), ct);

    [HttpGet("api/knowledge/{id:guid}/documents/page")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListPageAsync(Guid id, CancellationToken ct)
        => InvokeAsync("documents.list_page", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/documents/parse-batch")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> ParseBatchAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("documents.parse_batch", ReqGuidWithBody(body, id), ct);

    /// <summary>
    /// Multipart upload. Bypasses the facade because <c>IFormFile</c>
    /// doesn't fit the JSON envelope the facade carries; the
    /// <see cref="DocumentService"/> does the role check + dedup +
    /// blob write + audit directly.
    /// </summary>
    [HttpPost("api/knowledge/{id:guid}/documents/upload")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadAsync(
        Guid id,
        [FromForm] IFormFile? file,
        [FromForm] string? folder,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { detail = "file is required and must be non-empty" });
        }

        await using var stream = file.OpenReadStream();
        var actor = ResolveActor();
        try
        {
            var result = await _documents.UploadAsync(
                ksId: id,
                content: stream,
                fileName: file.FileName,
                mime: file.ContentType,
                sizeBytes: file.Length,
                folder: folder ?? "/",
                actor: actor,
                ct: ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            // Bad filename / missing extension.
            return BadRequest(new { detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Unsupported extension, empty file, KS not found, role gate
            // — translate to 400 / 403 / 404 by message rather than let
            // FastApiErrorMiddleware turn them into a generic 500.
            if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(new { detail = ex.Message });
            }
            return BadRequest(new { detail = ex.Message });
        }
    }

    [HttpGet("api/knowledge/{id:guid}/documents/{document_id:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> GetAsync(Guid id, Guid document_id, CancellationToken ct)
        => InvokeAsync("documents.get", ReqGuid(id, res: document_id.ToString()), ct);

    [HttpPatch("api/knowledge/{id:guid}/documents/{document_id:guid}")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> MoveAsync(Guid id, Guid document_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("documents.move", ReqGuidWithBody(body, id, res: document_id.ToString()), ct);

    [HttpGet("api/knowledge/{id:guid}/documents/{document_id:guid}/chunks")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ListChunksAsync(Guid id, Guid document_id, CancellationToken ct)
        => InvokeAsync("documents.list_chunks", ReqGuid(id, res: document_id.ToString()), ct);

    [HttpGet("api/knowledge/{id:guid}/documents/{document_id:guid}/contribution")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ContributionAsync(Guid id, Guid document_id, CancellationToken ct)
        => InvokeAsync("documents.contribution", ReqGuid(id, res: document_id.ToString()), ct);

    // NOTE: no [KSRoleAuthorize] on DeleteAsync — the existing HTTP
    // contract pins a role=None soft-fail (200 with {ok:false}) for this
    // route, and the filter hard-403s role=None. See the RBAC coverage
    // matrix task-2 report.
    [HttpPost("api/knowledge/{id:guid}/documents/{document_id:guid}/delete")]
    public Task<IActionResult> DeleteAsync(Guid id, Guid document_id, CancellationToken ct)
        => InvokeAsync("documents.delete", ReqGuid(id, res: document_id.ToString()), ct);

    [HttpGet("api/knowledge/{id:guid}/documents/{document_id:guid}/impact")]
    [KSRoleAuthorize(Minimum = KSRole.Viewer)]
    public Task<IActionResult> ImpactAsync(Guid id, Guid document_id, CancellationToken ct)
        => InvokeAsync("documents.impact", ReqGuid(id, res: document_id.ToString()), ct);

    [HttpPost("api/knowledge/{id:guid}/documents/{document_id:guid}/parse")]
    [KSRoleAuthorize(Minimum = KSRole.Editor)]
    public Task<IActionResult> ParseAsync(Guid id, Guid document_id, CancellationToken ct)
        => InvokeAsync("documents.parse", ReqGuid(id, res: document_id.ToString()), ct);
}
