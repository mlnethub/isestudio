namespace ISEStudio.Authentication;

// ---------------------------------------------------------------------------
// Wire DTOs for /api/knowledge/{ks_id}/tokens* — knowledge-API bearer
// tokens (create / list / revoke / reveal). Mirrors backend/app/api/tokens.py
// so the existing frontend types stay in lock-step with the Python baseline.
// ---------------------------------------------------------------------------

/// <summary>
/// One token row as listed by <c>GET /api/knowledge/{ks_id}/tokens</c>
/// and returned by <c>DELETE /tokens/{id}</c> on revocation. Mirrors
/// <c>backend/app/api/tokens.py:TokenOut</c>.
/// </summary>
public sealed record TokenOut(
    Guid Id,
    string Name,
    string TokenPrefix,
    IReadOnlyList<string> Scopes,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool CanReveal);

/// <summary>
/// <see cref="TokenOut"/> + the plaintext bearer that the caller MUST
/// surface to the user exactly once on create. Mirrors
/// <c>backend/app/api/tokens.py:TokenCreated</c>.
/// </summary>
public sealed record TokenCreatedOut(
    Guid Id,
    string Name,
    string TokenPrefix,
    IReadOnlyList<string> Scopes,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool CanReveal,
    string Token);

/// <summary>
/// Reveal response — plaintext only. Mirrors
/// <c>backend/app/api/tokens.py:TokenRevealed</c>.
/// </summary>
public sealed record TokenRevealedOut(string Token);

/// <summary>
/// Body for <c>POST /api/knowledge/{ks_id}/tokens</c>. Mirrors
/// <c>backend/app/api/tokens.py:TokenCreate</c>: <c>name</c> required,
/// <c>scopes</c> defaults to the canonical five,
/// <c>expires_in_days</c> defaults to 90 and is clamped to [1, 3650].
/// </summary>
public sealed record TokenCreateRequest(
    string Name,
    IReadOnlyList<string>? Scopes,
    int? ExpiresInDays);

/// <summary>
/// Wire DTOs for /api/knowledge/{ks_id}/mcp/tokens* — MCP bearer tokens
/// (create / list / revoke). Mirrors <c>backend/app/api/mcp_tokens.py</c>.
/// </summary>
public sealed record McpTokenOut(
    Guid Id,
    string Name,
    string TokenPrefix,
    IReadOnlyList<string> Scopes,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// <see cref="McpTokenOut"/> + the plaintext bearer surfaced once on
/// create + the MCP endpoint URL the client should call.
/// </summary>
public sealed record McpTokenCreatedOut(
    Guid Id,
    string Name,
    string TokenPrefix,
    IReadOnlyList<string> Scopes,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string Token,
    string Endpoint);

/// <summary>
/// List-response envelope for <c>GET /mcp/tokens</c>: the public MCP
/// endpoint, the canonical scope catalog, and the user's tokens.
/// </summary>
public sealed record McpTokenListOut(
    string Endpoint,
    IReadOnlyList<string> SupportedScopes,
    IReadOnlyList<McpTokenOut> Items);

/// <summary>
/// Body for <c>POST /api/knowledge/{ks_id}/mcp/tokens</c>. Mirrors
/// <c>backend/app/api/mcp_tokens.py:CreateMcpToken</c>: <c>name</c>
/// defaults to "Agent session", <c>scopes</c> is role-derived when null,
/// <c>expires_in_minutes</c> is optional and bounded by the
/// configured max TTL.
/// </summary>
public sealed record McpTokenCreateBody(
    string? Name,
    IReadOnlyList<string>? Scopes,
    int? ExpiresInMinutes);