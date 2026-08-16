using System.Text.Json;

namespace OnToPilot.Infrastructure.Persistence.Entities;

// ---------------------------------------------------------------------------
// Releases, deployments, exports, conflicts & learned-decision queues
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable snapshot of the three governed layers for a knowledge system.
/// Versions are unique per KS.
/// </summary>
public sealed class OntologyReleaseEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Internal <c>draft-&lt;id&gt;</c> before publication; assigned a public version at publish time.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Lifecycle: <c>draft</c> | <c>reviewed</c> | <c>published</c> | <c>deleted</c>.</summary>
    public string Status { get; set; } = "draft";

    /// <summary>Short title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Long-form release notes.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Filesystem directory containing the snapshot artefacts.</summary>
    public string SnapshotDir { get; set; } = string.Empty;

    /// <summary>Manifest describing the snapshot contents.</summary>
    public JsonDocument? Manifest { get; set; }

    /// <summary>FK to the user who created the release.</summary>
    public Guid? CreatedById { get; set; }

    /// <summary>Denormalized display name of the creator.</summary>
    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>FK to the user who reviewed the release.</summary>
    public Guid? ReviewedById { get; set; }

    /// <summary>Denormalized display name of the reviewer.</summary>
    public string ReviewedByName { get; set; } = string.Empty;

    /// <summary>FK to the user who published the release.</summary>
    public Guid? PublishedById { get; set; }

    /// <summary>Denormalized display name of the publisher.</summary>
    public string PublishedByName { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC review timestamp.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>UTC publication timestamp.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>
/// Queryable read-only projection of one published release.
/// </summary>
public sealed class ReleaseDeploymentEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>FK to the <see cref="OntologyReleaseEntity"/> being deployed. Unique.</summary>
    public Guid ReleaseId { get; set; }

    /// <summary>Lifecycle: <c>provisioning</c> | <c>active</c> | <c>stopping</c> | <c>stopped</c> | <c>failed</c>.</summary>
    public string Status { get; set; } = "provisioning";

    /// <summary>Named graph IRI for the deployed TBox.</summary>
    public string TboxGraphIri { get; set; } = string.Empty;

    /// <summary>Named graph IRI for the deployed SKOS vocabulary.</summary>
    public string VocabularyGraphIri { get; set; } = string.Empty;

    /// <summary>Named graph IRI for the deployed ABox.</summary>
    public string AboxGraphIri { get; set; } = string.Empty;

    /// <summary>Number of statements in the deployed graphs.</summary>
    public int StatementCount { get; set; }

    /// <summary>Number of provenance rows associated with the deployment.</summary>
    public int ProvenanceCount { get; set; }

    /// <summary>Failure message if <see cref="Status"/> == <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC activation timestamp.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>UTC stop timestamp.</summary>
    public DateTimeOffset? StoppedAt { get; set; }
}

/// <summary>
/// Release-fixed provenance index used by immutable service endpoints.
/// </summary>
public sealed class ReleaseStatementProvenanceEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>FK to the <see cref="OntologyReleaseEntity"/> this row is fixed against.</summary>
    public Guid ReleaseId { get; set; }

    /// <summary>Either <c>tbox</c> or <c>abox</c>.</summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>Canonical statement key.</summary>
    public string StatementKey { get; set; } = string.Empty;

    /// <summary>Statement payload (provenance + evidence).</summary>
    public JsonDocument? Payload { get; set; }
}

/// <summary>
/// Asynchronous, stream-written export of one layer or a complete release bundle.
/// </summary>
public sealed class ExportJobEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>FK to the release being exported, if any.</summary>
    public Guid? ReleaseId { get; set; }

    /// <summary>Layer: <c>tbox</c> | <c>vocabulary</c> | <c>abox</c> | <c>bundle</c>.</summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>Output format (e.g. <c>nquads</c>).</summary>
    public string Format { get; set; } = "nquads";

    /// <summary>Lifecycle: <c>pending</c> | <c>running</c> | <c>completed</c> | <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Statements per shard file.</summary>
    public int ShardSize { get; set; } = 100_000;

    /// <summary>Statements written so far.</summary>
    public int ProcessedStatements { get; set; }

    /// <summary>Total statements to write.</summary>
    public int TotalStatements { get; set; }

    /// <summary>Output directory.</summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>List of produced shard descriptors.</summary>
    public JsonDocument? Files { get; set; }

    /// <summary>Failure message if <see cref="Status"/> == <c>failed</c>.</summary>
    public string? Error { get; set; }

    /// <summary>FK to the user who triggered the export.</summary>
    public Guid? CreatedById { get; set; }

    /// <summary>Denormalized display name of the trigger user.</summary>
    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC start timestamp.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>UTC finish timestamp (success or failure).</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// A detected ontology conflict/contradiction awaiting user resolution. The
/// <see cref="Signature"/> is a stable key so re-running detection does not
/// re-open a conflict the user already resolved/dismissed.
/// </summary>
public sealed class ConflictEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Stable signature derived from the conflict's nature.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>Conflict type: <c>cycle</c> | <c>disjoint_subclass</c> | <c>disjoint_common</c> | <c>domain_multi</c> | <c>range_multi</c> | <c>equiv_disjoint</c> | <c>duplicate</c>.</summary>
    public string Ctype { get; set; } = string.Empty;

    /// <summary>Either <c>error</c> or <c>warning</c>.</summary>
    public string Severity { get; set; } = "error";

    /// <summary>Lifecycle: <c>open</c> | <c>resolved</c> | <c>dismissed</c>.</summary>
    public string Status { get; set; } = "open";

    /// <summary>Short title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Long-form detail.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Payload: entities and resolution candidates.</summary>
    public JsonDocument? Payload { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC resolution timestamp.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Resolution id applied, or <c>dismissed</c>.</summary>
    public string? Resolution { get; set; }
}

/// <summary>
/// Learned, human-in-the-loop entity-resolution memory for the ABox. One row
/// = one decision about a mention (surface form of an individual, in the
/// context of a class). Both a queue (<c>status == "pending"</c>) and a
/// lookup table the extraction agent consults.
/// </summary>
public sealed class EntityResolutionEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Surface form mentioned in the source text.</summary>
    public string SurfaceForm { get; set; } = string.Empty;

    /// <summary>Class IRI the surface form was attached to.</summary>
    public string? ClassIri { get; set; }

    /// <summary>Status: <c>pending</c> | <c>matched</c> | <c>new</c> | <c>distinct</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The individual the surface form resolves to.</summary>
    public string? IndividualIri { get; set; }

    /// <summary>Agent confidence (0..1) for automatic decisions.</summary>
    public double? Confidence { get; set; }

    /// <summary>Either <c>agent</c> for automatic decisions or a username.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>FK to the chunk where the mention was observed.</summary>
    public Guid? SourceChunkId { get; set; }

    /// <summary>Candidates, evidence, notes.</summary>
    public JsonDocument? Context { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC resolution timestamp.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Human-in-the-loop terminology governance proposal. Stores only the review
/// workflow and learned decision memory (approved terminology lives as SKOS
/// RDF in the KS vocabulary graph).
/// </summary>
public sealed class TermProposalEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Stable signature derived from the proposal's nature.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>Action: <c>create</c> | <c>add_alias</c> | <c>update</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Term label.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Target concept IRI, when the proposal targets an existing term.</summary>
    public string? TargetIri { get; set; }

    /// <summary>Status: <c>pending</c> | <c>accepted</c> | <c>rejected</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Action payload (e.g. aliases to add, definition body).</summary>
    public JsonDocument? Payload { get; set; }

    /// <summary>Agent confidence (0..1).</summary>
    public double? Confidence { get; set; }

    /// <summary>Agent's free-form rationale.</summary>
    public string? Reason { get; set; }

    /// <summary>Evidence supporting the proposal.</summary>
    public JsonDocument? Evidence { get; set; }

    /// <summary>Chunk IDs providing evidence.</summary>
    public JsonDocument? SourceChunkIds { get; set; }

    /// <summary>FK to the originating <see cref="ExtractionJobEntity"/>.</summary>
    public Guid? ExtractionJobId { get; set; }

    /// <summary>Proposer identifier; defaults to <c>terminology-agent</c>.</summary>
    public string ProposedBy { get; set; } = "terminology-agent";

    /// <summary>Resolver identifier (<c>agent</c> or username).</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>Human note accompanying the resolution.</summary>
    public string? ResolutionNote { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC resolution timestamp.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Learned memory for TBox domain/range reconciliation &mdash; the analog of
/// <see cref="EntityResolutionEntity"/> for the schema. One row = a decision
/// about how a property that accrued several domains/ranges was reconciled.
/// </summary>
public sealed class TboxReconciliationEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Either <c>domain</c> or <c>range</c>.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>Property label.</summary>
    public string PropertyLabel { get; set; } = string.Empty;

    /// <summary>Property IRI.</summary>
    public string? PropertyIri { get; set; }

    /// <summary>Candidate class labels.</summary>
    public JsonDocument? Candidates { get; set; }

    /// <summary>Resolution kind: <c>common_super</c> | <c>union</c> | <c>keep</c>.</summary>
    public string Choice { get; set; } = string.Empty;

    /// <summary>Label of the chosen candidate.</summary>
    public string? ChosenLabel { get; set; }

    /// <summary>The decider's rationale (shown to humans + fed back to the agent).</summary>
    public string? Reason { get; set; }

    /// <summary>Either <c>agent</c> or a username.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Learned memory for datatype-violation fixes. One row = "for this
/// numeric-typed data property, the fix is X".
/// </summary>
public sealed class ValidationDecisionEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system.</summary>
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Property label.</summary>
    public string PropertyLabel { get; set; } = string.Empty;

    /// <summary>Property IRI.</summary>
    public string? PropertyIri { get; set; }

    /// <summary>The declared numeric type at decision time (<c>decimal</c>, <c>integer</c>, …).</summary>
    public string? XsdType { get; set; }

    /// <summary>Either <c>relax</c> or <c>remove</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Decider rationale.</summary>
    public string? Reason { get; set; }

    /// <summary>Either <c>agent</c> or a username.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}