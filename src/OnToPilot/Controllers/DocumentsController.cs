using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;
using OnToPilot.Documents;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/documents*</c> surface &mdash;
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

    [HttpGet("api/knowledge/{ks_id:long}/documents")]
    public Task<IActionResult> ListAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("documents.list", Req(ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/page")]
    public Task<IActionResult> ListPageAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("documents.list_page", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/documents/parse-batch")]
    public Task<IActionResult> ParseBatchAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("documents.parse_batch", ReqWithBody(body, ks: ks_id), ct);

    /// <summary>
    /// Multipart upload. Bypasses the facade because <c>IFormFile</c>
    /// doesn't fit the JSON envelope the facade carries; the
    /// <see cref="DocumentService"/> does the role check + dedup +
    /// blob write + audit directly.
    /// </summary>
    [HttpPost("api/knowledge/{ks_id:long}/documents/upload")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadAsync(
        long ks_id,
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
                ksId: ks_id,
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

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id:long}")]
    public Task<IActionResult> GetAsync(long ks_id, long document_id, CancellationToken ct)
        => InvokeAsync("documents.get", Req(ks: ks_id, res: document_id.ToString()), ct);

    [HttpPatch("api/knowledge/{ks_id:long}/documents/{document_id:long}")]
    public Task<IActionResult> MoveAsync(long ks_id, long document_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("documents.move", ReqWithBody(body, ks: ks_id, res: document_id.ToString()), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id:long}/chunks")]
    public Task<IActionResult> ListChunksAsync(long ks_id, long document_id, CancellationToken ct)
        => InvokeAsync("documents.list_chunks", Req(ks: ks_id, res: document_id.ToString()), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id:long}/contribution")]
    public Task<IActionResult> ContributionAsync(long ks_id, long document_id, CancellationToken ct)
        => InvokeAsync("documents.contribution", Req(ks: ks_id, res: document_id.ToString()), ct);

    [HttpPost("api/knowledge/{ks_id:long}/documents/{document_id:long}/delete")]
    public Task<IActionResult> DeleteAsync(long ks_id, long document_id, CancellationToken ct)
        => InvokeAsync("documents.delete", Req(ks: ks_id, res: document_id.ToString()), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id:long}/impact")]
    public Task<IActionResult> ImpactAsync(long ks_id, long document_id, CancellationToken ct)
        => InvokeAsync("documents.impact", Req(ks: ks_id, res: document_id.ToString()), ct);

    [HttpPost("api/knowledge/{ks_id:long}/documents/{document_id:long}/parse")]
    public Task<IActionResult> ParseAsync(long ks_id, long document_id, CancellationToken ct)
        => InvokeAsync("documents.parse", Req(ks: ks_id, res: document_id.ToString()), ct);
}