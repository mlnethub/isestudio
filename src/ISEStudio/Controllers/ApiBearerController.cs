using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ISEStudio.Authentication;

namespace ISEStudio.Controllers;

/// <summary>
/// Read-only API endpoints guarded by the bearer-token authentication scheme.
/// The Python backend exposes the same shape at <c>/api/v1/knowledge-systems/{public_id}</c>;
/// this controller is the .NET counterpart that accepts
/// <c>Authorization: Bearer &lt;opk_...&gt;</c> instead of a session cookie.
///
/// <para>The diagnostic <c>whoami</c> endpoint is hidden from the OpenAPI
/// document &mdash; it is a developer aid, not part of the public contract.
/// The api-mcp inventory gate asserts (METHOD, path) parity with the frozen
/// Python baseline, so the route must not leak into the .NET document.</para>
/// </summary>
[ApiController]
[Route("api/bearer")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ApiBearerController : ControllerBase
{
    /// <summary>
    /// Echo the verification result the bearer handler stashed on
    /// <c>HttpContext.Items</c>. Also confirms the token's knowledge system
    /// matches the <paramref name="publicId"/> in the URL — so a token
    /// scoped to one KS can't accidentally access another.
    /// </summary>
    [HttpGet("whoami/{publicId}")]
    [Authorize(AuthenticationSchemes = ApiBearerAuthenticationHandler.SchemeName)]
    public IActionResult WhoAmI(string publicId)
    {
        if (!HttpContext.Items.TryGetValue(ApiBearerAuthenticationHandler.VerificationItemKey, out var raw)
            || raw is not KnowledgeApiTokenVerificationResult verification)
        {
            return Unauthorized(new { detail = "Invalid or expired API token" });
        }

        if (!string.Equals(verification.KnowledgeSystem.PublicId, publicId, StringComparison.Ordinal))
        {
            return Unauthorized(new { detail = "Invalid or expired API token" });
        }

        return Ok(new
        {
            knowledge_system_id = verification.KnowledgeSystem.Id,
            knowledge_system_public_id = verification.KnowledgeSystem.PublicId,
            token_id = verification.Token.Id,
            token_name = verification.Token.Name,
            scopes = verification.Token.Scopes,
            now = verification.Now,
        });
    }
}