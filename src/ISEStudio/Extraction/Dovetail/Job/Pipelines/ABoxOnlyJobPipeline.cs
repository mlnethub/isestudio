using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.Job.Steps;

namespace ISEStudio.Extraction.Dovetail.Job.Pipelines;

/// <summary>
/// Dovetail Job pipeline for <see cref="JobKind.ABoxOnly"/>: the canonical
/// 6-segment chain with the <see cref="NoOpSegment{TIn, TOut}"/> adapter
/// substituting for the TBox layer (the 2-arity pipeline-entry slot) and
/// the <see cref="NoOpSegment{TIn, T1, TOut}"/> adapter + the
/// <see cref="ChainAdapter{TIn, T1, TOut}"/> wrapping the ABox layer step
/// at slot 5 (the 3-arity slot with HierarchyCarry predecessor).
///
/// <para>Slice 5 Task 4 R7 / R13 — same canonical chain and
/// <c>IPipeline&lt;JobState, TerminologyCarry&gt;</c> shape as the
/// TBox-only pipeline; only the step slot assignments differ.</para>
/// </summary>
public partial class ABoxOnlyJobPipeline : IPipeline<JobState, TerminologyCarry>
{
    public ABoxOnlyJobPipeline(
        [Segment] NoOpSegment<JobState, TBoxLayerCarry> noOpTBox,
        [Segment] NoOpSegment<TBoxLayerCarry, AgentCarry> noOpAgent,
        [Segment] NoOpSegment<AgentCarry, CorpusCarry> noOpCorpus,
        [Segment] NoOpSegment<CorpusCarry, HierarchyCarry> noOpHierarchy,
        [Segment] ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry> aboxLayer,
        [Segment] ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry> terminology)
    {
        NoOpTBox = noOpTBox;
        NoOpAgent = noOpAgent;
        NoOpCorpus = noOpCorpus;
        NoOpHierarchy = noOpHierarchy;
        ABoxLayer = aboxLayer;
        Terminology = terminology;
    }

    public NoOpSegment<JobState, TBoxLayerCarry> NoOpTBox { get; }
    public NoOpSegment<TBoxLayerCarry, AgentCarry> NoOpAgent { get; }
    public NoOpSegment<AgentCarry, CorpusCarry> NoOpCorpus { get; }
    public NoOpSegment<CorpusCarry, HierarchyCarry> NoOpHierarchy { get; }
    public ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry> ABoxLayer { get; }
    public ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry> Terminology { get; }
}
