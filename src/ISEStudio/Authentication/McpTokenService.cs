using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Authentication;

/// <summary>
/// Recognized MCP user-token scopes. Mirrors the Python backend's
/// <c>backend/app/mcp_tokens.py</c> dictionary: 3 scopes for the MCP
/// transport.
/// </summary>
public static class McpTokenScopes
{
    /// <summary>Read knowledge, evidence, queues, history, and releases.</summary>
    public const string McpRead = "mcp:read";

    /// <summary>Apply content edits and resolve review items.</summary>
    public const string McpWrite = "mcp:write";

    /// <summary>Run lifecycle and destructive owner-level operations.</summary>
    public const string McpManage = "mcp:manage";
}

/// <summary>
/// Request shape for <see cref="IMcpTokenService.CreateAsync"/>. Unlike
/// <see cref="KnowledgeApiTokenCreateRequest"/>, an MCP token is bound to a
/// specific user and inherits that user's role on the knowledge system.
/// </summary>
/// <param name="KnowledgeSystemId">FK to the knowledge system the token is scoped to.</param>
/// <param name="UserId">FK to the user the token acts on behalf of.</param>
/// <param name="Name">Owner-chosen label shown in management UIs.</param>
/// <param name="Scopes">Requested scopes; normalized against the canonical three.</param>
/// <param name="ExpiresAt">UTC expiry timestamp; required for MCP tokens.</param>
public sealed record McpTokenCreateRequest(
    Guid KnowledgeSystemId,
    Guid UserId,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Result of <see cref="IMcpTokenService.CreateAsync"/>.
/// </summary>
/// <param name="Entity">The persisted <see cref="McpUserTokenEntity"/>.</param>
/// <param name="Plaintext">
/// The bearer secret. Only the SHA-256 hash is persisted; this plaintext is
/// the only opportunity to display it to the user.
/// </param>
public sealed record MintedMcpToken(McpUserTokenEntity Entity, string Plaintext);

/// <summary>
/// Result of <see cref="IMcpTokenService.VerifyAsync"/>.
/// </summary>
/// <param name="Token">The persisted row matching the presented plaintext's hash.</param>
/// <param name="User">The user the token acts on behalf of.</param>
/// <param name="KnowledgeSystem">The KS the token grants access to.</param>
/// <param name="Now">The verification timestamp.</param>
public sealed record McpTokenVerificationResult(
    McpUserTokenEntity Token,
    UserEntity User,
    KnowledgeSystemEntity KnowledgeSystem,
    DateTimeOffset Now);

/// <summary>
/// Token-issuance and verification primitives for the MCP transport. Same
/// SHA-256-only persistence rules as <see cref="KnowledgeApiTokenService"/>:
/// the plaintext is unrecoverable from the database by design.
/// </summary>
public interface IMcpTokenService
{
    /// <summary>Mint a new MCP user token and return the plaintext exactly once.</summary>
    Task<MintedMcpToken> CreateAsync(McpTokenCreateRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Look up the token by its plaintext hash, confirm the bound user and
    /// KS still exist, and confirm the row is active. Returns <c>null</c>
    /// when any precondition fails.
    /// </summary>
    Task<McpTokenVerificationResult?> VerifyAsync(string plaintext, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IMcpTokenService"/> implementation. Mirrors the
/// Python backend's <c>backend/app/mcp_tokens.py</c>: the wire-format
/// prefix is <c>opm_</c>; the digest is SHA-256 hex; the canonical scope
/// set is the three MCP scopes.
/// </summary>
public sealed class McpTokenService : IMcpTokenService
{
    /// <summary>Wire-format prefix. Must match the Python backend's <c>opm_</c>.</summary>
    public const string SchemePrefix = "opm_";

    /// <summary>Bytes of cryptographic entropy in the bearer secret suffix.</summary>
    public const int SecretBytes = 32;

    /// <summary>Length of the public-id chunk embedded in the plaintext.</summary>
    public const int PublicIdChunkLength = 10;

    private readonly ISEStudioDbContext _db;
    private readonly TimeProvider _clock;

    /// <summary>DI constructor.</summary>
    public McpTokenService(ISEStudioDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The three canonical MCP scopes, in canonical display order.
    /// </summary>
    public static IReadOnlyList<string> KnownScopes { get; } = new[]
    {
        McpTokenScopes.McpRead,
        McpTokenScopes.McpWrite,
        McpTokenScopes.McpManage,
    };

    /// <summary>
    /// Compute the lowercase SHA-256 hex digest of the bearer secret. The
    /// service stores only this value; the plaintext cannot be recovered.
    /// </summary>
    public static string Digest(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)))
            .ToLowerInvariant();

    /// <summary>True when the row has not been revoked and the expiry is in the future.</summary>
    public static bool IsActive(McpUserTokenEntity token, DateTimeOffset now) =>
        token.RevokedAt is null && token.ExpiresAt > now;

    /// <summary>
    /// Generate a fresh bearer secret in the Python-compatible wire format:
    /// <c>opm_&lt;first-10-of-public-id&gt;_&lt;base64url(32 random bytes)&gt;</c>.
    /// </summary>
    public static string GeneratePlaintext(string publicId)
    {
        ArgumentNullException.ThrowIfNull(publicId);
        Span<byte> buffer = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(buffer);
        var suffix = Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var prefixChunk = publicId.Length <= PublicIdChunkLength
            ? publicId
            : publicId[..PublicIdChunkLength];
        return $"{SchemePrefix}{prefixChunk}_{suffix}";
    }

    /// <summary>
    /// Filter a caller-supplied scope list down to the canonical three and
    /// preserve the canonical ordering.
    /// </summary>
    public static IReadOnlyList<string> NormalizeScopes(IEnumerable<string> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var requestedSet = new HashSet<string>(requested, StringComparer.Ordinal);
        return KnownScopes.Where(s => requestedSet.Contains(s)).ToList();
    }

    /// <inheritdoc />
    public async Task<MintedMcpToken> CreateAsync(
        McpTokenCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ks = await _db.KnowledgeSystems
            .FirstOrDefaultAsync(x => x.Id == request.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null)
        {
            throw new InvalidOperationException(
                $"Knowledge system {request.KnowledgeSystemId} not found; cannot mint an MCP token for an unknown KS.");
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(x => x.Id == request.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            throw new InvalidOperationException(
                $"User {request.UserId} not found; cannot mint an MCP token for an unknown user.");
        }

        var plaintext = GeneratePlaintext(ks.PublicId);
        var entity = new McpUserTokenEntity
        {
            KnowledgeSystemId = ks.Id,
            UserId = user.Id,
            Name = request.Name,
            TokenPrefix = plaintext[..Math.Min(16, plaintext.Length)],
            TokenHash = Digest(plaintext),
            Scopes = NormalizeScopes(request.Scopes).ToList(),
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = request.ExpiresAt,
        };
        _db.McpUserTokens.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new MintedMcpToken(entity, plaintext);
    }

    /// <inheritdoc />
    public async Task<McpTokenVerificationResult?> VerifyAsync(
        string plaintext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;

        var now = _clock.GetUtcNow();
        var hash = Digest(plaintext);
        var row = await _db.McpUserTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (!IsActive(row, now)) return null;

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == row.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null || !user.Active) return null;

        var ks = await _db.KnowledgeSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == row.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null) return null;

        return new McpTokenVerificationResult(row, user, ks, now);
    }
}