using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Authentication;

/// <summary>
/// Recognized external (machine-to-machine) API token scopes. Mirrors the
/// Python backend's <c>backend/app/access_tokens.py</c> dictionary: 5 scopes
/// for read-only access to a knowledge system's ontology, vocabulary,
/// instances, SPARQL, and provenance layers.
/// </summary>
public static class KnowledgeApiTokenScopes
{
    /// <summary>Read ontology metadata, schema views, and RDF exports.</summary>
    public const string OntologyRead = "ontology:read";

    /// <summary>Browse and resolve controlled terminology; export SKOS RDF.</summary>
    public const string VocabularyRead = "vocabulary:read";

    /// <summary>Browse and search ABox individuals and assertions.</summary>
    public const string InstancesRead = "instances:read";

    /// <summary>Run bounded read-only SPARQL SELECT and ASK queries.</summary>
    public const string QueryRead = "query:read";

    /// <summary>Include source documents, chunks, and evidence snippets.</summary>
    public const string ProvenanceRead = "provenance:read";
}

/// <summary>
/// Request shape for <see cref="IKnowledgeApiTokenService.CreateAsync"/>.
/// </summary>
/// <param name="KnowledgeSystemId">FK to the knowledge system the token is scoped to.</param>
/// <param name="CreatedById">Optional FK to the user creating the token.</param>
/// <param name="Name">Owner-chosen label shown in management UIs.</param>
/// <param name="Scopes">Requested scopes; normalized against the canonical five.</param>
/// <param name="ExpiresAt">Optional UTC expiry; <c>null</c> means no expiry.</param>
public sealed record KnowledgeApiTokenCreateRequest(
    Guid KnowledgeSystemId,
    Guid? CreatedById,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Result of <see cref="IKnowledgeApiTokenService.CreateAsync"/>: the
/// persisted row plus the plaintext that the caller MUST surface to the user
/// exactly once.
/// </summary>
/// <param name="Entity">The persisted <see cref="KnowledgeApiTokenEntity"/>.</param>
/// <param name="Plaintext">
/// The bearer secret. The service persists only its SHA-256 hex digest; this
/// plaintext is the only opportunity to display it to the user.
/// </param>
public sealed record MintedKnowledgeApiToken(KnowledgeApiTokenEntity Entity, string Plaintext);

/// <summary>
/// Result of <see cref="IKnowledgeApiTokenService.VerifyAsync"/>: the matched
/// row plus the knowledge system it scopes to.
/// </summary>
/// <param name="Token">The persisted row matching the presented plaintext's hash.</param>
/// <param name="KnowledgeSystem">The KS the token grants access to.</param>
/// <param name="Now">The verification timestamp (for caller auditing).</param>
public sealed record KnowledgeApiTokenVerificationResult(
    KnowledgeApiTokenEntity Token,
    KnowledgeSystemEntity KnowledgeSystem,
    DateTimeOffset Now);

/// <summary>
/// Token-issuance and verification primitives for the external read-only
/// API. Bearer secrets are generated from <see cref="RandomNumberGenerator"/>
/// and stored as a SHA-256 hex digest only — the plaintext is unrecoverable
/// from the database by design.
/// </summary>
public interface IKnowledgeApiTokenService
{
    /// <summary>
    /// Mint a new token, persist only the SHA-256 hash, and return the
    /// plaintext to the caller once.
    /// </summary>
    Task<MintedKnowledgeApiToken> CreateAsync(
        KnowledgeApiTokenCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Look up a token by its plaintext (hashed, then matched on
    /// <c>TokenHash</c>). Returns <c>null</c> when the row is missing,
    /// revoked, expired, or the KS has been deleted.
    /// </summary>
    Task<KnowledgeApiTokenVerificationResult?> VerifyAsync(
        string plaintext,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IKnowledgeApiTokenService"/> implementation. Hashes
/// are SHA-256 hex digests; entropy comes from
/// <see cref="RandomNumberGenerator.GetBytes(int)"/>.
/// </summary>
/// <remarks>
/// <para>The token plaintext follows the Python backend's wire shape:
/// <c>opk_&lt;first-10-of-public-id&gt;_&lt;base64url(suffix)&gt;</c> so
/// existing client tooling that recognizes the <c>opk_</c> prefix keeps
/// working during the .NET migration.</para>
/// </remarks>
public sealed class KnowledgeApiTokenService : IKnowledgeApiTokenService
{
    /// <summary>Wire-format prefix. Must match the Python backend's <c>opk_</c>.</summary>
    public const string SchemePrefix = "opk_";

    /// <summary>Bytes of cryptographic entropy in the bearer secret suffix.</summary>
    public const int SecretBytes = 32;

    /// <summary>Length of the public-id chunk embedded in the plaintext.</summary>
    public const int PublicIdChunkLength = 10;

    private readonly ISEStudioDbContext _db;
    private readonly TimeProvider _clock;

    /// <summary>DI constructor.</summary>
    public KnowledgeApiTokenService(ISEStudioDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The five canonical external scopes, in canonical display order.
    /// </summary>
    public static IReadOnlyList<string> KnownScopes { get; } = new[]
    {
        KnowledgeApiTokenScopes.OntologyRead,
        KnowledgeApiTokenScopes.VocabularyRead,
        KnowledgeApiTokenScopes.InstancesRead,
        KnowledgeApiTokenScopes.QueryRead,
        KnowledgeApiTokenScopes.ProvenanceRead,
    };

    /// <summary>
    /// Compute the lowercase SHA-256 hex digest of the bearer secret. The
    /// service stores only this value; the plaintext cannot be recovered.
    /// </summary>
    public static string Digest(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)))
            .ToLowerInvariant();

    /// <summary>
    /// True when the row has not been revoked and either has no expiry or
    /// the expiry is still in the future.
    /// </summary>
    public static bool IsActive(KnowledgeApiTokenEntity token, DateTimeOffset now) =>
        token.RevokedAt is null && (token.ExpiresAt is null || token.ExpiresAt > now);

    /// <summary>
    /// Generate a fresh bearer secret in the Python-compatible wire format:
    /// <c>opk_&lt;first-10-of-public-id&gt;_&lt;base64url(32 random bytes)&gt;</c>.
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
    /// Filter a caller-supplied scope list down to the canonical five and
    /// preserve the canonical ordering. Drops duplicates and unknown values.
    /// </summary>
    public static IReadOnlyList<string> NormalizeScopes(IEnumerable<string> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var requestedSet = new HashSet<string>(requested, StringComparer.Ordinal);
        return KnownScopes.Where(s => requestedSet.Contains(s)).ToList();
    }

    /// <inheritdoc />
    public async Task<MintedKnowledgeApiToken> CreateAsync(
        KnowledgeApiTokenCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ks = await _db.KnowledgeSystems
            .FirstOrDefaultAsync(x => x.Id == request.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null)
        {
            throw new InvalidOperationException(
                $"Knowledge system {request.KnowledgeSystemId} not found; cannot mint a token for an unknown KS.");
        }

        var now = _clock.GetUtcNow();
        var plaintext = GeneratePlaintext(ks.PublicId);
        var entity = new KnowledgeApiTokenEntity
        {
            KnowledgeSystemId = ks.Id,
            Name = request.Name,
            TokenPrefix = plaintext[..Math.Min(16, plaintext.Length)],
            TokenHash = Digest(plaintext),
            Scopes = NormalizeScopes(request.Scopes).ToList(),
            CreatedById = request.CreatedById,
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt,
        };
        _db.KnowledgeApiTokens.Add(entity);
        // LegacyId is filled by the column DEFAULT 0 at INSERT time.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new MintedKnowledgeApiToken(entity, plaintext);
    }

    /// <inheritdoc />
    public async Task<KnowledgeApiTokenVerificationResult?> VerifyAsync(
        string plaintext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;

        var now = _clock.GetUtcNow();
        var hash = Digest(plaintext);
        var row = await _db.KnowledgeApiTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (!IsActive(row, now)) return null;

        // Resolve the KS explicitly so the bearer handler doesn't need to
        // rely on EF navigation-property population succeeding.
        var ks = await _db.KnowledgeSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == row.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null) return null;

        return new KnowledgeApiTokenVerificationResult(row, ks, now);
    }
}