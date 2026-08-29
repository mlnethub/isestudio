using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.AgentChain;

/// <summary>
/// Input to the agent chain Dovetail pipeline. Conflicts are detected
/// externally by <c>ConflictService.DetectAsync</c> (per Slice 3 spec §5 D1)
/// and passed in here; the pipeline runs ConflictAgent → StructureAgent →
/// StatsRefresh as three typed segments.
/// </summary>
public sealed record AgentChainInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts,
    string? Model);

/// <summary>
/// Output of <c>ConflictAgentStep</c>. Holds the job-log summary lines
/// produced by <see cref="ISEStudio.Conflicts.ConflictAgent.TriageAsync"/>.
/// Note: P1-1's agent returns <c>Task&lt;IReadOnlyList&lt;string&gt;&gt;</c>
/// (job-log summary, NOT a typed count). Records faithfully wrap the real
/// return shape so DOVE006 is satisfied without semantic distortion.
/// </summary>
public sealed record ConflictTriageResult(
    IReadOnlyList<string> TriageLog);

/// <summary>
/// Output of <c>StructureAgentStep</c>. Holds the job-log summary lines
/// produced by <see cref="ISEStudio.Ontology.StructureAgent.AttachIsolatedAsync"/>.
/// Same caveat as <see cref="ConflictTriageResult"/>.
/// </summary>
public sealed record StructureAttachResult(
    IReadOnlyList<string> AttachLog);

/// <summary>
/// Final output of <c>AgentChainPipeline</c>. Bundles the two intermediate
/// results for the orchestrator to log/expose.
/// </summary>
public sealed record AgentChainResult(
    ConflictTriageResult Triage,
    StructureAttachResult Structure);