using System.Text.Json;
using ISEStudio.Application.Conflicts;
using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.History;

public sealed record HistoryItemOut(
    Guid Id,
    string ActorName,
    string Action,
    string Summary,
    JsonElement? Detail,
    DateTimeOffset CreatedAt,
    bool CanRollback);

public sealed record HistoryResponseOut(
    IReadOnlyList<HistoryItemOut> Items,
    int Total);

/// <summary>
/// Result of a rollback: how many audit events were applied, the
/// post-rollback ontology view (re-built so the frontend can re-render
/// without a second round-trip), and the freshly synced open-conflict
/// list (only populated when the TBox graph actually changed during
/// the rollback — empty otherwise).
/// </summary>
public sealed record RollbackResponseOut(
    int Undone,
    OntologyResponse? View,
    IReadOnlyList<ConflictOut>? OpenConflicts);