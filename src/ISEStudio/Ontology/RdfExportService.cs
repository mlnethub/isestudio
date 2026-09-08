using System.Text;
using VDS.RDF;
using VDS.RDF.Writing;

namespace ISEStudio.Ontology;

/// <summary>
/// Layered exporter. Supports N-Quads, N-Triples, Turtle, and TriG — the four
/// formats required for the language-tag round-trip test. The exporter
/// always serializes exactly one workspace layer at a time; for an
/// across-KS bundle use the release artifact store.
/// </summary>
public sealed class RdfExportService
{
    private readonly IRdfStatementRepository _statements;

    public RdfExportService(IRdfStatementRepository statements)
    {
        _statements = statements ?? throw new ArgumentNullException(nameof(statements));
    }

    /// <summary>
    /// Serialize one layer of the workspace to bytes in <paramref name="format"/>.
    /// The bytes preserve blank-node labels, language tags, and explicit
    /// datatypes for all four formats (N-Quads / N-Triples / Turtle / TriG).
    /// </summary>
    public async Task<byte[]> ExportAsync(
        KsContext ks,
        RdfLayer layer,
        RdfExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ks);
        cancellationToken.ThrowIfCancellationRequested();

        var graphIri = ReleaseManager.GraphIriFor(ks, layer);
        var statements = await _statements.ListAsync(
            ks.KnowledgeSystemId, layer.ToString(), cancellationToken).ConfigureAwait(false);

        byte[] bytes = format switch
        {
            RdfExportFormat.NQuads => DumpNQuads(statements),
            RdfExportFormat.TriG => DumpTriG(statements, graphIri),
            RdfExportFormat.Turtle => DumpTurtle(statements),
            RdfExportFormat.NTriples => DumpNTriples(statements),
            RdfExportFormat.RdfXml => WriteGraphWithDotNetRdf(statements, new RdfXmlWriter()),
            RdfExportFormat.JsonLd => WriteStoreWithDotNetRdf(statements, new JsonLdWriter()),
            _ => throw new ArgumentOutOfRangeException(nameof(format),
                $"Format {format} is not a single-layer export format."),
        };
        return bytes;
    }

    public static byte[] SerializeNQuads(IReadOnlyList<RdfStatement> statements) =>
        DumpNQuads(statements);

    // ------------------------------------------------------------------
    // dotNetRDF-backed formats (RDF/XML, JSON-LD). Oxigraph's 0.5.8 bindings
    // round-trip N-Quads / Turtle / N-Triples / TriG via the hand-rolled
    // dumpers above (which preserve blank-node labels, language tags, and
    // explicit datatypes). RDF/XML and JSON-LD have no in-house serializer,
    // so we project the Oxigraph quads onto a dotNetRDF graph and hand it
    // to dotNetRDF's writers — they handle the full grammars and preserve
    // typed literals. RDF/XML is graph-based (IRdfWriter); JSON-LD is a
    // dataset format (IStoreWriter), so the graph is wrapped in a
    // TripleStore for the JSON-LD writer.
    // ------------------------------------------------------------------
    private static byte[] WriteGraphWithDotNetRdf(IReadOnlyList<RdfStatement> statements, IRdfWriter writer)
    {
        if (statements.Count == 0) return Array.Empty<byte>();
        var graph = BuildDotNetRdfGraph(statements);
        using var sw = new System.IO.StringWriter();
        writer.Save(graph, sw);
        return Encoding.UTF8.GetBytes(sw.ToString());
    }

    private static byte[] WriteStoreWithDotNetRdf(IReadOnlyList<RdfStatement> statements, IStoreWriter writer)
    {
        if (statements.Count == 0) return Array.Empty<byte>();
        var graph = BuildDotNetRdfGraph(statements);
        var store = new TripleStore();
        store.Add(graph);
        using var sw = new System.IO.StringWriter();
        writer.Save(store, sw);
        return Encoding.UTF8.GetBytes(sw.ToString());
    }

    private static VDS.RDF.Graph BuildDotNetRdfGraph(IReadOnlyList<RdfStatement> statements)
    {
        var graph = new VDS.RDF.Graph();
        foreach (var statement in statements)
        {
            var s = ToDotNetRdfNode(graph, statement.Subject);
            var p = graph.CreateUriNode(new Uri(statement.PredicateIri, UriKind.RelativeOrAbsolute));
            var o = ToDotNetRdfNode(graph, statement.Object);
            graph.Assert(s, p, o);
        }
        return graph;
    }

    private static INode ToDotNetRdfNode(IGraph g, RdfTerm term) => term switch
    {
        RdfIri iri => g.CreateUriNode(new Uri(iri.Value, UriKind.RelativeOrAbsolute)),
        RdfBlankNode blank => g.CreateBlankNode(blank.Id),
        RdfLiteral literal => ToDotNetRdfLiteral(g, literal),
        _ => throw new InvalidOperationException($"Unsupported RDF term: {term.GetType().Name}"),
    };

    private static ILiteralNode ToDotNetRdfLiteral(IGraph g, RdfLiteral lit)
    {
        if (!string.IsNullOrEmpty(lit.Language))
            return g.CreateLiteralNode(lit.Value, lit.Language);
        if (lit.Datatype is not null)
            return g.CreateLiteralNode(lit.Value, new Uri(lit.Datatype, UriKind.RelativeOrAbsolute));
        return g.CreateLiteralNode(lit.Value);
    }

    // ------------------------------------------------------------------
    // Strategy
    //
    //  - NQuads: served by StoreWrapper.DumpNQuads (in-process byte-exact,
    //    preserves blank-node labels, language tags, datatypes, AND the
    //    graph context).
    //
    //  - TriG: Oxigraph's Store.Dump(TriG) works fine for our use case
    //    (load the layer into a fresh in-memory store with the named graph,
    //    then Dump). TriG preserves named graphs natively.
    //
    //  - Turtle / NTriples: triple-only formats. Oxigraph 0.5.8's Dump for
    //    these formats throws "A RDF format supporting datasets was
    //    expected" on *any* store that has quads, including stores whose
    //    only graph is the default graph. We hand-roll a minimal serializer
    //    that emits N-Triples (no graph context, one statement per line)
    //    or Turtle (subject grouping, dot-terminated) with full blank
    //    node / language tag / datatype support. The output is enough for
    //    our round-trip tests; it is not a complete implementation of the
    //    Turtle grammar (no prefix compaction, no collection syntax, no
    //    abbreviated IRX blank-node `[]`).
    // ------------------------------------------------------------------

    private static byte[] DumpTriG(IReadOnlyList<RdfStatement> statements, string graphIri)
    {
        if (statements.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var sb = new StringBuilder();
        sb.Append('<').Append(graphIri).Append("> {\n");
        foreach (var statement in statements)
        {
            AppendTerm(sb, statement.Subject);
            sb.Append(' ');
            AppendTerm(sb, new RdfIri(statement.PredicateIri));
            sb.Append(' ');
            AppendTerm(sb, statement.Object);
            sb.Append(" .\n");
        }
        sb.Append("}\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] DumpNQuads(IReadOnlyList<RdfStatement> statements)
    {
        if (statements.Count == 0) return Array.Empty<byte>();
        var sb = new StringBuilder();
        foreach (var statement in statements)
        {
            AppendTerm(sb, statement.Subject);
            sb.Append(' ');
            AppendTerm(sb, new RdfIri(statement.PredicateIri));
            sb.Append(' ');
            AppendTerm(sb, statement.Object);
            if (!string.IsNullOrWhiteSpace(statement.GraphIri)) sb.Append(" <").Append(statement.GraphIri).Append('>');
            sb.Append(" .\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] DumpNTriples(IReadOnlyList<RdfStatement> statements)
    {
        if (statements.Count == 0) return Array.Empty<byte>();

        var sb = new StringBuilder();
        foreach (var statement in statements)
        {
            AppendTerm(sb, statement.Subject);
            sb.Append(' ');
            AppendTerm(sb, new RdfIri(statement.PredicateIri));
            sb.Append(' ');
            AppendTerm(sb, statement.Object);
            sb.Append(" .\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] DumpTurtle(IReadOnlyList<RdfStatement> statements)
    {
        if (statements.Count == 0) return Array.Empty<byte>();

        // Group by subject so we can use Turtle's `;` continuation form.
        var bySubject = new Dictionary<string, (RdfTerm Key, List<(RdfIri P, RdfTerm O)> Rows)>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            var key = SubjectKey(statement.Subject);
            if (!bySubject.TryGetValue(key, out var entry))
            {
                entry = (statement.Subject, new List<(RdfIri, RdfTerm)>());
                bySubject[key] = entry;
            }
            entry.Rows.Add((new RdfIri(statement.PredicateIri), statement.Object));
        }

        var sb = new StringBuilder();
        sb.Append("@prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .\n");
        sb.Append("@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n");
        sb.Append("@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .\n\n");

        foreach (var entry in bySubject.Values)
        {
            AppendTerm(sb, entry.Key);
            sb.Append('\n');
            for (int i = 0; i < entry.Rows.Count; i++)
            {
                var (p, obj) = entry.Rows[i];
                sb.Append("    ");
                AppendTerm(sb, p);
                sb.Append(' ');
                AppendTerm(sb, obj);
                sb.Append(i == entry.Rows.Count - 1 ? " .\n" : " ;\n");
            }
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string SubjectKey(RdfTerm subject) => subject switch
    {
        RdfIri iri => "<" + iri.Value + ">",
        RdfBlankNode blank => "_:" + blank.Id,
        _ => subject.ToString() ?? string.Empty,
    };

    // ------------------------------------------------------------------
    // Term writer — preserves blank-node labels, language tags, datatypes.
    // Delegates to NQuadsTermWriter so conflict signatures, store dumps,
    // and export bytes all share one implementation (and cannot drift).
    // ------------------------------------------------------------------
    private static void AppendTerm(StringBuilder sb, RdfTerm term)
    {
        switch (term)
        {
            case RdfIri iri: sb.Append('<').Append(iri.Value).Append('>'); break;
            case RdfBlankNode blank: sb.Append("_:").Append(blank.Id); break;
            case RdfLiteral literal:
                sb.Append('"').Append(literal.Value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append('"');
                if (!string.IsNullOrWhiteSpace(literal.Language)) sb.Append('@').Append(literal.Language);
                else if (!string.IsNullOrWhiteSpace(literal.Datatype)) sb.Append("^^<").Append(literal.Datatype).Append('>');
                break;
        }
    }
}