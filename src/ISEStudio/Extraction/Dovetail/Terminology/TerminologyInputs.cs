using ISEStudio.Application.Vocabulary;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.Terminology;

/// <summary>
/// Input to the terminology Dovetail pipeline. <c>Ks</c> is the pure-value
/// context (graph IRIs) the deterministic passes need; the knowledge-system
/// id and model flow to the LLM proposal pass; <c>SuggestEnabled</c> is the
/// operator switch (ISEStudioOptions.TerminologySuggestDuringExtraction)
/// folded at the orchestrator — the pipeline itself stays option-free.
/// </summary>
public sealed record TerminologyInput(
    KsContext Ks,
    Guid KnowledgeSystemId,
    string? Model,
    bool SuggestEnabled);

/// <summary>
/// Per-pass carry record threading the SyncCore state through the DAG
/// (parent-spec D3: one record per segment output). <c>View</c> is the TBox
/// snapshot (classes/properties — passes 2-4 read it); <c>PreView</c> is the
/// vocabulary SKOS view captured by the init step (pass 1 iterates it, pass 2
/// builds its conceptByMapping index from it). The per-pass counters
/// accumulate; <c>Error</c> is set by a pass step's catch and makes every
/// downstream step short-circuit (mirrors SyncAsync's whole-pass try/catch).
/// <c>Skipped</c> marks the zero paths where no view can be built
/// (<c>_store</c> null — contract-test path — or an empty ontology), in which
/// case <c>View</c>/<c>PreView</c> are null; FoldCarry restores the original
/// <see cref="TerminologyResult.Zero"/> shape from it.
/// </summary>
public sealed record TermSyncCarry(
    string? SchemeIri,
    OntologyView? View,
    SkosView? PreView,
    int PropertyCount,
    int StaleMappingsRemoved = 0,
    int TermsAdded = 0,
    int TermsMapped = 0,
    int MappingConflicts = 0,
    int AliasesAdded = 0,
    int BroaderAdded = 0,
    string? Error = null,
    bool Skipped = false);
