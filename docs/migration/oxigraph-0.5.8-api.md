# Oxigraph 0.5.8 — Verified API Reference

Source of truth: `src/OnToPilot.OxigraphProbe/` — compiles and runs against the
locked 0.5.8 packages and exercises every API surface that `StoreWrapper` and
its callers depend on.

## Package identity (correction to brief)

The task brief said `OxigraphCS` and namespace `Oxigraph`. The actual 0.5.8
release on nuget.org is:

```xml
<PackageReference Include="Oxigraph" Version="0.5.8" />
<PackageReference Include="Oxigraph.Extensions.DotNetRDF" Version="0.5.8" />
```

Both packages still expose the `Oxigraph` namespace, so the brief's
namespace claim was right; only the package id was renamed in the brief.

```csharp
using Oxigraph;
```

## `Store` — `Oxigraph.Store`

```csharp
public sealed class Store : IDisposable, IEnumerable<Quad>, IEnumerable
{
    // Open a disk-backed RocksDB store at `path`; null opens a memory store.
    public Store(string? path = null);

    // Open an existing on-disk store in read-only mode. Writes throw.
    public static Store OpenReadOnly(string path);

    // Quad-level CRUD
    public void Add(Quad quad);
    public void Remove(Quad quad);
    public bool Contains(Quad quad);

    // Pattern match. Any null parameter is treated as a wildcard.
    // `subject` is INamedOrBlankNode (NamedNode | BlankNode),
    // `object`  is ITerm (NamedNode | BlankNode | Literal | Triple),
    // `graph`   is IGraphName (NamedNode | BlankNode | DefaultGraph).
    public IReadOnlyList<Quad> Match(
        INamedOrBlankNode? subject = null,
        NamedNode?         predicate = null,
        ITerm?             @object = null,
        IGraphName?        graph = null);

    // Total triple count across the entire store.
    public ulong Count { get; }

    // Bulk add / clear.
    public void Extend(IEnumerable<Quad> quads);          // one-shot JSON encode
    public void BulkExtend(IEnumerable<Quad> quads);      // 10k-chunked bulk loader
    public void Clear();                                  // wipes every named graph
    public void Optimize();                               // compacts RocksDB SSTs

    // Graph-level helpers.
    public bool ContainsNamedGraph(IGraphName graph);
    public void AddGraph(INamedOrBlankNode graphName);    // empty graph
    public void RemoveGraph(INamedOrBlankNode graphName); // graph + all quads
    public void ClearGraph(IGraphName graph);             // graph only, keep slot

    // Serialization / deserialization.
    public void Load(string data, RdfFormat format, LoadOptions? options = null);
    public void LoadFromStream(Stream stream, RdfFormat format, LoadOptions? options = null);
    public void LoadFromFile(string filePath, RdfFormat format, LoadOptions? options = null);
    public void BulkLoad(string data, RdfFormat format, LoadOptions? options = null);
    public void BulkLoadFromFile(string filePath, RdfFormat format, LoadOptions? options = null);

    // Dump returns a string. ToStream/ToFile write directly to a sink.
    public string Dump(RdfFormat format, DumpOptions? options = null);
    public void   DumpToStream(Stream stream, RdfFormat format, DumpOptions? options = null);
    public void   DumpToFile(string filePath, RdfFormat format, DumpOptions? options = null);

    // SPARQL.
    public QueryResults Query(string sparql, QueryOptions? options = null);
    public void         Update(string sparql, UpdateOptions? options = null);

    // Async wrappers (Task.Run-bound; no real async I/O).
    public Task<QueryResults> QueryAsync(string sparql, QueryOptions? options = null, CancellationToken ct = default);
    public Task               UpdateAsync(string sparql, UpdateOptions? options = null, CancellationToken ct = default);
    public Task               LoadFromFileAsync(string filePath, RdfFormat format, LoadOptions? options = null, CancellationToken ct = default);
    public Task               BulkLoadFromFileAsync(string filePath, RdfFormat format, LoadOptions? options = null, CancellationToken ct = default);
    public Task               DumpToFileAsync(string filePath, RdfFormat format, DumpOptions? options = null, CancellationToken ct = default);
    public Task               LoadFromStreamAsync(Stream stream, RdfFormat format, LoadOptions? options = null, CancellationToken ct = default);
    public Task               DumpToStreamAsync(Stream stream, RdfFormat format, DumpOptions? options = null, CancellationToken ct = default);

    // Backup / flush / dispose.
    public void Flush();
    public void Backup(string targetDirectory);
    public Task BackupAsync(string targetDirectory, CancellationToken ct = default);
    public void Dispose();
}
```

## RDF terms

```csharp
public sealed record Quad(
    [JsonConverter(typeof(NamedOrBlankNodeConverter))] INamedOrBlankNode Subject,
    [JsonConverter(typeof(NamedNodeConverter))]        NamedNode          Predicate,
    [JsonConverter(typeof(TermConverter))]            ITerm              Object,
    [JsonConverter(typeof(GraphNameConverter))]       IGraphName         Graph)
{
    [JsonIgnore] public Triple Triple => new(Subject, Predicate, Object);
}

public sealed record NamedNode(string Value)  : INamedOrBlankNode, ITerm, IGraphName;
public sealed record BlankNode(string Value)  : INamedOrBlankNode, ITerm, IGraphName;
public sealed record Literal(
    string       Value,
    string?      Language  = null,
    NamedNode?   Datatype  = null,
    BaseDirection? Direction = null) : ITerm
{
    public static readonly NamedNode XsdString;
    public static readonly NamedNode XsdInteger;
    public static readonly NamedNode XsdDouble;
    public static readonly NamedNode XsdBoolean;
    public static Literal FromInt(int v);
    public static Literal FromDouble(double v);
    public static Literal FromBool(bool v);
}
[StructLayout(LayoutKind.Sequential, Size = 1)]
public readonly record struct DefaultGraph : IGraphName;
```

The probe verified: blank-node labels, language tags (`"hello"@en`), and
explicit datatypes (`"42"^^<http://www.w3.org/2001/XMLSchema#integer>`) all
survive an N-Quads dump → reload round-trip.

## `RdfFormat`

```csharp
public enum RdfFormat { N3, NQuads, NTriples, RdfXml, TriG, Turtle, JsonLd, StreamingJsonLd }
```

> **Format constraint.** Turtle, NTriples, N3, RDF/XML and JSON-LD are
> *triple*-only formats. Calling `Store.Dump(RdfFormat.Turtle)` on a store that
> has any non-default named graph throws `Oxigraph.ParseException("A RDF
> format supporting datasets was expected, Turtle found")`. Use `NQuads` or
> `TriG` for round-trips that include named graphs.

## `LoadOptions` and `DumpOptions`

```csharp
public sealed record LoadOptions(
    string?       BaseIri = null,
    IGraphName?   ToGraph = null,
    bool          Lenient = false,
    bool          RenameBlankNodes = false);

public sealed record DumpOptions(
    IGraphName?                   FromGraph = null,
    string?                       BaseIri   = null,
    Dictionary<string, string>?   Prefixes  = null);
```

## `Oxigraph.Extensions.DotNetRDF` 0.5.8

```csharp
public static class Extensions
{
    public static ITerm     ToOxigraphTerm(this INode node);   // VDS.RDF.INode -> Oxigraph.ITerm
    public static Quad      ToOxigraphQuad(this Triple triple);
    public static void      LoadFromGraph(this Store store, IGraph graph);
}
```

## Probe — how to reproduce

```bash
rm -rf /tmp/oxigraph-probe && mkdir -p /tmp/oxigraph-probe
dotnet build src/OnToPilot.OxigraphProbe -warnaserror
dotnet run --project src/OnToPilot.OxigraphProbe -- /tmp/oxigraph-probe
cat /tmp/oxigraph-probe/probe-report.txt
```

The probe leaves a `probe-report.txt` inside the target directory plus a
full RocksDB store (`CURRENT`, `MANIFEST-*`, `OPTIONS-*`, `LOCK`, `LOG`,
`*.sst`, `*.log`).

## Where the wrapper diverges from the brief

| Brief shape | Actual API | Wrapper choice |
|-------------|------------|----------------|
| `new Store(args[0])` | `Store(string? path = null)` | Same — `null` opens in-memory; we always pass a path. |
| `new Quad(s,p,o,g)` | `Quad(S,P,O,G)` (positional record) | Same. |
| `store.Match(null,null,null,g)` | Returns `IReadOnlyList<Quad>`, not enumerable | `StoreWrapper.Match` returns the same list; tests iterate `.Count` and indexer. |
| Dump API for diff | `Store.Dump(RdfFormat, DumpOptions)` returns a string | `DumpNQuads(graphIri)` calls `Dump(NQuads, DumpOptions(FromGraph: graph))` and UTF-8 encodes the result. |
| Replace graph primitive | No single `replace_graph` FFI call | Implemented as `ClearGraph` + `BulkExtend`. |

`StoreWrapper` deliberately does not wrap `Query`/`Update` — those are
sparql-specific and will live in `ABoxManager` / `SkosManager` in task 3.
