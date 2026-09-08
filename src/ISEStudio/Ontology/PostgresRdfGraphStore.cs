using ISEStudio.Infrastructure.Persistence.Entities;
using System.Text;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;
using OntoLiteral = Oxigraph.Literal;

namespace ISEStudio.Ontology;

/// <summary>Synchronous graph-shaped facade over the PostgreSQL RDF layer.</summary>
public sealed class PostgresRdfGraphStore
{
    private readonly IRdfStatementRepository _statements;
    private readonly Guid _knowledgeSystemId;
    private readonly string _layer;

    public PostgresRdfGraphStore(IRdfStatementRepository statements, Guid knowledgeSystemId, string layer)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentException.ThrowIfNullOrEmpty(layer);
        _statements = statements;
        _knowledgeSystemId = knowledgeSystemId;
        _layer = layer;
    }

    public List<OntoQuad> Match(string? graphIri = null, string? subjectIri = null,
        string? predicateIri = null, string? objectIri = null)
    {
        var statements = Statements();
        var matches = statements.Where(s => graphIri is null || s.GraphIri == graphIri)
            .Where(s => subjectIri is null || s.Subject is RdfIri iri && iri.Value == subjectIri)
            .Where(s => predicateIri is null || s.PredicateIri == predicateIri)
            .Where(s => objectIri is null || s.Object is RdfIri iri && iri.Value == objectIri)
            .Select(ToQuad).ToList();
        return matches;
    }

    public byte[] DumpNQuads(string graphIri) =>
        RdfExportService.SerializeNQuads(Statements().Where(s => s.GraphIri == graphIri).ToList());

    public void AddQuads(OntoNamedNode graph, IEnumerable<OntoQuad> quads)
    {
        var incoming = quads.ToList();
        var all = Statements().Select(ToQuad).ToList();
        var selected = all.Where(q => q.Graph is OntoNamedNode node && node.Value == graph.Value).Concat(incoming).Distinct().ToList();
        var other = all.Where(q => q.Graph is not OntoNamedNode node || node.Value != graph.Value);
        Replace(other.Concat(selected));
    }

    public void RemoveQuads(OntoNamedNode graph, IEnumerable<OntoQuad> quads)
    {
        var remove = quads.ToHashSet();
        Replace(Statements().Select(ToQuad).Where(q => !(q.Graph is OntoNamedNode node && node.Value == graph.Value) || !remove.Contains(q)));
    }

    public ValueTask<PostgresRdfCapture> CaptureAsync(string graphIri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PostgresRdfCapture(this, graphIri, DumpNQuads(graphIri)));
    }

    public ValueTask<PostgresRdfCapture> CaptureAsync(OntoNamedNode graph,
        bool revertOnError, TimeSpan? waitTimeout, CancellationToken cancellationToken) =>
        CaptureAsync(graph.Value, cancellationToken);

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
        var current = Statements().Select(ToQuad).ToList();
        using var parsed = new Oxigraph.Store();
        parsed.Load(Encoding.UTF8.GetString(snapshot), Oxigraph.RdfFormat.NQuads);
        var restored = parsed.Match().ToList();
        Replace(current.Where(q => !(q.Graph is OntoNamedNode node && node.Value == graphIri)).Concat(restored));
    }

    private IReadOnlyList<RdfStatement> Statements() =>
        _statements.ListAsync(_knowledgeSystemId, _layer).GetAwaiter().GetResult();

    private void Replace(IEnumerable<OntoQuad> quads) =>
        _statements.ReplaceLayerAsync(_knowledgeSystemId, _layer,
            quads.Select(FromQuad).ToList()).GetAwaiter().GetResult();

    private static RdfStatement FromQuad(OntoQuad quad) =>
        new(FromTerm(quad.Subject), quad.Predicate.Value, FromTerm(quad.Object),
            quad.Graph is OntoNamedNode graph ? graph.Value : quad.Graph?.ToString());

    public static OntoQuad ToQuadForConflictDetection(RdfStatement statement) => ToQuad(statement);

    private static RdfTerm FromTerm(Oxigraph.ITerm term) => term switch
    {
        OntoNamedNode iri => new RdfIri(iri.Value),
        Oxigraph.BlankNode blank => new RdfBlankNode(blank.Value),
        OntoLiteral literal => new RdfLiteral(literal.Value, literal.Language, literal.Datatype?.Value),
        _ => throw new InvalidOperationException($"Unsupported RDF term: {term.GetType().Name}"),
    };

    private static OntoQuad ToQuad(RdfStatement statement)
    {
        var graph = new OntoNamedNode(statement.GraphIri ?? throw new InvalidOperationException("RDF graph is required"));
        return new OntoQuad(ToSubject(statement.Subject), new OntoNamedNode(statement.PredicateIri), ToObject(statement.Object), graph);
    }

    private static Oxigraph.INamedOrBlankNode ToSubject(RdfTerm term) => term switch
    {
        RdfIri iri => new OntoNamedNode(iri.Value),
        RdfBlankNode blank => new Oxigraph.BlankNode(blank.Id),
        _ => throw new InvalidOperationException("RDF subject must be an IRI or blank node"),
    };

    private static Oxigraph.ITerm ToObject(RdfTerm term) => term switch
    {
        RdfIri iri => new OntoNamedNode(iri.Value),
        RdfBlankNode blank => new Oxigraph.BlankNode(blank.Id),
        RdfLiteral literal => new OntoLiteral(literal.Value, literal.Language,
            literal.Datatype is null ? null : new OntoNamedNode(literal.Datatype)),
        _ => throw new InvalidOperationException($"Unsupported RDF term: {term.GetType().Name}"),
    };
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
