using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Authentication;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the seven token dispatcher arms (12/13
/// slice): tokens.list / create / revoke / reveal + mcp_tokens.list /
/// create / revoke. Unpacks the <see cref="InternalRequest"/> envelope
/// (body DTOs, KnowledgeSystemGuid, ResourceId token Guid), delegates
/// to the scoped <see cref="TokenManagementService"/>, and owns the
/// snake_case wire projections. Missing body throws
/// <see cref="InvalidOperationException"/>; a missing KS / token id
/// returns <c>null</c> for the dispatcher's per-arm fallback — both
/// matching the pre-split helpers.
/// </summary>
public sealed class TokenApplicationService : ITokenApplicationService
{
    private readonly TokenManagementService _tokens;

    public TokenApplicationService(TokenManagementService tokens)
    {
        _tokens = tokens;
    }

    public async Task<object?> ListTokensAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var rows = await _tokens.ListApiTokensAsync(
                request.KnowledgeSystemGuid.Value, ct)
            .ConfigureAwait(false);
        return (object?)rows;
    }

    public async Task<object?> CreateTokenAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<TokenCreateRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for tokens.create.");
        if (request.KnowledgeSystemGuid is null) return null;
        var row = await _tokens.CreateApiTokenAsync(
                request.KnowledgeSystemGuid.Value, body, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)ProjectTokenCreatedOut(row);
    }

    public async Task<object?> RevokeTokenAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var tokenId))
        {
            return null;
        }
        var row = await _tokens.RevokeApiTokenAsync(
                request.KnowledgeSystemGuid.Value, tokenId, request.Actor, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            // No row matched (KS/token mismatch): empty envelope.
            return (object?)new { id = Guid.Empty, name = string.Empty, token = string.Empty };
        }
        return (object?)ProjectTokenOut(row);
    }

    public async Task<object?> RevealTokenAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var tokenId))
        {
            return null;
        }
        var row = await _tokens.RevealApiTokenAsync(
                request.KnowledgeSystemGuid.Value, tokenId, request.Actor, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            // Missing row or secret-ciphertext unavailable: empty
            // envelope matches the Python "legacy token cannot be
            // recovered" / 404 path semantics at the wire level.
            return (object?)new { id = Guid.Empty, plaintext = string.Empty };
        }
        return (object?)new { token = row.Token };
    }

    public async Task<object?> ListMcpTokensAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null) return null;
        var actorId = Guid.TryParse(request.Actor.UserId, out var parsed)
            ? parsed : Guid.Empty;
        var row = await _tokens.ListMcpTokensAsync(
                request.KnowledgeSystemGuid.Value, actorId, ct)
            .ConfigureAwait(false);
        return (object?)row;
    }

    public async Task<object?> CreateMcpTokenAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<McpTokenCreateBody>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for mcp_tokens.create.");
        if (request.KnowledgeSystemGuid is null) return null;
        var row = await _tokens.CreateMcpTokenAsync(
                request.KnowledgeSystemGuid.Value, body, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)ProjectMcpTokenCreatedOut(row);
    }

    public async Task<object?> RevokeMcpTokenAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var tokenId))
        {
            return null;
        }
        var row = await _tokens.RevokeMcpTokenAsync(
                request.KnowledgeSystemGuid.Value, tokenId, request.Actor, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return (object?)new { id = Guid.Empty, name = string.Empty, plaintext = string.Empty };
        }
        return (object?)ProjectMcpTokenOut(row);
    }

    private static object ProjectTokenOut(TokenOut row) => new
    {
        id = row.Id,
        name = row.Name,
        token_prefix = row.TokenPrefix,
        scopes = row.Scopes,
        status = row.Status,
        created_at = row.CreatedAt,
        expires_at = row.ExpiresAt,
        last_used_at = row.LastUsedAt,
        revoked_at = row.RevokedAt,
        can_reveal = row.CanReveal,
    };

    private static object ProjectTokenCreatedOut(TokenCreatedOut row) => new
    {
        id = row.Id,
        name = row.Name,
        token_prefix = row.TokenPrefix,
        scopes = row.Scopes,
        status = row.Status,
        created_at = row.CreatedAt,
        expires_at = row.ExpiresAt,
        last_used_at = row.LastUsedAt,
        revoked_at = row.RevokedAt,
        can_reveal = row.CanReveal,
        token = row.Token,
    };

    private static object ProjectMcpTokenOut(McpTokenOut row) => new
    {
        id = row.Id,
        name = row.Name,
        token_prefix = row.TokenPrefix,
        scopes = row.Scopes,
        status = row.Status,
        created_at = row.CreatedAt,
        expires_at = row.ExpiresAt,
        last_used_at = row.LastUsedAt,
        revoked_at = row.RevokedAt,
    };

    private static object ProjectMcpTokenCreatedOut(McpTokenCreatedOut row) => new
    {
        id = row.Id,
        name = row.Name,
        token_prefix = row.TokenPrefix,
        scopes = row.Scopes,
        status = row.Status,
        created_at = row.CreatedAt,
        expires_at = row.ExpiresAt,
        last_used_at = row.LastUsedAt,
        revoked_at = row.RevokedAt,
        token = row.Token,
        endpoint = row.Endpoint,
    };
}
