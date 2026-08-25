using System.Text.Json;

namespace ISEStudio.Ontology;

public sealed record HistoryItemOut(
    Guid Id, string ActorName, string Action, string Summary,
    JsonElement? Detail, DateTimeOffset CreatedAt, bool CanRollback);

public sealed record HistoryResponseOut(IReadOnlyList<HistoryItemOut> Items, int Total);

public sealed record RollbackResponseOut(int Undone, object? View, object? OpenConflicts);
