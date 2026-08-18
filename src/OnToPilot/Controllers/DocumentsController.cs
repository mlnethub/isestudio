using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/documents*</c> surface &mdash;
/// document upload, listing, parsing, chunking, contribution, impact.
/// </summary>
[ApiController]
[Authorize]
public sealed class DocumentsController : InternalControllerBase
{
    public DocumentsController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/documents")]
    public Task<IActionResult> ListAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("documents.list", Req(ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/page")]
    public Task<IActionResult> ListPageAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("documents.list_page", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/documents/parse-batch")]
    public Task<IActionResult> ParseBatchAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("documents.parse_batch", ReqWithBody(body, ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/documents/upload")]
    public Task<IActionResult> UploadAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("documents.upload", ReqWithBody(body, ks: ks_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id}")]
    public Task<IActionResult> GetAsync(long ks_id, string document_id, CancellationToken ct)
        => InvokeAsync("documents.get", Req(ks: ks_id, res: document_id), ct);

    [HttpPatch("api/knowledge/{ks_id:long}/documents/{document_id}")]
    public Task<IActionResult> MoveAsync(long ks_id, string document_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("documents.move", ReqWithBody(body, ks: ks_id, res: document_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id}/chunks")]
    public Task<IActionResult> ListChunksAsync(long ks_id, string document_id, CancellationToken ct)
        => InvokeAsync("documents.list_chunks", Req(ks: ks_id, res: document_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id}/contribution")]
    public Task<IActionResult> ContributionAsync(long ks_id, string document_id, CancellationToken ct)
        => InvokeAsync("documents.contribution", Req(ks: ks_id, res: document_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/documents/{document_id}/delete")]
    public Task<IActionResult> DeleteAsync(long ks_id, string document_id, CancellationToken ct)
        => InvokeAsync("documents.delete", Req(ks: ks_id, res: document_id), ct);

    [HttpGet("api/knowledge/{ks_id:long}/documents/{document_id}/impact")]
    public Task<IActionResult> ImpactAsync(long ks_id, string document_id, CancellationToken ct)
        => InvokeAsync("documents.impact", Req(ks: ks_id, res: document_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/documents/{document_id}/parse")]
    public Task<IActionResult> ParseAsync(long ks_id, string document_id, CancellationToken ct)
        => InvokeAsync("documents.parse", Req(ks: ks_id, res: document_id), ct);
}