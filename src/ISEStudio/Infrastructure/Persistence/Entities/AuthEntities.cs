namespace ISEStudio.Infrastructure.Persistence.Entities;

// ---------------------------------------------------------------------------
// Auth: users & sessions
// ---------------------------------------------------------------------------

/// <summary>
/// EF Core entity for the Python backend's <c>User</c> SQLModel. The actual
/// primary key is <see cref="LegacyAddressableEntity.Id"/> (Guid); the
/// Python integer identifier is preserved in <see cref="LegacyId"/>.
/// </summary>
public sealed class UserEntity : EntityBase
{
    /// <summary>Login handle, immutable after creation.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Optional friendly nickname shown in the UI; falls back to <see cref="Username"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>BCrypt hash of the password. Never exposed by the API.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>True if the user is treated as an administrator (full access to every KS).</summary>
    public bool IsAdmin { get; set; }

    /// <summary>False disables login without deleting the row (audit preservation).</summary>
    public bool Active { get; set; } = true;

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Server-side session: opaque token stored in an <c>HttpOnly</c> cookie.
/// In Python the token is the primary key; in the EF model the Guid
/// <see cref="LegacyAddressableEntity.Id"/> is the primary key and the token
/// is a separately indexed unique column.
/// </summary>
public sealed class AuthSessionEntity : EntityBase
{
    /// <summary>Opaque session token presented by the cookie.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>FK to the owning user.</summary>
    public Guid UserId { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC expiry timestamp; the auth handler rejects the session past this point.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Per-knowledge-system access grant. Owners have full control implicitly;
/// grants add other users as viewer (read) or editor (read + content ops).
/// </summary>
public sealed class KSGrantEntity : EntityBase
{
    /// <summary>FK to the knowledge system being granted.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>FK to the granted user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Either <c>viewer</c> or <c>editor</c>.</summary>
    public string Role { get; set; } = "viewer";

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Per-knowledge-system override for one registered model instruction prompt.
/// </summary>
public sealed class KnowledgePromptOverrideEntity : EntityBase
{
    /// <summary>FK to the knowledge system the override applies to.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Prompt key (e.g. <c>tbox.system</c>, <c>abox.system</c>).</summary>
    public string PromptKey { get; set; } = string.Empty;

    /// <summary>Prompt content (full template body).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>FK to the user who last updated this override.</summary>
    public Guid? UpdatedById { get; set; }

    /// <summary>Denormalized display name of the last updater.</summary>
    public string UpdatedByName { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC last-update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A revocable machine credential scoped to exactly one knowledge system.
/// Authentication uses the SHA-256 hash (<see cref="TokenHash"/>); the raw
/// bearer secret is never stored. The <see cref="SecretCiphertext"/> column
/// is schema-retained for Python parity but is not yet populated by the
/// .NET service — tokens remain hash-only and unrecoverable.
/// </summary>
public sealed class KnowledgeApiTokenEntity : EntityBase
{
    /// <summary>FK to the knowledge system this token is scoped to.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Navigation to the owning knowledge system.</summary>
    public KnowledgeSystemEntity? KnowledgeSystem { get; set; }

    /// <summary>Owner-chosen label.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>First few characters of the bearer secret, safe to display.</summary>
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest of the bearer secret. Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Encrypted copy of the bearer secret for owner-driven reveal.</summary>
    public string? SecretCiphertext { get; set; }

    /// <summary>Granted scopes (e.g. <c>read</c>, <c>write</c>).</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>FK to the user who created the token.</summary>
    public Guid? CreatedById { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC expiry timestamp; null = no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful use.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>UTC revocation timestamp; non-null means the token is dead.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Revocable user credential for the MCP transport, bound to one knowledge
/// system. The bearer secret is never stored — only its SHA-256 hash.
/// </summary>
public sealed class McpUserTokenEntity : EntityBase
{
    /// <summary>FK to the knowledge system this token is scoped to.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Navigation to the owning knowledge system.</summary>
    public KnowledgeSystemEntity? KnowledgeSystem { get; set; }

    /// <summary>FK to the user the token acts on behalf of.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the owning user.</summary>
    public UserEntity? User { get; set; }

    /// <summary>Owner-chosen label.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>First few characters of the bearer secret, safe to display.</summary>
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest of the bearer secret. Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Granted scopes.</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC expiry timestamp.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful use.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>UTC revocation timestamp; non-null means the token is dead.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}