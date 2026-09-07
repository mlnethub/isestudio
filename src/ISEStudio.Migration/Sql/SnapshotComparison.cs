namespace ISEStudio.Migration.Sql;

public sealed record GraphSnapshot(long RowCount, string ContentChecksum);

public sealed record SnapshotComparisonResult(
    bool IsEquivalent,
    IReadOnlyList<string> Differences);

public static class SnapshotComparison
{
    public static SnapshotComparisonResult Compare(
        SnapshotResult before,
        SnapshotResult after,
        GraphSnapshot graphBefore,
        GraphSnapshot graphAfter)
    {
        var differences = new List<string>();
        foreach (var table in before.TableCounts.Keys.Union(after.TableCounts.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            before.TableCounts.TryGetValue(table, out var beforeCount);
            after.TableCounts.TryGetValue(table, out var afterCount);
            if (beforeCount != afterCount)
            {
                differences.Add($"table {table} row count changed from {beforeCount} to {afterCount}");
            }

            before.BusinessChecksums.TryGetValue(table, out var beforeChecksum);
            after.BusinessChecksums.TryGetValue(table, out var afterChecksum);
            if (!string.Equals(beforeChecksum, afterChecksum, StringComparison.Ordinal))
            {
                differences.Add($"table {table} business checksum changed");
            }
        }

        foreach (var key in before.OrphanCounts.Keys.Union(after.OrphanCounts.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            before.OrphanCounts.TryGetValue(key, out var beforeOrphans);
            after.OrphanCounts.TryGetValue(key, out var afterOrphans);
            if (beforeOrphans != afterOrphans)
            {
                differences.Add($"foreign-key orphan count {key} changed from {beforeOrphans} to {afterOrphans}");
            }
        }

        if (graphBefore.RowCount != graphAfter.RowCount)
        {
            differences.Add($"graph row count changed from {graphBefore.RowCount} to {graphAfter.RowCount}");
        }
        if (!string.Equals(graphBefore.ContentChecksum, graphAfter.ContentChecksum, StringComparison.Ordinal))
        {
            differences.Add("graph content checksum changed");
        }

        return new SnapshotComparisonResult(differences.Count == 0, differences);
    }

    public static void AssertEquivalent(
        SnapshotResult before,
        SnapshotResult after,
        GraphSnapshot graphBefore,
        GraphSnapshot graphAfter)
    {
        var result = Compare(before, after, graphBefore, graphAfter);
        if (!result.IsEquivalent)
        {
            throw new InvalidOperationException($"Migration changed business data: {string.Join("; ", result.Differences)}");
        }
    }
}