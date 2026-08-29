using Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.Job.Steps;

namespace ISEStudio.Extraction.Dovetail.Job.Pipelines;

/// <summary>
/// Dovetail Job pipeline for <see cref="JobKind.Combined"/>: the canonical
/// 6-segment chain with all six real step classes wired
/// (<c>TBoxLayerStep</c> → <c>AgentStep</c> → <c>CorpusStep</c> →
/// <c>HierarchyStep</c> → <c>ABoxLayerStep</c> → <c>TerminologyStep</c>).
/// Each non-first slot wraps the 2-arity Task 3 step in a
/// <see cref="ChainAdapter{TIn, T1, TOut}"/> with a
/// <c>carry =&gt; carry.State</c> mapper so the inner step observes the
/// post-previous-step <see cref="JobState"/>.
///
/// <para>Mirrors the legacy <c>CombinedRunnerAsync</c> control flow exactly.
///
/// <para>Slice 5 Task 4 R7 / R13 — same canonical chain and
/// <c>IPipeline&lt;JobState, TerminologyCarry&gt;</c> shape as the
/// single-layer pipelines; the only difference is that no slot is
/// substituted with <c>NoOpSegment&lt;,&gt;</c>.</para>
/// </summary>
public partial class CombinedJobPipeline : IPipeline<JobState, TerminologyCarry>
{
    public CombinedJobPipeline(
        [Segment] TBoxLayerStep tboxLayer,
        [Segment] ChainAdapter<JobState, TBoxLayerCarry, AgentCarry> agent,
        [Segment] ChainAdapter<JobState, AgentCarry, CorpusCarry> corpus,
        [Segment] ChainAdapter<JobState, CorpusCarry, HierarchyCarry> hierarchy,
        [Segment] ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry> aboxLayer,
        [Segment] ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry> terminology)
    {
        TBoxLayer = tboxLayer;
        Agent = agent;
        Corpus = corpus;
        Hierarchy = hierarchy;
        ABoxLayer = aboxLayer;
        Terminology = terminology;
    }

    public TBoxLayerStep TBoxLayer { get; }
    public ChainAdapter<JobState, TBoxLayerCarry, AgentCarry> Agent { get; }
    public ChainAdapter<JobState, AgentCarry, CorpusCarry> Corpus { get; }
    public ChainAdapter<JobState, CorpusCarry, HierarchyCarry> Hierarchy { get; }
    public ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry> ABoxLayer { get; }
    public ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry> Terminology { get; }
}
