using System.Collections.Concurrent;

namespace ISEStudio.Tests.Persistence;

/// <summary>
/// Shared, process-wide legacy-id allocator used by tests that need to seed
/// entities that carry a unique <c>legacy_id</c> column. The production
/// Postgres schema assigns legacy ids via stored sequences on first boot;
/// SQLite has no per-table sequence, so each row needs an explicit value.
/// </summary>
public static class TestLegacyIds
{
    private static readonly ConcurrentDictionary<string, long> _counters = new();

    /// <summary>Allocate a unique legacy id for the given entity table.</summary>
    /// <param name="table">Logical table name (e.g. <c>"users"</c>).</param>
    public static long Next(string table) =>
        _counters.AddOrUpdate(table, 1, (_, current) => current + 1);
}
