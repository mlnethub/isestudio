using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox.Steps;

namespace ISEStudio.Extraction.Dovetail.ABox;

/// <summary>
/// Job-level ABox pipeline: candidate gathering → embedding match →
/// LLM judge → merge apply → cascade retype → final merge.
/// <![CDATA[
/// graph TD
///   gather --> embed
///   embed --> judge
///   judge --> merge
///   merge --> cascade
///   cascade --> final
/// ]]>
/// </summary>
public partial class ABoxJobPipeline(
    [Segment] CandidateGatherStep gather,
    [Segment] EmbeddingMatchStep embed,
    [Segment] LLMJudgeStep judge,
    [Segment] MergeApplyStep merge,
    [Segment] CascadeRetypeStep cascade,
    [Segment] FinalMergeStep final) : IPipeline<ABoxJobInput, ABoxJobResult>;
