using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Integration;
using ISEStudio.Authentication;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Mcp;

/// <summary>
/// Resolves the current MCP principal (user + knowledge system + scopes)
/// and the live KS role on each call. The lookup is deliberately not
/// cached at the request, tool, or principal level: every MCP tool
/// invocation re-reads the user/KS/grant rows from the database so a
/// membership downgrade (or revocation) takes effect on the next call
/// without forcing the operator to invalidate the bearer token.
///
/// <para>The accessor is registered as <c>Scoped</c> so each MCP tool
/// call gets a fresh resolution against the request-scoped
/// <see cref="ISEStudioDbContext"/>.</para>
/// </summary>
public sealed class McpPrincipalAccessor
{
    /// <summary>HttpContext.Items key for the <see cref="McpPrincipal"/> the middleware stashes.</summary>
    public const string PrincipalItemKey = "mcp.principal";

    /// <summary>HttpContext.Items key for the verified bearer plaintext; never exposed off the request scope.</summary>
    public const string PlaintextItemKey = "mcp.bearer";

    private readonly ISEStudioDbContext _db;
    private readonly KnowledgeSystemAccessService _access;
    private readonly IMcpTokenService _tokens;
    private readonly TimeProvider _clock;

    /// <summary>DI constructor.</summary>
    public McpPrincipalAccessor(
        ISEStudioDbContext db,
        KnowledgeSystemAccessService access,
        IMcpTokenService tokens,
        TimeProvider clock)
    {
        _db = db;
        _access = access;
        _tokens = tokens;
        _clock = clock;
    }

    /// <summary>
    /// Resolve the live <see cref="McpPrincipal"/> from the request
    /// scope. The middleware runs ahead of the MCP endpoint and stashes
    /// the principal + bearer plaintext under the items keys; on every
    /// tool call we re-read the row from the database so a token that
    /// was valid at <c>POST /mcp</c> arrival time is revalidated
    /// immediately before each <c>tools/call</c>.
    /// </summary>
    public async Task<McpPrincipal> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (!httpContext.Items.TryGetValue(PlaintextItemKey, out var rawPlaintext)
            || rawPlaintext is not string plaintext
            || string.IsNullOrWhiteSpace(plaintext))
        {
            throw new McpToolException("Not authenticated");
        }

        var verification = await _tokens.VerifyAsync(plaintext, cancellationToken).ConfigureAwait(false);
        if (verification is null)
        {
            throw new McpToolException("Not authenticated");
        }

        // Preserve the scopes the middleware stamped on the principal so
        // we don't have to re-query the token row just to know what the
        // caller is allowed to do. The token row itself is the source of
        // truth for revocation / expiry; that is checked above.
        IReadOnlyList<string> scopes = Array.Empty<string>();
        if (httpContext.Items.TryGetValue(PrincipalItemKey, out var rawPrincipal)
            && rawPrincipal is McpPrincipal snapshot)
        {
            scopes = snapshot.Scopes;
        }

        return new McpPrincipal(
            User: verification.User,
            KnowledgeSystem: verification.KnowledgeSystem,
            Scopes: scopes);
    }

    /// <summary>
    /// Look up the live KS role for the supplied principal. Re-runs the
    /// membership / ownership / grant query on every call so a
    /// <c>viewer → editor</c> change (or downgrade) takes effect on the
    /// next tool invocation. The membership downgrade test in
    /// <c>McpAuthorizationTests</c> depends on this real-time lookup.
    /// </summary>
    public Task<KSRole> GetEffectiveRoleAsync(
        McpPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return _access.GetEffectiveRoleAsync(principal.User, principal.KnowledgeSystem, _db, cancellationToken);
    }

    /// <summary>
    /// Convenience check: throw <see cref="McpToolException"/> when the
    /// principal lacks one of the requested scopes. The MCP transport
    /// returns the message via the JSON-RPC <c>isError</c> envelope so
    /// the test assertion <c>Assert.Contains("mcp:read", error.Message)</c>
    /// can pin the wording.
    /// </summary>
    public void RequireScope(McpPrincipal principal, string scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(scope);
        if (!principal.Scopes.Contains(scope))
        {
            throw new McpToolException($"Missing required scope '{scope}'.");
        }
    }

    /// <summary>
    /// Convenience check: throw <see cref="McpToolException"/> when the
    /// principal's live KS role is below <paramref name="minimum"/>.
    /// </summary>
    public async Task RequireRoleAsync(
        McpPrincipal principal,
        KSRole minimum,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var role = await GetEffectiveRoleAsync(principal, cancellationToken).ConfigureAwait(false);
        if (role < minimum)
        {
            throw new McpToolException(
                $"This tool requires the editor role on knowledge system '{principal.KnowledgeSystem.PublicId}'.");
        }
    }
}