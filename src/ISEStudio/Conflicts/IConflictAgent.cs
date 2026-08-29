namespace ISEStudio.Conflicts;

/// <summary>
/// Agent that triages detected conflicts and attaches LLM recommendations.
/// Thin interface over <see cref="ConflictAgent"/> for testability (the
/// concrete class is sealed with non-virtual methods, so inheritance-based
/// fakes cannot be used). Slice 3 spec §5 D6 — interface-keyed DI.
/// </summary>
public interface IConflictAgent
{
    /// <summary>
    /// Run the agent's ReAct triage loop. Returns the job-log summary lines
    /// for the extraction job (one entry per significant decision).
    /// </summary>
    Task<IReadOnlyList<string>> TriageAsync(
        Guid ksId,
        CancellationToken ct,
        string? model = null,
        bool skipActiveExtractionGate = false);
}