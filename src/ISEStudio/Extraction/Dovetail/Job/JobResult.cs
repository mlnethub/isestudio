namespace ISEStudio.Extraction.Dovetail.Job;

public sealed record JobResult(
    Guid JobId,
    bool Succeeded,
    string? Error,
    long ProcessedChunks,
    IReadOnlyList<ChunkResult> TBoxChunkResults,
    IReadOnlyList<ChunkResult> ABoxChunkResults,
    JobTerminology? Terminology)
{
    public static JobResult FromJobState(JobState state) => new(
        state.JobId,
        state.Succeeded,
        state.Error,
        state.ProcessedChunks,
        state.TBoxChunkResults,
        state.ABoxChunkResults,
        state.Terminology);
}

public sealed record ChunkResult(
    int ChunkId,
    IReadOnlyList<object> ClassesAdded,
    IReadOnlyList<object> PropertiesAdded,
    IReadOnlyList<object> AxiomsAdded);

public sealed record JobTerminology(
    long TermsAdded,
    long TermsMapped,
    long ProposalsQueued,
    string? Error);