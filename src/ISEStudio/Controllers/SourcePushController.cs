using ISEStudio.Sources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ISEStudio.Controllers;

[ApiController]
[Authorize]
[Route("api/knowledge/{id:guid}/ingestion-sources/{sourceId:guid}")]
public sealed class SourcePushController(SourcePushService pushes) : ControllerBase
{
    /// <summary>Accepts up to 1000 RDF statements with stable ids; results preserve input order and per-item status.</summary>
    [HttpPost("statements")]
    [AllowAnonymous]
    [EnableRateLimiting(SourcePushService.RateLimitPolicy)]
    [RequestSizeLimit(SourcePushService.MaxItemBytes)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SourceStatementsResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<SourceStatementsResult>(StatusCodes.Status207MultiStatus)]
    public async Task<IActionResult> StatementsAsync(Guid id, Guid sourceId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var result = await pushes.PushStatementsAsync(id, sourceId, Request, ct).ConfigureAwait(false);
        return result.Error is null ? StatusCode(result.StatusCode, result.Value) : StatusCode(result.StatusCode, new { detail = result.Error });
    }

    /// <summary>
    /// Pushes raw document bytes (at most 20 MiB). Requires one external_key query value,
    /// X-Source-Filename (plain filename with extension), Content-Type, and one Bearer Source token.
    /// Returns added/updated counts; an unchanged retry returns zero for both. Tokens are never read from query/body.
    /// </summary>
    [HttpPost("documents")]
    [AllowAnonymous]
    [EnableRateLimiting(SourcePushService.RateLimitPolicy)]
    [RequestSizeLimit(SourcePushService.MaxItemBytes)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SourceItemResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> DocumentsAsync(Guid id, Guid sourceId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var result = await pushes.PushDocumentAsync(id, sourceId, Request, ct).ConfigureAwait(false);
        return result.Error is null ? Ok(result.Value) : StatusCode(result.StatusCode, new { detail = result.Error });
    }
}