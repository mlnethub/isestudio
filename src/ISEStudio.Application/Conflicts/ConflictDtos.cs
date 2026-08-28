using System.Text.Json;

namespace ISEStudio.Application.Conflicts;

// ---------------------------------------------------------------------------
// Wire DTOs for /api/knowledge/{ks_id}/conflicts* and
// /api/knowledge/{ks_id}/reconciliations*.
// ---------------------------------------------------------------------------

/// <summary>
/// One conflict row as the wire exposes it. Mirrors the Python
/// <c>Conflict</c> SQLModel field set so the existing frontend types stay
/// in lock-step. <see cref="Payload"/> is the raw stored JSON (entities +
/// resolutions), reused by the resolve flow without round-tripping through
/// the DB.
/// </summary>
public sealed record ConflictOut(
    Guid Id,
    Guid KnowledgeSystemId,
    string Signature,
    string Ctype,
    string Severity,
    string Status,
    string Title,
    string Detail,
    JsonElement? Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution);

/// <summary>
/// Body for <c>POST /api/knowledge/{ks_id}/conflicts/{cid}/resolve</c>.
/// The id refers to one of the resolutions in the conflict's payload.
/// </summary>
public sealed record ResolveConflictRequest(string ResolutionId);

/// <summary>
/// Return shape for the resolve endpoint: the conflict that was just
/// resolved, the freshly-synced open-conflict list, and a re-built ontology
/// view so the frontend can re-render without a second round-trip.
/// </summary>
public sealed record ResolveConflictResponse(
    Guid ResolvedCid,
    IReadOnlyList<ConflictOut> OpenConflicts,
    JsonElement? View);

/// <summary>
/// One evidence snippet attached to a conflicting axiom. Carries the chunk
/// text the operator needs to make a human decision; the source provenance
/// row is <c>chunk_id</c> + <c>job_id</c>.
/// </summary>
public sealed record ConflictEvidenceSource(
    Guid ChunkId,
    long ChunkIndex,
    Guid? DocumentId,
    string? Document,
    string? Folder,
    Guid? JobId,
    string Snippet);

/// <summary>
/// One evidence bundle for a single axiom key. <c>description</c> is the
/// human-readable rendering of the axiom (see Python <c>provenance.describe_axiom</c>).
/// </summary>
public sealed record ConflictEvidence(
    string AxiomKey,
    string Description,
    int SourceCount,
    IReadOnlyList<ConflictEvidenceSource> Sources);

/// <summary>
/// Body for <c>GET /api/knowledge/{ks_id}/conflicts/{cid}</c>. Wraps the
/// conflict plus the evidence bundles, ranked so the most-relevant axiom
/// comes first.
/// </summary>
public sealed record ConflictContext(
    ConflictOut Conflict,
    IReadOnlyList<ConflictEvidence> Evidence);

/// <summary>
/// One TBox reconciliation memory row (the learned agent / human decision
/// log for domain/range conflicts).
/// </summary>
public sealed record ReconciliationOut(
    Guid Id,
    Guid KnowledgeSystemId,
    string Slot,
    string PropertyLabel,
    string? PropertyIri,
    JsonElement? Candidates,
    string Choice,
    string? ChosenLabel,
    string? Reason,
    string? ResolvedBy,
    DateTimeOffset CreatedAt);

/// <summary>Paginated list response for the reconciliations endpoint.</summary>
public sealed record ReconciliationListResponse(
    IReadOnlyList<ReconciliationOut> Items,
    int Total);

/// <summary>Body for <c>PATCH /api/knowledge/{ks_id}/reconciliations/{rid}</c>.</summary>
public sealed record EditReconciliationReasonRequest(string? Reason);
