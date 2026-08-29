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
/// Output of <c>ConflictAgentStep</c>. Holds the triaged conflicts plus
/// the count of conflicts to which a recommendation was attached.
/// </summary>
public sealed record ConflictTriageResult(
    IReadOnlyList<ConflictDetection.DetectedConflict> TriagedConflicts,
    int RecommendationsAttached);

/// <summary>
/// Output of <c>StructureAgentStep</c>. Counts of isolated classes that
/// were attached to a parent + new parent classes created.
/// </summary>
public sealed record StructureAttachResult(
    int IsolatedAttached,
    int NewClassesCreated);

/// <summary>
/// Final output of <c>AgentChainPipeline</c>. Bundles the two intermediate
/// results for the orchestrator to log/expose.
/// </summary>
public sealed record AgentChainResult(
    ConflictTriageResult Triage,
    StructureAttachResult Structure);
