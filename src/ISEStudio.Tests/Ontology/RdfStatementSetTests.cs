using ISEStudio.Ontology;

namespace ISEStudio.Tests.Ontology;

/// <summary>
/// Tests for the pure-functional statement operations used by importers,
/// exporters, and PostgreSQL repository methods. These operations must be
/// total over their inputs and must not call any IO — every assertion here
/// runs against an in-memory list.
/// </summary>
public class RdfStatementSetTests
{
    [Fact]
    [Trait("Category", "RdfCore")]
    public void Merge_unions_two_lists_and_deduplicates()
    {
        var shared = new RdfStatement(new RdfIri("urn:s"), "urn:p", new RdfIri("urn:o"), "urn:g");
        var first = new[] { shared, new(new RdfIri("urn:a"), "urn:p", new RdfIri("urn:b"), "urn:g") };
        var second = new[] { shared, new(new RdfIri("urn:c"), "urn:p", new RdfIri("urn:d"), "urn:g") };

        var merged = RdfStatementSet.Merge(first, second);

        Assert.Equal(3, merged.Count);
        Assert.Contains(shared, merged);
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void ReplaceGraph_removes_only_target_graph_statements()
    {
        var kept = new RdfStatement(new RdfIri("urn:keep"), "urn:p", new RdfIri("urn:o"), "urn:other");
        var removed = new RdfStatement(new RdfIri("urn:drop"), "urn:p", new RdfIri("urn:o"), "urn:target");
        var replacement = new[]
        {
            new RdfStatement(new RdfIri("urn:new"), "urn:p", new RdfIri("urn:o")),
        };

        var next = RdfStatementSet.ReplaceGraph(new[] { kept, removed }, "urn:target", replacement);

        Assert.Contains(kept, next);
        Assert.DoesNotContain(removed, next);
        Assert.Single(next, s => s.GraphIri == "urn:target");
        Assert.Equal(new RdfIri("urn:new"), Assert.IsType<RdfIri>(next.Single(s => s.GraphIri == "urn:target").Subject));
    }

    [Fact]
    [Trait("Category", "RdfCore")]
    public void Diff_returns_added_and_removed_in_deterministic_order()
    {
        var before = new[]
        {
            new RdfStatement(new RdfIri("urn:a"), "urn:p", new RdfIri("urn:1"), "urn:g"),
            new RdfStatement(new RdfIri("urn:b"), "urn:p", new RdfIri("urn:2"), "urn:g"),
        };
        var after = new[]
        {
            new RdfStatement(new RdfIri("urn:b"), "urn:p", new RdfIri("urn:2"), "urn:g"),
            new RdfStatement(new RdfIri("urn:c"), "urn:p", new RdfIri("urn:3"), "urn:g"),
        };

        var (added, removed) = RdfStatementSet.Diff(before, after);

        Assert.Single(removed);
        Assert.Equal(new RdfIri("urn:a"), Assert.IsType<RdfIri>(removed[0].Subject));
        Assert.Single(added);
        Assert.Equal(new RdfIri("urn:c"), Assert.IsType<RdfIri>(added[0].Subject));
    }
}