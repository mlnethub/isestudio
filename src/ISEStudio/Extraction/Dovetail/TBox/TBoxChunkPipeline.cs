using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox.Steps;

namespace ISEStudio.Extraction.Dovetail.TBox;

/// <summary>
/// One chunk's TBox verify pipeline: critic → adjudicator → denotation → merge.
/// Dovetail source generator infers a partial-order DAG from the four
/// [Segment] parameters and emits a generated <c>ExecuteAsync</c> with a
/// Mermaid diagram as an XML doc comment.
/// <![CDATA[
/// graph TD
///   critic --> adjudicator
///   critic --> denotation
///   adjudicator --> denotation
///   adjudicator --> merge
///   denotation --> merge
/// ]]>
/// </summary>
public partial class TBoxChunkPipeline(
    [Segment] CriticStep critic,
    [Segment] AdjudicatorStep adjudicator,
    [Segment] DenotationStep denotation,
    [Segment] ChunkMergeStep merge) : IPipeline<TBoxChunkInput, TBoxVerifyResult>;