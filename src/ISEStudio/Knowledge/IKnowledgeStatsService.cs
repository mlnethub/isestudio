namespace ISEStudio.Knowledge;

/// <summary>
/// Service that recomputes <c>ClassCount</c>/<c>PropertyCount</c>/<c>AxiomCount</c>
/// on a knowledge system. Thin interface over <see cref="KnowledgeStatsService"/>
/// for testability (concrete class is sealed with non-virtual methods and
/// a non-parameterless ctor). Slice 3 spec §5 D6.
/// </summary>
public interface IKnowledgeStatsService
{
    /// <summary>
    /// Refresh the cached stats for the given knowledge system. Fail-soft
    /// callers must catch exceptions themselves; this method propagates errors.
    /// </summary>
    Task RefreshAsync(Guid ksId, CancellationToken ct);
}