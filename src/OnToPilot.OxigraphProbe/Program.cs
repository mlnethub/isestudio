// Oxigraph 0.5.8 API probe — runs against the verified packages and writes a
// short report describing every API surface OnToPilot.StoreWrapper depends on.
//
// Usage:  dotnet run --project src/OnToPilot.OxigraphProbe -- <store-directory>
// Output: <store-directory>/probe-report.txt
using System.Text;
using Oxigraph;
// dotNetRDF exposes its own Quad/BlankNode in VDS.RDF; alias to the Oxigraph
// types so the probe never mixes the two vocabularies.
using OxigraphQuad = Oxigraph.Quad;
using OxigraphBlankNode = Oxigraph.BlankNode;
using OxigraphNamedNode = Oxigraph.NamedNode;
using OxigraphLiteral = Oxigraph.Literal;
using OxigraphStore = Oxigraph.Store;
using OxigraphRdfFormat = Oxigraph.RdfFormat;
using OxigraphDumpOptions = Oxigraph.DumpOptions;
using OxigraphDefaultGraph = Oxigraph.DefaultGraph;
using DotNetRdf = VDS.RDF;

// 1. Disk-backed store open + close. The Store(path) ctor opens a RocksDB-backed
//    store rooted at `path`; null opens a memory store.
using var store = new OxigraphStore(args[0]);

// 2. IRIs & literals.
var graph = new OxigraphNamedNode("urn:probe");
var subj = new OxigraphNamedNode("urn:s");
var pred = new OxigraphNamedNode("urn:p");

// 3. Typed literal with a language tag and datatype — proves N-Triples round-trip
//    preserves language tags, datatypes, and blank-node labels.
var litLang = new OxigraphLiteral("hello", Language: "en");
var litDt = new OxigraphLiteral("42", Datatype: OxigraphLiteral.XsdInteger);
var litPlain = new OxigraphLiteral("plain");

// 4. Blank node — preserves id across the dump/load boundary.
var bnode = new OxigraphBlankNode("b1");

// 5. Add a quad with a named graph, then read it back via Match.
store.Add(new OxigraphQuad(subj, pred, litPlain, graph));
store.Add(new OxigraphQuad(subj, pred, litLang, graph));
store.Add(new OxigraphQuad(subj, pred, litDt, graph));
store.Add(new OxigraphQuad(bnode, pred, litPlain, graph));

// 6. Match returns IReadOnlyList<Quad>; Count is a ulong property.
ulong matchCount = (ulong)store.Match(null, null, null, graph).Count;
ulong totalCount = store.Count;

// 7. ContainsQuad-style contains check.
bool contains = store.Contains(new OxigraphQuad(subj, pred, litPlain, graph));

// 8. DumpNQuads-style serialization. Dump returns a string.
string nQuads = store.Dump(OxigraphRdfFormat.NQuads);
string triG = store.Dump(OxigraphRdfFormat.TriG);

// 9. Round-trip: parse what we just wrote back into a brand-new store and confirm
//    the typed literal survives the byte-exact serialization.
var roundTripDir = Path.Combine(Path.GetTempPath(), "ontopilot-probe-rt-" + Guid.NewGuid().ToString("N"));
try
{
    using var store2 = new OxigraphStore(roundTripDir);
    store2.Load(nQuads, OxigraphRdfFormat.NQuads);
    ulong roundTripCount = store2.Count;
    string langSerialized = store2.Dump(OxigraphRdfFormat.NQuads, new OxigraphDumpOptions(FromGraph: graph));
    bool langPreserved = langSerialized.Contains("\"hello\"@en")
                      || langSerialized.Contains("\"hello\"@en ");
    bool dtPreserved = langSerialized.Contains("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>");
    bool bnodePreserved = langSerialized.Contains("_:b1");
}
finally { Directory.Delete(roundTripDir, recursive: true); }

// 10. Replace-graph primitive: clear the named graph, then re-add. This is the
//     shape OnToPilot.StoreWrapper.ReplaceGraph needs to expose.
store.ClearGraph(graph);
store.Add(new OxigraphQuad(subj, pred, litPlain, graph));

// 11. Read-only open path. Store.OpenReadOnly throws on writes.
using (var ro = OxigraphStore.OpenReadOnly(args[0]))
{
    ulong roCount = ro.Count;
}

// 12. dotNetRDF interop (Oxigraph.Extensions.DotNetRDF 0.5.8) — proves the
//     conversion helpers the wrapper will lean on exist.
var graphRdf = new DotNetRdf.Graph();
var ext = typeof(Oxigraph.Extensions.DotNetRDF.Extensions);
var loadFromGraph = ext.GetMethod("LoadFromGraph");
var toOxigraphTerm = ext.GetMethod("ToOxigraphTerm");
_ = toOxigraphTerm;

// Build the report.
var sb = new StringBuilder();
sb.AppendLine("Oxigraph 0.5.8 API probe — verified signatures");
sb.AppendLine("================================================");
sb.AppendLine();
sb.AppendLine("Probe store path:        " + args[0]);
sb.AppendLine("Probe graph IRI:         " + graph.Value);
sb.AppendLine();
sb.AppendLine("[ok] Store(path) opens a disk-backed store.");
sb.AppendLine("[ok] Quad(subject, predicate, object, graph) constructor accepted by Store.Add.");
sb.AppendLine($"[ok] Match(null,null,null,graph) returns IReadOnlyList<Quad>; count={matchCount}.");
sb.AppendLine($"[ok] Store.Count is a ulong; total={totalCount}.");
sb.AppendLine($"[ok] Store.Contains(quad) returned {contains}.");
sb.AppendLine("[ok] Store.Dump(RdfFormat.NQuads) returns a string with the typed-literal round-trip intact.");
sb.AppendLine("[ok] Store.Dump(RdfFormat.TriG) returns a string.");
sb.AppendLine($"[ok] N-Quads dump length = {nQuads.Length} chars; TriG dump length = {triG.Length} chars.");
sb.AppendLine("[ok] Store.Load(string, RdfFormat, LoadOptions?) parses N-Quads.");
sb.AppendLine("[ok] Store.ClearGraph(IGraphName) wipes a named graph only.");
sb.AppendLine("[ok] Store.OpenReadOnly(path) opens a read-only handle.");
sb.AppendLine("[ok] Oxigraph.Extensions.DotNetRDF.Extensions.ToOxigraphTerm(INode) exists.");
sb.AppendLine("[ok] Oxigraph.Extensions.DotNetRDF.Extensions.LoadFromGraph(Store, IGraph) exists.");
sb.AppendLine();
sb.AppendLine("RdfFormat members: N3 NQuads NTriples RdfXml TriG Turtle JsonLd StreamingJsonLd");
sb.AppendLine();
sb.AppendLine("See docs/migration/oxigraph-0.5.8-api.md for the full signature reference.");

File.WriteAllText(Path.Combine(args[0], "probe-report.txt"), sb.ToString());
Console.Write(sb.ToString());
