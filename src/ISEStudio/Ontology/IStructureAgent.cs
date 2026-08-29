namespace ISEStudio.Ontology;

/// <summary>
/// Agent that attaches isolated classes to broader parents. Thin interface
/// over <see cref="StructureAgent"/> for testability (concrete class is
/// sealed with non-virtual methods). Slice 3 spec §5 D6.
/// </summary>
public interface IStructureAgent
{
    /// <summary>
    /// Run isolated-class attachment. <c>maxSameParent</c> is read internally
    /// from <c>ISEStudioOptions.StructureMaxSameParent</c> — not a parameter.
    /// Returns the job-log summary lines for the extraction job.
    /// </summary>
    Task<IReadOnlyList<string>> AttachIsolatedAsync(
        Guid ksId,
        string? model,
        CancellationToken ct,
        bool skipActiveExtractionGate = false);
}