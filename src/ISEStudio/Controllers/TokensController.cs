using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ISEStudio.Application.Integration;

namespace ISEStudio.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/tokens*</c> surface &mdash;
/// knowledge-API bearer tokens (create / list / revoke / reveal).
/// </summary>
[ApiController]
[Authorize]
public sealed class TokensController : InternalControllerBase
{
    public TokensController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/tokens")]
    public Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => InvokeAsync("tokens.list", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/tokens")]
    public Task<IActionResult> CreateAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("tokens.create", ReqGuidWithBody(body, id), ct);

    [HttpDelete("api/knowledge/{id:guid}/tokens/{token_id}")]
    public Task<IActionResult> RevokeAsync(Guid id, string token_id, CancellationToken ct)
        => InvokeAsync("tokens.revoke", ReqGuid(ks: id, res: token_id), ct);

    [HttpPost("api/knowledge/{id:guid}/tokens/{token_id}/reveal")]
    public Task<IActionResult> RevealAsync(Guid id, string token_id, CancellationToken ct)
        => InvokeAsync("tokens.reveal", ReqGuid(ks: id, res: token_id), ct);
}
