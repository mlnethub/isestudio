using System.Text.Json;

namespace OnToPilot.Knowledge;

// ---------------------------------------------------------------------------
// Wire DTOs for /api/knowledge* (KS CRUD + membership + review stats).
// Mirrors backend/app/api/knowledge.py:KSOut / MemberOut / AddMember / UpdateKS
// so the existing frontend types stay in lock-step.
// ---------------------------------------------------------------------------

/// <summary>
/// A knowledge-system row enriched with the requesting user's
/// <c>my_role</c> so the frontend can gate write controls without
/// re-deriving permissions client-side.
/// </summary>
public sealed record KnowledgeSystemOut(
    Guid Id,
    string PublicId,
    string Name,
    string Description,
    Guid? OwnerId,
    string GraphIri,
    string BaseIri,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int ClassCount,
    int PropertyCount,
    int AxiomCount,
    string? LlmModel,
    Guid? LlmProviderId,
    Guid? EmbeddingProviderId,
    string? EmbeddingModel,
    string MyRole);

/// <summary>Body for <c>POST /api/knowledge</c>.</summary>
public sealed record CreateKnowledgeSystemRequest(
    string Name,
    string? Description,
    string? LlmModel,
    Guid? LlmProviderId,
    Guid? EmbeddingProviderId,
    string? EmbeddingModel);

/// <summary>
/// Body for <c>PATCH /api/knowledge/{ks_id}</c>. Each field is
/// three-valued: omit (null) = unchanged; empty string = clear the
/// override; non-empty = set. Mirrors Python's UpdateKS semantics.
/// </summary>
public sealed record UpdateKnowledgeSystemRequest(
    string? Name,
    string? Description,
    string? LlmModel,
    Guid? LlmProviderId,
    Guid? EmbeddingProviderId,
    string? EmbeddingModel);

/// <summary>One membership entry on a KS.</summary>
public sealed record MemberOut(
    Guid UserId,
    string Username,
    string? DisplayName,
    string Role);

/// <summary>Body for <c>POST /api/knowledge/{ks_id}/members</c>.</summary>
public sealed record AddMemberRequest(string Username, string? Role);

/// <summary>
/// One user the owner can still grant access to (id + username +
/// is_admin, no other fields). The Python backend calls these
/// "grantable users" / "candidates".
/// </summary>
public sealed record GrantableUserOut(
    Guid Id,
    string Username,
    bool IsAdmin);

/// <summary>
/// Cross-KS access + recent activity for one user. The
/// <see cref="User"/> block is a subset of <see cref="UserEntity"/>;
/// <see cref="Access"/> is the requester's visible-KS list; <see cref="Activity"/>
/// is the most-recent 30 audit events for the target user.
/// </summary>
public sealed record MemberDetailOut(
    MemberDetailUser User,
    IReadOnlyList<MemberAccessEntry> Access,
    IReadOnlyList<MemberActivityEntry> Activity);

public sealed record MemberDetailUser(
    Guid Id,
    string Username,
    string? DisplayName,
    bool IsAdmin,
    bool Active);

public sealed record MemberAccessEntry(
    Guid KsId,
    string KsName,
    string Role);

public sealed record MemberActivityEntry(
    string? KsName,
    string Action,
    string Summary,
    DateTimeOffset CreatedAt);

/// <summary>
/// Pending-item counts for the Review sidebar badges. Mirrors Python
/// <c>knowledge.review_counts</c> exactly — the previous placeholder
/// used different field names (<c>pending_conflicts</c> etc.) and would
/// have shipped a wire-shape regression.
/// </summary>
public sealed record ReviewCountsOut(
    int Conflicts,
    int Resolution,
    int Terminology,
    int Validation,
    int Total)
{
    /// <summary>Sum the four counts so callers don't have to.</summary>
    public static ReviewCountsOut Sum(int conflicts, int resolution, int terminology, int validation) =>
        new(conflicts, resolution, terminology, validation, conflicts + resolution + terminology + validation);
}