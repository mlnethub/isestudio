using OnToPilot.Ontology;
using Oxigraph;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoBlankNode = Oxigraph.BlankNode;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Each test instance owns a fresh on-disk RocksDB store in its own temp
/// directory; both are torn down on dispose. The store is reset (cleared) at
/// the start of every test so tests don't leak quads into each other.
/// </summary>
public sealed class StoreWrapperFixture : IDisposable
{
    public string Path { get; }
    public StoreWrapper Store { get; }

    public StoreWrapperFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-oxigraph-" + Guid.NewGuid().ToString("N"));
        Store = new StoreWrapper(Path);
    }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public class StoreWrapperTests : IClassFixture<StoreWrapperFixture>, IAsyncLifetime
{
    private readonly StoreWrapperFixture _fx;
    private readonly OntoNamedNode _g1 = new("urn:g1");
    private readonly OntoNamedNode _g2 = new("urn:g2");

    public StoreWrapperTests(StoreWrapperFixture fx) { _fx = fx; }

    public Task InitializeAsync()
    {
        // ClassFixture gives us a single StoreWrapper shared across tests in
        // this class; wipe everything so each test starts with a clean slate.
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // CRUD — Add/Remove/Match/Count/Contains/DumpNQuads/ReplaceGraph
    // ------------------------------------------------------------------

    [Fact]
    public void AddQuads_then_Match_returns_inserted_quads()
    {
        var q = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                             new OntoLiteral("v"), _g1);
        _fx.Store.AddQuads(_g1, [q]);

        Assert.Equal(1ul, _fx.Store.Count(graph: _g1));
        Assert.Equal(new[] { q }, _fx.Store.Match(graph: _g1));
    }

    [Fact]
    public void RemoveQuads_then_Match_returns_empty()
    {
        var q = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                             new OntoLiteral("v"), _g1);
        _fx.Store.AddQuads(_g1, [q]);
        _fx.Store.RemoveQuads(_g1, [q]);

        Assert.Equal(0ul, _fx.Store.Count(graph: _g1));
        Assert.Empty(_fx.Store.Match(graph: _g1));
    }

    [Fact]
    public void ContainsQuad_reports_membership()
    {
        var q = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                             new OntoLiteral("v"), _g1);
        _fx.Store.AddQuads(_g1, [q]);

        Assert.True(_fx.Store.ContainsQuad(q));
        Assert.False(_fx.Store.ContainsQuad(new OntoQuad(
            new OntoNamedNode("urn:absent"), new OntoNamedNode("urn:p"),
            new OntoLiteral("v"), _g1)));
    }

    [Fact]
    public void Count_across_all_named_graphs_matches_total()
    {
        _fx.Store.AddQuads(_g1, [new OntoQuad(new OntoNamedNode("urn:s1"), new OntoNamedNode("urn:p"),
                                              new OntoLiteral("a"), _g1)]);
        _fx.Store.AddQuads(_g2, [new OntoQuad(new OntoNamedNode("urn:s2"), new OntoNamedNode("urn:p"),
                                              new OntoLiteral("b"), _g2)]);
        Assert.Equal(2ul, _fx.Store.Count());
        Assert.Equal(1ul, _fx.Store.Count(graph: _g1));
        Assert.Equal(1ul, _fx.Store.Count(graph: _g2));
    }

    [Fact]
    public void ReplaceGraph_wipes_existing_quads_and_adds_new_ones()
    {
        var oldQ = new OntoQuad(new OntoNamedNode("urn:old"), new OntoNamedNode("urn:p"),
                                new OntoLiteral("old"), _g1);
        var newQ = new OntoQuad(new OntoNamedNode("urn:new"), new OntoNamedNode("urn:p"),
                                new OntoLiteral("new"), _g1);
        _fx.Store.AddQuads(_g1, [oldQ]);

        _fx.Store.ReplaceGraph(_g1, [newQ]);

        Assert.DoesNotContain(oldQ, _fx.Store.Match(graph: _g1));
        Assert.Equal(new[] { newQ }, _fx.Store.Match(graph: _g1));
    }

    // ------------------------------------------------------------------
    // Format round-trip — preserve blank nodes, language tags, datatypes
    // ------------------------------------------------------------------

    [Fact]
    public void DumpNQuads_round_trips_preserving_blank_node_label()
    {
        var bnode = new OntoBlankNode("b1");
        _fx.Store.AddQuads(_g1, [new OntoQuad(bnode, new OntoNamedNode("urn:p"),
                                              new OntoLiteral("v"), _g1)]);

        var bytes = _fx.Store.DumpNQuads(_g1);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        // Dump must contain a `_:...` blank-node token (label is Oxigraph-
        // assigned at dump time; we don't pin the exact label).
        Assert.Contains("_:", text);

        // Round-trip into a fresh store and confirm the blank node is still
        // present (Oxigraph will assign it a new internal label, but it stays
        // a blank node — never promoted to a named node).
        using var fresh = new StoreWrapper(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-rt-" + Guid.NewGuid().ToString("N")));
        fresh.LoadNQuads(bytes);
        var roundTripped = fresh.Match(graph: _g1);
        Assert.Single(roundTripped);
        Assert.IsType<OntoBlankNode>(roundTripped[0].Subject);
    }

    [Fact]
    public void DumpNQuads_round_trips_preserving_language_tag()
    {
        _fx.Store.AddQuads(_g1, [new OntoQuad(new OntoNamedNode("urn:s"),
                                              new OntoNamedNode("urn:p"),
                                              new OntoLiteral("hello", Language: "en"),
                                              _g1)]);
        var bytes = _fx.Store.DumpNQuads(_g1);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"hello\"@en", text);

        using var fresh = new StoreWrapper(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-rt-" + Guid.NewGuid().ToString("N")));
        fresh.LoadNQuads(bytes);
        var q = Assert.Single(fresh.Match(graph: _g1));
        var lit = Assert.IsType<OntoLiteral>(q.Object);
        Assert.Equal("en", lit.Language);
    }

    [Fact]
    public void DumpNQuads_round_trips_preserving_datatype()
    {
        _fx.Store.AddQuads(_g1, [new OntoQuad(new OntoNamedNode("urn:s"),
                                              new OntoNamedNode("urn:p"),
                                              new OntoLiteral("42", Datatype: OntoLiteral.XsdInteger),
                                              _g1)]);
        var bytes = _fx.Store.DumpNQuads(_g1);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", text);

        using var fresh = new StoreWrapper(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-rt-" + Guid.NewGuid().ToString("N")));
        fresh.LoadNQuads(bytes);
        var q = Assert.Single(fresh.Match(graph: _g1));
        var lit = Assert.IsType<OntoLiteral>(q.Object);
        Assert.Equal(OntoLiteral.XsdInteger, lit.Datatype);
    }

    // ------------------------------------------------------------------
    // Capture / revert
    // ------------------------------------------------------------------

    [Fact]
    public async Task Failed_capture_restores_exact_pre_operation_graph()
    {
        var existing = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("existing"), _g1);
        var added = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("added"), _g1);
        _fx.Store.AddQuads(_g1, [existing]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var capture = await _fx.Store.CaptureAsync(_g1, revertOnError: true);
            _fx.Store.RemoveQuads(_g1, [existing]);
            _fx.Store.AddQuads(_g1, [added]);
            throw new InvalidOperationException();
        });

        Assert.Equal(new[] { existing }, _fx.Store.Match(graph: _g1));
    }

    [Fact]
    public async Task Successful_capture_commits_changes()
    {
        var existing = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("existing"), _g1);
        var added = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("added"), _g1);
        _fx.Store.AddQuads(_g1, [existing]);

        // No exception + revertOnError=false → commits on dispose. Callers
        // that want automatic revert on throw should pass revertOnError=true.
        await using (var capture = await _fx.Store.CaptureAsync(_g1, revertOnError: false))
        {
            _fx.Store.RemoveQuads(_g1, [existing]);
            _fx.Store.AddQuads(_g1, [added]);
        }

        Assert.Equal(new[] { added }, _fx.Store.Match(graph: _g1));
    }

    [Fact]
    public async Task Capture_without_revert_keeps_changes_even_on_exception()
    {
        var existing = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("existing"), _g1);
        var added = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("added"), _g1);
        _fx.Store.AddQuads(_g1, [existing]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var capture = await _fx.Store.CaptureAsync(_g1, revertOnError: false);
            _fx.Store.RemoveQuads(_g1, [existing]);
            _fx.Store.AddQuads(_g1, [added]);
            throw new InvalidOperationException();
        });

        Assert.Equal(new[] { added }, _fx.Store.Match(graph: _g1));
    }

    [Fact]
    public async Task MarkError_forces_revert_on_dispose()
    {
        var existing = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("existing"), _g1);
        var added = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
                                    new OntoLiteral("added"), _g1);
        _fx.Store.AddQuads(_g1, [existing]);

        await using (var capture = await _fx.Store.CaptureAsync(_g1, revertOnError: false))
        {
            _fx.Store.RemoveQuads(_g1, [existing]);
            _fx.Store.AddQuads(_g1, [added]);
            capture.MarkError();
        }

        Assert.Equal(new[] { existing }, _fx.Store.Match(graph: _g1));
    }

    // ------------------------------------------------------------------
    // Contention / concurrency
    // ------------------------------------------------------------------

    [Fact]
    public async Task Same_graph_capture_times_out_when_already_held()
    {
        await using var first = await _fx.Store.CaptureAsync(_g1, revertOnError: false);

        await Assert.ThrowsAsync<GraphWriteConflictException>(async () =>
        {
            // Use a short timeout so the test is fast but still goes through the
            // same 15s-bounded wait used in production.
            await using var second = await _fx.Store.CaptureAsync(_g1,
                revertOnError: false,
                waitTimeout: TimeSpan.FromMilliseconds(200));
        });
    }

    [Fact]
    public async Task Different_graphs_can_be_written_concurrently()
    {
        await using var first = await _fx.Store.CaptureAsync(_g1, revertOnError: false);
        await using var second = await _fx.Store.CaptureAsync(_g2, revertOnError: false);

        _fx.Store.AddQuads(_g1, [new OntoQuad(new OntoNamedNode("urn:s1"), new OntoNamedNode("urn:p"),
                                              new OntoLiteral("a"), _g1)]);
        _fx.Store.AddQuads(_g2, [new OntoQuad(new OntoNamedNode("urn:s2"), new OntoNamedNode("urn:p"),
                                              new OntoLiteral("b"), _g2)]);

        Assert.Equal(1ul, _fx.Store.Count(graph: _g1));
        Assert.Equal(1ul, _fx.Store.Count(graph: _g2));
    }

    [Fact]
    public async Task ReadLockAsync_allows_multiple_readers_and_blocks_writer()
    {
        await using var r1 = await _fx.Store.ReadLockAsync();
        await using var r2 = await _fx.Store.ReadLockAsync();

        await Assert.ThrowsAsync<GraphWriteConflictException>(async () =>
        {
            await using var w = await _fx.Store.CaptureAsync(_g1,
                revertOnError: false,
                waitTimeout: TimeSpan.FromMilliseconds(200));
        });
    }
}
