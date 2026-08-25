namespace ISEStudio.Application.Foundation;

/// <summary>
/// Protocol-agnostic DTO returned by
/// <see cref="Integration.IIntegrationApiFacade.PreviewOntologyChangesAsync"/>.
/// Carries the exact RDF diff the caller would commit if the operations were
/// applied, without mutating the workspace. Concrete shape lands in task 2.
/// </summary>
public sealed record ChangePreview(IReadOnlyList<string> AddedTriples, IReadOnlyList<string> RemovedTriples);