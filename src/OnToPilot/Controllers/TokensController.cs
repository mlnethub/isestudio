using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{ks_id}/tokens*</c> surface &mdash;
/// knowledge-API bearer tokens (create / list / revoke / reveal).
/// </summary>
[ApiController]
[Authorize]
public sealed class TokensController : InternalControllerBase
{
    public TokensController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{ks_id:long}/tokens")]
    public Task<IActionResult> ListAsync(long ks_id, CancellationToken ct)
        => InvokeAsync("tokens.list", Req(ks: ks_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/tokens")]
    public Task<IActionResult> CreateAsync(long ks_id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("tokens.create", ReqWithBody(body, ks: ks_id), ct);

    [HttpDelete("api/knowledge/{ks_id:long}/tokens/{token_id}")]
    public Task<IActionResult> RevokeAsync(long ks_id, string token_id, CancellationToken ct)
        => InvokeAsync("tokens.revoke", Req(ks: ks_id, res: token_id), ct);

    [HttpPost("api/knowledge/{ks_id:long}/tokens/{token_id}/reveal")]
    public Task<IActionResult> RevealAsync(long ks_id, string token_id, CancellationToken ct)
        => InvokeAsync("tokens.reveal", Req(ks: ks_id, res: token_id), ct);
}