using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.Job.Steps;

namespace ISEStudio.Extraction.Dovetail.Job.Pipelines;

/// <summary>
/// Dovetail Job pipeline for <see cref="JobKind.TBoxOnly"/>: the canonical
/// 6-segment chain
/// <c>JobState → TBoxLayerCarry → AgentCarry → CorpusCarry →
/// HierarchyCarry → ABoxLayerCarry → TerminologyCarry</c> with the
/// <see cref="ChainAdapter{TIn, T1, TOut}"/> wrapping each 2-arity Task 3
/// step into its proper 3-arity slot (positions 2..N need a predecessor
/// carry) and the <see cref="NoOpSegment{TIn, T1, TOut}"/> substituting for
/// the agent-chain slot and the ABox slot (TBox-only runs have no agent
/// chain and no ABox layer).
///
/// <para>Slice 5 Task 4 R7 (canonical chain order), R8 (no step variants —
/// the 2-arity Task 3 shapes are wrapped, not duplicated) and R13 (Dovetail
/// shape <c>IPipeline&lt;JobState, TerminologyCarry&gt;</c>, not the brief's
/// <c>IPipeline&lt;JobInput, JobResult&gt;</c> — the first segment input is
/// <see cref="JobState"/>).</para>
/// </summary>
public partial class TBoxOnlyJobPipeline : IPipeline<JobState, TerminologyCarry>
{
    public TBoxOnlyJobPipeline(
        [Segment] TBoxLayerStep tboxLayer,
        [Segment] NoOpSegment<TBoxLayerCarry, AgentCarry> noOpAgent,
        [Segment] ChainAdapter<JobState, AgentCarry, CorpusCarry> corpus,
        [Segment] ChainAdapter<JobState, CorpusCarry, HierarchyCarry> hierarchy,
        [Segment] NoOpSegment<HierarchyCarry, ABoxLayerCarry> noOpABox,
        [Segment] ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry> terminology)
    {
        TBoxLayer = tboxLayer;
        NoOpAgent = noOpAgent;
        Corpus = corpus;
        Hierarchy = hierarchy;
        NoOpABox = noOpABox;
        Terminology = terminology;
    }

    public TBoxLayerStep TBoxLayer { get; }
    public NoOpSegment<TBoxLayerCarry, AgentCarry> NoOpAgent { get; }
    public ChainAdapter<JobState, AgentCarry, CorpusCarry> Corpus { get; }
    public ChainAdapter<JobState, CorpusCarry, HierarchyCarry> Hierarchy { get; }
    public NoOpSegment<HierarchyCarry, ABoxLayerCarry> NoOpABox { get; }
    public ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry> Terminology { get; }
}
