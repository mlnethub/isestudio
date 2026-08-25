using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ISEStudio.Application.Integration;

namespace ISEStudio.Controllers;

/// <summary>
/// Internal <c>/api/knowledge/{id}/mcp/tokens*</c> surface &mdash;
/// MCP-bearer tokens (create / list / revoke). Distinct from
/// <see cref="TokensController"/> because the MCP transport enforces
/// scopes + active-user state on every call rather than trusting the
/// token's snapshot.
/// </summary>
[ApiController]
[Authorize]
public sealed class McpTokensController : InternalControllerBase
{
    public McpTokensController(IIntegrationApiFacade facade) : base(facade) { }

    [HttpGet("api/knowledge/{id:guid}/mcp/tokens")]
    public Task<IActionResult> ListAsync(Guid id, CancellationToken ct)
        => InvokeAsync("mcp_tokens.list", ReqGuid(id), ct);

    [HttpPost("api/knowledge/{id:guid}/mcp/tokens")]
    public Task<IActionResult> CreateAsync(Guid id, [FromBody] object body, CancellationToken ct)
        => InvokeAsync("mcp_tokens.create", ReqGuidWithBody(body, id), ct);

    [HttpDelete("api/knowledge/{id:guid}/mcp/tokens/{token_id}")]
    public Task<IActionResult> RevokeAsync(Guid id, string token_id, CancellationToken ct)
        => InvokeAsync("mcp_tokens.revoke", ReqGuid(ks: id, res: token_id), ct);
}
