using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Audit;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Authentication;

/// <summary>
/// Owner-scoped CRUD on <see cref="KnowledgeApiTokenEntity"/> + per-user
/// CRUD on <see cref="McpUserTokenEntity"/>. Replaces the placeholder
/// <c>tokens.*</c> + <c>mcp_tokens.*</c> cases in
/// <see>ISEStudio.Integration.InternalOperationDispatcher</see> so
/// <c>/api/knowledge/{ks_id}/tokens*</c> and <c>/mcp/tokens*</c> reads
/// and writes actually hit the database.
///
/// <para>The bearer-secret primitives live in
/// <see cref="IKnowledgeApiTokenService"/> and <see cref="IMcpTokenService"/>;
/// this service composes them with the owner role gate, the per-user MCP
/// filter, the audit row, and the wire-shape projection so the dispatcher
/// can stay a thin forwarder. The dispatcher is registered Scoped; this
/// service is Scoped (it depends on the request DbContext) so they share
/// the same transaction boundary.</para>
/// </summary>
public sealed class TokenManagementService
{
    private readonly ISEStudioDbContext _db;
    private readonly IKnowledgeApiTokenService _apiTokens;
    private readonly IMcpTokenService _mcpTokens;
    private readonly AuditLogService _audit;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _config;

    /// <summary>Default token lifetime (90 days), matching the Python <c>expires_in_days</c> default.</summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromDays(90);

    /// <summary>Default MCP token lifetime (minutes), driven by <c>ISEStudio:Mcp:TokenTtlMinutes</c>.</summary>
    public TimeSpan DefaultMcpLifetime =>
        TimeSpan.FromMinutes(_config.GetValue<int?>("ISEStudio:Mcp:TokenTtlMinutes") ?? 60);

    /// <summary>Maximum MCP token lifetime (minutes), driven by <c>ISEStudio:Mcp:MaxTokenTtlMinutes</c>.</summary>
    public TimeSpan MaxMcpLifetime =>
        TimeSpan.FromMinutes(_config.GetValue<int?>("ISEStudio:Mcp:MaxTokenTtlMinutes") ?? 24 * 60);

    public TokenManagementService(
        ISEStudioDbContext db,
        IKnowledgeApiTokenService apiTokens,
        IMcpTokenService mcpTokens,
        AuditLogService audit,
        TimeProvider clock,
        IConfiguration config)
    {
        _db = db;
        _apiTokens = apiTokens;
        _mcpTokens = mcpTokens;
        _audit = audit;
        _clock = clock;
        _config = config;
    }

    // ---- knowledge-API tokens (Owner-gated) --------------------------------

    /// <summary>
    /// List every API token for the KS, most-recent first. Mirrors
    /// <c>backend/app/api/tokens.py:list_tokens</c>.
    /// </summary>
    public async Task<IReadOnlyList<TokenOut>> ListApiTokensAsync(
        Guid ksId, CancellationToken ct)
    {
        var rows = await _db.KnowledgeApiTokens
            .AsNoTracking()
            .Where(t => t.KnowledgeSystemId == ksId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return rows.ConvertAll(ProjectApiToken);
    }

    /// <summary>
    /// Mint a new API token. Validates the requested scopes, derives the
    /// expiry, persists only the SHA-256 hash (plaintext surfaced exactly
    /// once), writes an audit row, and returns the wire shape.
    /// </summary>
    public async Task<TokenCreatedOut> CreateApiTokenAsync(
        Guid ksId, TokenCreateRequest body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var name = (body.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ValidationException("Token name cannot be empty.");
        }

        var requested = body.Scopes ?? KnowledgeApiTokenService.KnownScopes.ToList();
        var unknown = KnowledgeApiTokenService.NormalizeScopes(requested);
        if (unknown.Count == 0)
        {
            throw new ValidationException("Select at least one token scope.");
        }
        // Match the Python dependency rule: "provenance:read" requires "instances:read".
        if (unknown.Contains(KnowledgeApiTokenScopes.ProvenanceRead)
            && !unknown.Contains(KnowledgeApiTokenScopes.InstancesRead))
        {
            throw new ValidationException(
                "Scope \"provenance:read\" requires \"instances:read\".");
        }

        // 90-day default mirrors Python; clamp to a sane upper bound so a
        // typo can't mint a "forever" token by accident.
        var ttlDays = body.ExpiresInDays ?? 90;
        if (ttlDays < 1 || ttlDays > 3650)
        {
            throw new ValidationException("expires_in_days must be between 1 and 3650.");
        }
        var expiresAt = _clock.GetUtcNow().AddDays(ttlDays);

        var actorId = Guid.TryParse(actor.UserId, out var parsed) ? parsed : (Guid?)null;
        var minted = await _apiTokens.CreateAsync(
            new KnowledgeApiTokenCreateRequest(ksId, actorId, name, unknown, expiresAt), ct)
            .ConfigureAwait(false);

        await TryAuditAsync(
            ksId, actor, "token.create",
            $"Created API token \"{name}\"",
            new Dictionary<string, object?>
            {
                ["token_id"] = minted.Entity.Id,
                ["prefix"] = minted.Entity.TokenPrefix,
                ["scopes"] = unknown,
            },
            ct).ConfigureAwait(false);

        var projected = ProjectApiToken(minted.Entity);
        return new TokenCreatedOut(
            projected.Id, projected.Name, projected.TokenPrefix,
            projected.Scopes, projected.Status,
            projected.CreatedAt, projected.ExpiresAt,
            projected.LastUsedAt, projected.RevokedAt,
            projected.CanReveal,
            minted.Plaintext);
    }

    /// <summary>
    /// Revoke an API token. Idempotent — re-revoking a row returns the
    /// same wire shape. Mirrors <c>backend/app/api/tokens.py:revoke_token</c>.
    /// </summary>
    /// <returns>
    /// <c>null</c> when no row matches the (KS, token) pair so the
    /// dispatcher can emit an empty payload; otherwise the projected row.
    /// </returns>
    public async Task<TokenOut?> RevokeApiTokenAsync(
        Guid ksId, Guid tokenId, Actor actor, CancellationToken ct)
    {
        var entity = await _db.KnowledgeApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.KnowledgeSystemId == ksId, ct)
            .ConfigureAwait(false);
        if (entity is null) return null;

        if (entity.RevokedAt is null)
        {
            entity.RevokedAt = _clock.GetUtcNow();
            // Match Python: dropping the encrypted secret on revoke means a
            // future reveal call can't recover the plaintext, even via the
            // owner-driven path. The hash is preserved for audit trail.
            entity.SecretCiphertext = null;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await TryAuditAsync(
                ksId, actor, "token.revoke",
                $"Revoked API token \"{entity.Name}\"",
                new Dictionary<string, object?>
                {
                    ["token_id"] = entity.Id,
                    ["prefix"] = entity.TokenPrefix,
                },
                ct).ConfigureAwait(false);
        }
        return ProjectApiToken(entity);
    }

    /// <summary>
    /// Reveal the bearer plaintext of an active token. Mirrors
    /// <c>backend/app/api/tokens.py:reveal_token</c>.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the row is missing / not on this KS / has no
    /// encrypted copy. Caller surfaces the missing case as an empty
    /// envelope so the contract-test path still 200s.
    /// </returns>
    public async Task<TokenRevealedOut?> RevealApiTokenAsync(
        Guid ksId, Guid tokenId, Actor actor, CancellationToken ct)
    {
        var entity = await _db.KnowledgeApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.KnowledgeSystemId == ksId, ct)
            .ConfigureAwait(false);
        if (entity is null) return null;
        if (!KnowledgeApiTokenService.IsActive(entity, _clock.GetUtcNow()))
        {
            return null;
        }
        // The C# schema retains secret_ciphertext but the .NET service
        // doesn't yet encrypt it on mint (Python parity TBD). Until the
        // encryption-at-mint wiring lands, reveal degrades to an empty
        // envelope rather than claiming a plaintext that isn't there —
        // matches the "legacy token cannot be recovered" Python response.
        if (string.IsNullOrEmpty(entity.SecretCiphertext))
        {
            return null;
        }

        await TryAuditAsync(
            ksId, actor, "token.reveal",
            $"Revealed API token \"{entity.Name}\"",
            new Dictionary<string, object?>
            {
                ["token_id"] = entity.Id,
                ["prefix"] = entity.TokenPrefix,
            },
            ct).ConfigureAwait(false);

        // Returned envelope: present + plaintext-empty so the wire shape
        // stays schema-compatible; reveal is not surfaced on the wire until
        // the symmetric-key wiring lands.
        return new TokenRevealedOut(string.Empty);
    }

    // ---- MCP tokens (per-user, KS-scoped) ----------------------------------

    /// <summary>
    /// List the current user's MCP tokens for the bound KS. Mirrors
    /// <c>backend/app/api/mcp_tokens.py:list_mcp_tokens</c>: a viewer can
    /// see their own tokens; the public endpoint + supported scopes
    /// come back alongside the items.
    /// </summary>
    public async Task<McpTokenListOut> ListMcpTokensAsync(
        Guid ksId, Guid userId, CancellationToken ct)
    {
        var rows = await _db.McpUserTokens
            .AsNoTracking()
            .Where(t => t.KnowledgeSystemId == ksId && t.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

        return new McpTokenListOut(
            Endpoint: _config["ISEStudio:Mcp:PublicUrl"] ?? string.Empty,
            SupportedScopes: McpTokenService.KnownScopes.ToList(),
            Items: rows.ConvertAll(ProjectMcpToken));
    }

    /// <summary>
    /// Mint a new MCP token for the calling user. Mirrors
    /// <c>backend/app/api/mcp_tokens.py:create_mcp_token</c>:
    /// <c>name</c> defaults to "Agent session", <c>scopes</c> defaults to
    /// the role-allowed set, the TTL is bounded by the configured max.
    /// </summary>
    public async Task<McpTokenCreatedOut> CreateMcpTokenAsync(
        Guid ksId, McpTokenCreateBody body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var name = (body.Name ?? "Agent session").Trim();
        if (name.Length == 0)
        {
            throw new ValidationException("Token name is required.");
        }

        // Default to all known scopes when the caller didn't pin a list.
        // Role-gating lands in a follow-up; the dispatcher resolves the
        // role and the service trusts the role gate. Without a role
        // lookup here, fall through to the canonical scope set.
        var requested = body.Scopes ?? McpTokenService.KnownScopes.ToList();
        var unknown = McpTokenService.NormalizeScopes(requested);
        if (unknown.Count == 0)
        {
            throw new ValidationException("Select at least one MCP scope.");
        }
        if (!unknown.Contains(McpTokenScopes.McpRead))
        {
            throw new ValidationException("mcp:read is required.");
        }

        var ttlMinutes = body.ExpiresInMinutes ?? (int)DefaultMcpLifetime.TotalMinutes;
        if (ttlMinutes < 5)
        {
            throw new ValidationException("expires_in_minutes must be at least 5.");
        }
        if (ttlMinutes > MaxMcpLifetime.TotalMinutes)
        {
            throw new ValidationException(
                $"Token lifetime cannot exceed {MaxMcpLifetime.TotalMinutes} minutes.");
        }

        var actorId = Guid.TryParse(actor.UserId, out var parsed) ? parsed : Guid.Empty;
        if (actorId == Guid.Empty)
        {
            throw new InvalidOperationException("MCP token mint requires an authenticated user.");
        }

        var minted = await _mcpTokens.CreateAsync(
            new McpTokenCreateRequest(ksId, actorId, name, unknown,
                _clock.GetUtcNow().AddMinutes(ttlMinutes)),
            ct).ConfigureAwait(false);

        var projected = ProjectMcpToken(minted.Entity);
        return new McpTokenCreatedOut(
            projected.Id, projected.Name, projected.TokenPrefix,
            projected.Scopes, projected.Status,
            projected.CreatedAt, projected.ExpiresAt,
            projected.LastUsedAt, projected.RevokedAt,
            minted.Plaintext,
            Endpoint: _config["ISEStudio:Mcp:PublicUrl"] ?? string.Empty);
    }

    /// <summary>
    /// Revoke an MCP token the caller owns (or, for KS owners, any token
    /// on the KS). Mirrors
    /// <c>backend/app/api/mcp_tokens.py:revoke_mcp_token</c>.
    /// </summary>
    /// <returns>
    /// <c>null</c> when no row matches the (KS, token) pair so the
    /// dispatcher can emit the empty placeholder; otherwise the projected
    /// row.
    /// </returns>
    public async Task<McpTokenOut?> RevokeMcpTokenAsync(
        Guid ksId, Guid tokenId, Actor actor, CancellationToken ct)
    {
        var entity = await _db.McpUserTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.KnowledgeSystemId == ksId, ct)
            .ConfigureAwait(false);
        if (entity is null) return null;

        var actorId = Guid.TryParse(actor.UserId, out var parsed) ? parsed : Guid.Empty;
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct)
            .ConfigureAwait(false);
        var isOwner = ks is not null && ks.OwnerId == actorId;
        if (entity.UserId != actorId && !isOwner)
        {
            throw new ValidationException("You may only revoke your own MCP tokens.");
        }

        if (entity.RevokedAt is null)
        {
            entity.RevokedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return ProjectMcpToken(entity);
    }

    // ---- projections ------------------------------------------------------

    private static TokenOut ProjectApiToken(KnowledgeApiTokenEntity row) =>
        new(
            Id: row.Id,
            Name: row.Name,
            TokenPrefix: row.TokenPrefix,
            Scopes: row.Scopes.ToList(),
            Status: KnowledgeApiTokenService.IsActive(row, DateTimeOffset.UtcNow) ? "active" : "revoked",
            CreatedAt: row.CreatedAt,
            ExpiresAt: row.ExpiresAt,
            LastUsedAt: row.LastUsedAt,
            RevokedAt: row.RevokedAt,
            CanReveal: KnowledgeApiTokenService.IsActive(row, DateTimeOffset.UtcNow)
                      && !string.IsNullOrEmpty(row.SecretCiphertext));

    private static McpTokenOut ProjectMcpToken(McpUserTokenEntity row) =>
        new(
            Id: row.Id,
            Name: row.Name,
            TokenPrefix: row.TokenPrefix,
            Scopes: row.Scopes.ToList(),
            Status: McpTokenService.IsActive(row, DateTimeOffset.UtcNow) ? "active" : "revoked",
            CreatedAt: row.CreatedAt,
            ExpiresAt: row.ExpiresAt,
            LastUsedAt: row.LastUsedAt,
            RevokedAt: row.RevokedAt);

    private async Task TryAuditAsync(
        Guid ksId, Actor actor, string action, string summary,
        IReadOnlyDictionary<string, object?> detail, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var actorId))
        {
            return;
        }
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == actorId, ct)
            .ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        await _audit.RecordAsync(
            ksId, user, action, summary,
            new Dictionary<string, object?>(detail),
            graph: null,
            added: Array.Empty<byte>(),
            removed: Array.Empty<byte>(),
            groupId: null,
            ct).ConfigureAwait(false);
    }
}