using System.Text.Json;

namespace OnToPilot.Infrastructure.Persistence.Entities;

// ---------------------------------------------------------------------------
// Extraction jobs & provenance
// ---------------------------------------------------------------------------

/// <summary>
/// One run of TBox extraction: a set of chunks &rarr; ontology axioms for a
/// knowledge system. Reused for ABox extraction via the <see cref="Kind"/>
/// column.
/// </summary>
public sealed class ExtractionJobEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Either <c>tbox</c> or <c>abox</c>.</summary>
    public string Kind { get; set; } = "tbox";

    /// <summary>Lifecycle status: <c>pending</c> | <c>running</c> | <c>completed</c> | <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Model identifier used for this run.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Immutable effective prompt contents used by this run.</summary>
    public JsonDocument? PromptSnapshot { get; set; }

    /// <summary>Chunk IDs included in this run (ordered).</summary>
    public List<int> ChunkIds { get; set; } = new();

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC finish timestamp (success or failure).</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Free-form run log.</summary>
    public string Log { get; set; } = string.Empty;

    /// <summary>Failure message if <see cref="Status"/> == <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>Total chunks scheduled for this run.</summary>
    public int TotalChunks { get; set; }

    /// <summary>Chunks processed so far (live progress).</summary>
    public int ProcessedChunks { get; set; }

    /// <summary>TBox metric: OWL classes added.</summary>
    public int ClassesAdded { get; set; }

    /// <summary>TBox metric: OWL properties added.</summary>
    public int PropertiesAdded { get; set; }

    /// <summary>TBox metric: axioms added.</summary>
    public int AxiomsAdded { get; set; }

    /// <summary>ABox metric: individuals added.</summary>
    public int IndividualsAdded { get; set; }

    /// <summary>ABox metric: assertions added.</summary>
    public int AssertionsAdded { get; set; }

    /// <summary>ABox metric: mentions sent to the manual resolution queue.</summary>
    public int PendingAdded { get; set; }

    /// <summary>ABox metric: <c>{label: times_seen}</c> for classes not yet in the TBox.</summary>
    public JsonDocument? UnknownClasses { get; set; }

    /// <summary>Live stage indicator: <c>tbox</c> | <c>abox</c> | <c>terminology</c> | <c>finalizing</c>.</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Terminology metric: terms added.</summary>
    public int TermsAdded { get; set; }

    /// <summary>Terminology metric: terms mapped to existing concepts.</summary>
    public int TermsMapped { get; set; }

    /// <summary>Terminology metric: proposals queued for review.</summary>
    public int TerminologyProposals { get; set; }

    /// <summary>Terminology-stage error, if any.</summary>
    public string? TerminologyError { get; set; }
}

/// <summary>
/// Links an ontology axiom (by canonical key) back to the chunk/job that
/// produced it.
/// </summary>
public sealed class AxiomProvenanceEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Canonical string key (e.g. <c>subClassOf|dog|animal</c>).</summary>
    public string AxiomKey { get; set; } = string.Empty;

    /// <summary>FK to the originating chunk, if known.</summary>
    public Guid? ChunkId { get; set; }

    /// <summary>FK to the originating <see cref="ExtractionJobEntity"/>.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Provenance method (e.g. <c>extraction</c>).</summary>
    public string Method { get; set; } = "extraction";

    /// <summary>Denormalized actor display name.</summary>
    public string ActorName { get; set; } = string.Empty;

    /// <summary>FK to the <see cref="AuditEventEntity"/> that captured this change.</summary>
    public Guid? AuditEventId { get; set; }

    /// <summary>Free-form review / override record.</summary>
    public JsonDocument? ReviewRecord { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Links an ABox fact (an individual, or a data/object assertion by canonical
/// key) back to the chunk/job that produced it. A fact may have several rows:
/// many chunks can mention the same individual or assert the same value, so
/// ABox provenance is multi-source by design.
/// </summary>
public sealed class AboxProvenanceEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Canonical key: <c>ind|&lt;iri&gt;</c> | <c>data|&lt;sub&gt;|&lt;prop&gt;|&lt;value&gt;</c> | <c>obj|&lt;sub&gt;|&lt;prop&gt;|&lt;target&gt;</c>.</summary>
    public string FactKey { get; set; } = string.Empty;

    /// <summary>FK to the originating chunk, if known.</summary>
    public Guid? ChunkId { get; set; }

    /// <summary>FK to the originating <see cref="ExtractionJobEntity"/>.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Provenance method (e.g. <c>extraction</c>).</summary>
    public string Method { get; set; } = "extraction";

    /// <summary>Denormalized actor display name.</summary>
    public string ActorName { get; set; } = string.Empty;

    /// <summary>FK to the <see cref="AuditEventEntity"/> that captured this change.</summary>
    public Guid? AuditEventId { get; set; }

    /// <summary>Free-form review / override record.</summary>
    public JsonDocument? ReviewRecord { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Append-only change log for a knowledge system: who did what, when, with
/// details. The optional <see cref="Added"/> / <see cref="Removed"/> blobs
/// store gzipped N-Triples for graph rollback.
/// </summary>
public sealed class AuditEventEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>FK to the acting user, if any.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Denormalized actor display name.</summary>
    public string ActorName { get; set; } = string.Empty;

    /// <summary>Dotted action string (e.g. <c>ontology.edit</c>, <c>conflict.resolve</c>).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Short human-readable summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Operation payload kept for future rollback.</summary>
    public JsonDocument? Detail { get; set; }

    /// <summary>Named graph the diff applies to; null = the KS TBox graph (back-compat).</summary>
    public string? Graph { get; set; }

    /// <summary>Group ID linking cross-graph events from a single user action.</summary>
    public string? GroupId { get; set; }

    /// <summary>Gzipped N-Triples of triples added by this event (rollback payload).</summary>
    public byte[]? Added { get; set; }

    /// <summary>Gzipped N-Triples of triples removed by this event (rollback payload).</summary>
    public byte[]? Removed { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}