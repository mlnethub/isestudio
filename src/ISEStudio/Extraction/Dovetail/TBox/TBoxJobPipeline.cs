using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.TBox.Steps;

namespace ISEStudio.Extraction.Dovetail.TBox;

/// <summary>
/// Job-level TBox pipeline: chunk pass-through → corpus recovery →
/// hierarchy recovery → merge.
/// <![CDATA[
/// graph TD
///   chunk --> corpus
///   chunk --> hierarchy
///   chunk --> merge
///   corpus --> merge
///   hierarchy --> merge
/// ]]>
/// </summary>
public partial class TBoxJobPipeline(
    [Segment] ChunkPipelineStep chunk,
    [Segment] CorpusRecoveryStep corpus,
    [Segment] HierarchyRecoveryStep hierarchy,
    [Segment] JobMergeStep merge) : IPipeline<TBoxJobInput, TBoxJobResult>;
