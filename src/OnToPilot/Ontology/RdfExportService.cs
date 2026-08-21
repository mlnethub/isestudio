using System.Text;
using Oxigraph;
using VDS.RDF;
using VDS.RDF.Writing;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;
using OntoQuad = Oxigraph.Quad;

namespace OnToPilot.Ontology;

/// <summary>
/// Layered exporter. Supports N-Quads, N-Triples, Turtle, and TriG — the four
/// formats required for the language-tag round-trip test. The exporter
/// always serializes exactly one workspace layer at a time; for an
/// across-KS bundle use the release artifact store.
/// </summary>
public sealed class RdfExportService
{
    private readonly StoreWrapper _store;

    public RdfExportService(StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Serialize one layer of the workspace to bytes in <paramref name="format"/>.
    /// The bytes preserve blank-node labels, language tags, and explicit
    /// datatypes for all four formats (N-Quads / N-Triples / Turtle / TriG).
    /// </summary>
    public Task<byte[]> ExportAsync(
        KsContext ks,
        RdfLayer layer,
        RdfFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ks);
        cancellationToken.ThrowIfCancellationRequested();

        var graphIri = ReleaseManager.GraphIriFor(ks, layer);
        var graph = new OntoNamedNode(graphIri);
        var quads = _store.Match(graph: graph);

        byte[] bytes = format switch
        {
            RdfFormat.NQuads => _store.DumpNQuads(graph),
            RdfFormat.TriG => DumpTriG(quads, graphIri),
            RdfFormat.Turtle => DumpTurtle(quads),
            RdfFormat.NTriples => DumpNTriples(quads),
            RdfFormat.RdfXml => WriteGraphWithDotNetRdf(quads, new RdfXmlWriter()),
            RdfFormat.JsonLd => WriteStoreWithDotNetRdf(quads, new JsonLdWriter()),
            _ => throw new ArgumentOutOfRangeException(nameof(format),
                $"Format {format} is not a single-layer export format."),
        };
        return Task.FromResult(bytes);
    }

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
    private static byte[] WriteGraphWithDotNetRdf(IReadOnlyList<OntoQuad> quads, IRdfWriter writer)
    {
        if (quads.Count == 0) return Array.Empty<byte>();
        var graph = BuildDotNetRdfGraph(quads);
        using var sw = new System.IO.StringWriter();
        writer.Save(graph, sw);
        return Encoding.UTF8.GetBytes(sw.ToString());
    }

    private static byte[] WriteStoreWithDotNetRdf(IReadOnlyList<OntoQuad> quads, IStoreWriter writer)
    {
        if (quads.Count == 0) return Array.Empty<byte>();
        var graph = BuildDotNetRdfGraph(quads);
        var store = new TripleStore();
        store.Add(graph);
        using var sw = new System.IO.StringWriter();
        writer.Save(store, sw);
        return Encoding.UTF8.GetBytes(sw.ToString());
    }

    private static Graph BuildDotNetRdfGraph(IReadOnlyList<OntoQuad> quads)
    {
        var graph = new Graph();
        foreach (var q in quads)
        {
            var s = ToDotNetRdfNode(graph, q.Subject);
            var p = ToDotNetRdfNode(graph, q.Predicate);
            var o = ToDotNetRdfNode(graph, q.Object);
            graph.Assert(s, p, o);
        }
        return graph;
    }

    private static INode ToDotNetRdfNode(IGraph g, object term) => term switch
    {
        OntoNamedNode n => g.CreateUriNode(new Uri(n.Value, UriKind.RelativeOrAbsolute)),
        OntoBlankNode b => g.CreateBlankNode(b.Value),
        OntoLiteral lit => ToDotNetRdfLiteral(g, lit),
        _ => throw new InvalidOperationException(
            $"Unsupported Oxigraph term for dotNetRDF conversion: {term?.GetType().Name ?? "null"}"),
    };

    private static ILiteralNode ToDotNetRdfLiteral(IGraph g, OntoLiteral lit)
    {
        if (!string.IsNullOrEmpty(lit.Language))
            return g.CreateLiteralNode(lit.Value, lit.Language);
        if (lit.Datatype is not null)
            return g.CreateLiteralNode(lit.Value, new Uri(lit.Datatype.Value, UriKind.RelativeOrAbsolute));
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

    private static byte[] DumpTriG(IReadOnlyList<OntoQuad> quads, string graphIri)
    {
        if (quads.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var sb = new StringBuilder();
        var g = new OntoNamedNode(graphIri);
        sb.Append('<').Append(graphIri).Append("> {\n");
        foreach (var q in quads)
        {
            AppendTerm(sb, q.Subject);
            sb.Append(' ');
            AppendTerm(sb, q.Predicate);
            sb.Append(' ');
            AppendTerm(sb, q.Object);
            sb.Append(" .\n");
        }
        sb.Append("}\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] DumpNTriples(IReadOnlyList<OntoQuad> quads)
    {
        if (quads.Count == 0) return Array.Empty<byte>();
        var sb = new StringBuilder();
        foreach (var q in quads)
        {
            AppendTerm(sb, q.Subject);
            sb.Append(' ');
            AppendTerm(sb, q.Predicate);
            sb.Append(' ');
            AppendTerm(sb, q.Object);
            sb.Append(" .\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] DumpTurtle(IReadOnlyList<OntoQuad> quads)
    {
        if (quads.Count == 0) return Array.Empty<byte>();

        // Group by subject so we can use Turtle's `;` continuation form.
        var bySubject = new Dictionary<string, (object Key, List<(OntoNamedNode P, object O)> Rows)>(StringComparer.Ordinal);
        foreach (var q in quads)
        {
            var key = SubjectKey(q.Subject);
            if (!bySubject.TryGetValue(key, out var entry))
            {
                entry = (q.Subject, new List<(OntoNamedNode, object)>());
                bySubject[key] = entry;
            }
            entry.Rows.Add((q.Predicate, q.Object));
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

    private static string SubjectKey(object subject) => subject switch
    {
        OntoNamedNode n => "<" + n.Value + ">",
        OntoBlankNode b => "_:" + b.Value,
        _ => subject.ToString() ?? "",
    };

    // ------------------------------------------------------------------
    // Term writer — preserves blank-node labels, language tags, datatypes.
    // Delegates to NQuadsTermWriter so conflict signatures, store dumps,
    // and export bytes all share one implementation (and cannot drift).
    // ------------------------------------------------------------------
    private static void AppendTerm(StringBuilder sb, object term) =>
        NQuadsTermWriter.Append(sb, term);
}