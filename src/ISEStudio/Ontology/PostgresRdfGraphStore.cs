using System.Text;

namespace ISEStudio.Ontology;

/// <summary>Synchronous graph-shaped facade over the PostgreSQL RDF layer.</summary>
public sealed class PostgresRdfGraphStore
{
    private readonly IRdfStatementRepository _statements;
    private readonly Guid _knowledgeSystemId;
    private readonly string _layer;
    // EF Core's DbContext is not thread-safe — concurrent reads / writes
    // on the same instance trip the ConcurrencyDetector and the change
    // tracker. The fixture exposes one shared context across the test
    // suite, so every write through this facade is serialised behind a
    // semaphore to keep the read-modify-write cycle in AddStatements /
    // RemoveStatements coherent when the test driver fires concurrent tasks.
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public PostgresRdfGraphStore(IRdfStatementRepository statements, Guid knowledgeSystemId, string layer)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentException.ThrowIfNullOrEmpty(layer);
        _statements = statements;
        _knowledgeSystemId = knowledgeSystemId;
        _layer = layer;
    }

    public List<RdfStatement> Match(string? graphIri = null, string? subjectIri = null,
        string? predicateIri = null, string? objectIri = null)
    {
        var statements = Statements();
        return statements
            .Where(s => graphIri is null || s.GraphIri == graphIri)
            .Where(s => subjectIri is null || s.Subject is RdfIri iri && iri.Value == subjectIri)
            .Where(s => predicateIri is null || s.PredicateIri == predicateIri)
            .Where(s => objectIri is null || s.Object is RdfIri iri && iri.Value == objectIri)
            .ToList();
    }

    public byte[] DumpNQuads(string graphIri) =>
        RdfExportService.SerializeNQuads(Statements().Where(s => s.GraphIri == graphIri).ToList());

    /// <summary>
    /// Statement-shape upsert. The incoming statements are merged with the
    /// layer's existing statements in <see cref="RdfStatement"/> form.
    /// </summary>
    public void AddStatements(string graphIri, IEnumerable<RdfStatement> statements)
    {
        _writeLock.Wait();
        try
        {
            var incoming = statements.ToList();
            var existing = Statements();
            var other = existing.Where(s => s.GraphIri != graphIri);
            var inGraph = existing.Where(s => s.GraphIri == graphIri)
                .Concat(incoming)
                .Distinct()
                .ToList();
            Replace(other.Concat(inGraph));
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Statement-shape removal.
    /// </summary>
    public void RemoveStatements(string graphIri, IEnumerable<RdfStatement> statements)
    {
        _writeLock.Wait();
        try
        {
            var remove = statements.ToHashSet();
            var remaining = Statements()
                .Where(s => !(s.GraphIri == graphIri && remove.Contains(s)))
                .ToList();
            Replace(remaining);
        }
        finally { _writeLock.Release(); }
    }

    public ValueTask<PostgresRdfCapture> CaptureAsync(string graphIri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PostgresRdfCapture(this, graphIri, DumpNQuads(graphIri)));
    }

    public ValueTask<PostgresRdfCapture> CaptureAsync(string graphIri,
        bool revertOnError, TimeSpan? waitTimeout, CancellationToken cancellationToken) =>
        CaptureAsync(graphIri, cancellationToken);

    public static (byte[] Added, byte[] Removed) DiffNQuads(byte[] pre, byte[] post) =>
        DiffLines(pre, post);

    private static (byte[] Added, byte[] Removed) DiffLines(byte[] pre, byte[] post)
    {
        var before = SplitLines(pre);
        var after = SplitLines(post);
        var added = after.Except(before, StringComparer.Ordinal);
        var removed = before.Except(after, StringComparer.Ordinal);
        return (Encoding.UTF8.GetBytes(string.Join('\n', added) + (added.Any() ? "\n" : string.Empty)),
            Encoding.UTF8.GetBytes(string.Join('\n', removed) + (removed.Any() ? "\n" : string.Empty)));
    }

    private static HashSet<string> SplitLines(byte[] bytes) =>
        new(Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')), StringComparer.Ordinal);

    internal void Restore(string graphIri, byte[] snapshot)
    {
        var restored = RdfDotNetRdfCodec.ParseNQuads(snapshot).Statements;
        Replace(Statements().Where(s => s.GraphIri != graphIri).Concat(restored));
    }

    private IReadOnlyList<RdfStatement> Statements() =>
        _statements.ListAsync(_knowledgeSystemId, _layer).GetAwaiter().GetResult();

    private void Replace(IEnumerable<RdfStatement> statements) =>
        _statements.ReplaceLayerAsync(_knowledgeSystemId, _layer, statements.ToList())
            .GetAwaiter().GetResult();
}

public sealed class PostgresRdfCapture : IAsyncDisposable
{
    private readonly PostgresRdfGraphStore _store;
    private readonly string _graphIri;
    private readonly byte[] _snapshot;
    private bool _error;

    internal PostgresRdfCapture(PostgresRdfGraphStore store, string graphIri, byte[] snapshot)
    {
        _store = store;
        _graphIri = graphIri;
        _snapshot = snapshot;
    }

    public void MarkError() => _error = true;

    public ValueTask DisposeAsync()
    {
        if (_error) _store.Restore(_graphIri, _snapshot);
        return ValueTask.CompletedTask;
    }
}
