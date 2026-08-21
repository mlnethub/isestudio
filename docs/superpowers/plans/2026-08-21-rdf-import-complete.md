# Complete RDF Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current `rdf.import` placeholder with the full Python-compatible RDF import workflow for multipart uploads.

**Architecture:** Keep the controller thin, add a focused parser for RDF format normalization/parsing/partitioning, and add a scoped workflow service for permissions, active-extraction guard, graph writes, diffs, statistics, conflicts, terminology, validation, and audit. Route `rdf.import` through `InternalOperationDispatcher` so internal API behavior stays consistent with the rest of the migrated .NET backend.

**Tech Stack:** ASP.NET Core controllers, EF Core/Npgsql/SQLite tests, Oxigraph 0.5.8, dotNetRDF 3.5.2, xUnit, Docker Compose.

## Global Constraints

- Match Python `backend/app/api/rdf_import.py` response fields: `filename`, `sha256`, `format`, `target`, `strategy`, `base_iri`, `parsed_triples`, `tbox_triples`, `abox_triples`, `tbox_added`, `tbox_removed`, `abox_added`, `abox_removed`, `view`, `open_conflicts`, `validation`, `terminology`.
- Support `target`: `auto`, `tbox`, `abox`; reject anything else with HTTP 400.
- Support `strategy`: `merge`, `replace`; reject anything else with HTTP 400.
- Support `format`: `auto`, `turtle`, `rdfxml`, `ntriples`, `jsonld`, including aliases from the Python implementation.
- Default upload limit: `OnToPilot:RdfImportMaxBytes = 26214400`.
- Default parsed triple limit: `OnToPilot:RdfImportMaxTriples = 250000`.
- Default terminology sync behavior: `OnToPilot:AutomaticTerminology = true`.
- Reject RDF import with HTTP 409 when the target knowledge system has a pending or running extraction job.
- Preserve existing user changes in the working tree; do not revert unrelated edits.
- Use TDD: every production behavior change starts with a failing test, then the smallest passing implementation.
- Use raw N-Quads diff bytes consistently with the current .NET audit convention.

---

## File Structure

- Modify: `src/OnToPilot/Configuration/OnToPilotOptions.cs`
  - Adds `RdfImportMaxBytes`, `RdfImportMaxTriples`, and `AutomaticTerminology`.
- Create: `src/OnToPilot/Ontology/RdfImportParser.cs`
  - Owns format aliasing, auto sniffing, RDF parsing, blank-node scoping, max-triple enforcement, and TBox/ABox partitioning.
- Modify: `src/OnToPilot/Ontology/RdfImportService.cs`
  - Promotes current low-level N-Quads importer into the full workflow service, or replaces it with the workflow while preserving `ImportMode`.
- Create: `src/OnToPilot/Audit/AuditLogService.cs`
  - Single reusable audit writer for RDF import multi-graph events.
- Modify: `src/OnToPilot/Conflicts/ConflictService.cs`
  - Exposes non-semantic conflict sync for RDF import.
- Modify: `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs`
  - Registers `RdfImportParser`, `RdfImportService`, and `AuditLogService`.
- Modify: `src/OnToPilot/Controllers/RdfImportController.cs`
  - Keeps multipart binding and delegates to the facade with the uploaded bytes and form fields.
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`
  - Replaces `EmptyImportResponse()` with `InvokeRdfImportAsync(...)`.
- Modify: `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`
  - Adds HTTP-level tests for multipart import, invalid fields, active extraction guard, and response shape.
- Create: `src/OnToPilot.Tests/Ontology/RdfImportParserTests.cs`
  - Unit tests for parser aliases, sniffing, partitioning, blank-node scoping, and limits.
- Modify: `src/OnToPilot.Tests/Ontology/RdfRoundTripTests.cs`
  - Adds service-level merge/replace and rollback tests if not covered by HTTP tests.

---

### Task 1: Add Options And Preserve Multipart Contract

**Files:**
- Modify: `src/OnToPilot/Configuration/OnToPilotOptions.cs`
- Modify: `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`

**Interfaces:**
- Consumes: existing `RdfImportController.ImportAsync(...)` multipart endpoint.
- Produces: options properties used by later tasks:
  - `int RdfImportMaxBytes { get; set; }`
  - `int RdfImportMaxTriples { get; set; }`
  - `bool AutomaticTerminology { get; set; }`

- [ ] **Step 1: Write the failing response-shape HTTP test**

Add this test near the existing `Rdf_import_accepts_multipart_form_data` test in `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`:

```csharp
[Fact]
public async Task Rdf_import_returns_python_compatible_response_shape()
{
    await using var app = new AuthTestWebApplicationFactory();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var ksId = await CreateKsAsync(client, "rdf-import-shape");
    var multipart = new MultipartFormDataContent
    {
        { new StringContent("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:Pump> a owl:Class ."), "file", "pump.ttl" },
        { new StringContent("auto"), "target" },
        { new StringContent("merge"), "strategy" },
        { new StringContent("turtle"), "format" },
        { new StringContent("urn:base:"), "base_iri" },
    };

    var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("pump.ttl", body.GetProperty("filename").GetString());
    Assert.Equal("turtle", body.GetProperty("format").GetString());
    Assert.Equal("auto", body.GetProperty("target").GetString());
    Assert.Equal("merge", body.GetProperty("strategy").GetString());
    Assert.Equal("urn:base:", body.GetProperty("base_iri").GetString());
    Assert.Equal(1, body.GetProperty("parsed_triples").GetInt32());
    Assert.True(body.TryGetProperty("tbox_added", out _));
    Assert.True(body.TryGetProperty("abox_added", out _));
    Assert.True(body.TryGetProperty("view", out _));
    Assert.True(body.TryGetProperty("open_conflicts", out _));
    Assert.True(body.TryGetProperty("validation", out _));
    Assert.True(body.TryGetProperty("terminology", out _));
}
```

- [ ] **Step 2: Run the test and verify it fails on the placeholder response**

Run:

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import_returns_python_compatible_response_shape" --no-restore
```

Expected: FAIL because the current response only contains `graph_iri` and `triples_added`, so `filename` or `format` is missing.

- [ ] **Step 3: Add the options properties**

Edit `src/OnToPilot/Configuration/OnToPilotOptions.cs` and append these properties before the closing brace:

```csharp
/// <summary>Maximum RDF import upload size in bytes. Mirrors Python <c>rdf_import_max_bytes</c>.</summary>
public int RdfImportMaxBytes { get; set; } = 25 * 1024 * 1024;

/// <summary>Maximum parsed RDF statements accepted by a single import.</summary>
public int RdfImportMaxTriples { get; set; } = 250_000;

/// <summary>Whether TBox RDF imports trigger controlled terminology synchronization.</summary>
public bool AutomaticTerminology { get; set; } = true;
```

- [ ] **Step 4: Re-run the failing test**

Run the same command as Step 2.

Expected: still FAIL on missing response fields. This confirms the test is guarding workflow behavior, not only configuration.

- [ ] **Step 5: Commit this task**

```powershell
git add src/OnToPilot/Configuration/OnToPilotOptions.cs src/OnToPilot.Tests/Ontology/OntologyApiTests.cs
git commit -m "test: capture rdf import response contract"
```

---

### Task 2: Implement RDF Parser Format Handling And Limits

**Files:**
- Create: `src/OnToPilot/Ontology/RdfImportParser.cs`
- Create: `src/OnToPilot.Tests/Ontology/RdfImportParserTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ParsedRdfImport(string Format, IReadOnlyList<OntoTriple> Triples);`
  - `public sealed record RdfImportPartition(IReadOnlyList<OntoTriple> TBox, IReadOnlyList<OntoTriple> ABox);`
  - `public sealed class RdfImportParser`
  - `ParsedRdfImport Parse(byte[] data, string filename, string requestedFormat, string? baseIri, int? maxTriples, string blankNodeScope)`
  - `RdfImportPartition Partition(IReadOnlyList<OntoTriple> triples, string target)`
- Consumes: dotNetRDF parsers and Oxigraph term constructors.

- [ ] **Step 1: Write parser tests**

Create `src/OnToPilot.Tests/Ontology/RdfImportParserTests.cs`:

```csharp
using System.Text;
using OnToPilot.Ontology;
using Oxigraph;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Tests.Ontology;

public sealed class RdfImportParserTests
{
    private readonly RdfImportParser _parser = new();

    [Theory]
    [InlineData("ttl", "turtle")]
    [InlineData("rdf/xml", "rdfxml")]
    [InlineData("nt", "ntriples")]
    [InlineData("json-ld", "jsonld")]
    public void Parse_normalizes_supported_format_aliases(string requested, string expected)
    {
        var bytes = Encoding.UTF8.GetBytes("<urn:s> <urn:p> <urn:o> .");

        var parsed = _parser.Parse(bytes, "data.nt", requested, null, 10, "scope");

        Assert.Equal(expected, parsed.Format);
        Assert.Single(parsed.Triples);
    }

    [Fact]
    public void Parse_auto_uses_file_extension_before_sniffing()
    {
        var bytes = Encoding.UTF8.GetBytes("@prefix owl: <http://www.w3.org/2002/07/owl#> .\n<urn:Pump> a owl:Class .");

        var parsed = _parser.Parse(bytes, "pump.ttl", "auto", "urn:base:", 10, "scope");

        Assert.Equal("turtle", parsed.Format);
        Assert.Single(parsed.Triples);
    }

    [Fact]
    public void Parse_rejects_empty_files()
    {
        var ex = Assert.Throws<RdfImportException>(() =>
            _parser.Parse(Array.Empty<byte>(), "empty.ttl", "auto", null, 10, "scope"));

        Assert.Equal("The RDF file is empty", ex.Message);
    }

    [Fact]
    public void Parse_enforces_max_triples()
    {
        var bytes = Encoding.UTF8.GetBytes("<urn:s1> <urn:p> <urn:o> .\n<urn:s2> <urn:p> <urn:o> .");

        var ex = Assert.Throws<RdfImportException>(() =>
            _parser.Parse(bytes, "data.nt", "ntriples", null, 1, "scope"));

        Assert.Equal("RDF file exceeds the 1-triple import limit", ex.Message);
    }

    [Fact]
    public void Parse_scopes_blank_nodes_deterministically()
    {
        var bytes = Encoding.UTF8.GetBytes("_:b0 <urn:p> <urn:o> .");

        var parsed = _parser.Parse(bytes, "data.nt", "ntriples", null, 10, "abc123");

        var subject = Assert.IsType<OntoBlankNode>(parsed.Triples.Single().Subject);
        Assert.Contains("rdfimport_abc123_0", subject.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Partition_auto_places_schema_nodes_in_tbox_and_instances_in_abox()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
            <urn:Pump> a owl:Class .
            <urn:p101> rdf:type <urn:Pump> .
            """);
        var parsed = _parser.Parse(bytes, "mixed.ttl", "turtle", null, 10, "scope");

        var partition = _parser.Partition(parsed.Triples, "auto");

        Assert.Single(partition.TBox);
        Assert.Single(partition.ABox);
        Assert.Contains(partition.TBox, t => ((OntoNamedNode)t.Subject).Value == "urn:Pump");
        Assert.Contains(partition.ABox, t => ((OntoNamedNode)t.Subject).Value == "urn:p101");
    }
}
```

- [ ] **Step 2: Run parser tests and verify they fail because the parser does not exist**

Run:

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~RdfImportParserTests" --no-restore
```

Expected: compile FAIL with `RdfImportParser` and `RdfImportException` missing.

- [ ] **Step 3: Implement parser records, exception, format normalization, and partitioning**

Create `src/OnToPilot/Ontology/RdfImportParser.cs` with these public types and methods. Use dotNetRDF parsers for triple formats, then convert to Oxigraph terms. For JSON-LD, prefer dotNetRDF's JSON-LD parser if available; if the local package lacks it, keep the public alias but throw `RdfImportException("Could not parse RDF (jsonld: JSON-LD parser is unavailable)")` and add a skipped issue note in the final task.

```csharp
using System.Collections.Concurrent;
using System.Text;
using VDS.RDF;
using VDS.RDF.Parsing;
using OntoBlankNode = Oxigraph.BlankNode;
using OntoLiteral = Oxigraph.Literal;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoTriple = Oxigraph.Triple;

namespace OnToPilot.Ontology;

public sealed class RdfImportException : ValueException
{
    public RdfImportException(string message) : base(message) { }
}

public sealed record ParsedRdfImport(string Format, IReadOnlyList<OntoTriple> Triples);
public sealed record RdfImportPartition(IReadOnlyList<OntoTriple> TBox, IReadOnlyList<OntoTriple> ABox);

public sealed class RdfImportParser
{
    private static readonly IReadOnlyDictionary<string, string> FormatAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ttl"] = "turtle",
        ["turtle"] = "turtle",
        ["rdf"] = "rdfxml",
        ["rdf/xml"] = "rdfxml",
        ["rdfxml"] = "rdfxml",
        ["xml"] = "rdfxml",
        ["nt"] = "ntriples",
        ["n-triples"] = "ntriples",
        ["ntriples"] = "ntriples",
        ["json"] = "jsonld",
        ["json-ld"] = "jsonld",
        ["jsonld"] = "jsonld",
    };

    private static readonly IReadOnlyDictionary<string, string> ExtensionFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".ttl"] = "turtle",
        [".rdf"] = "rdfxml",
        [".xml"] = "rdfxml",
        [".nt"] = "ntriples",
        [".jsonld"] = "jsonld",
        [".json"] = "jsonld",
    };

    public ParsedRdfImport Parse(byte[] data, string filename, string requestedFormat, string? baseIri, int? maxTriples, string blankNodeScope)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(data)))
        {
            throw new RdfImportException("The RDF file is empty");
        }

        var errors = new List<string>();
        foreach (var format in CandidateFormats(data, filename, requestedFormat))
        {
            try
            {
                var triples = ParseWithDotNetRdf(data, format, baseIri, maxTriples, blankNodeScope);
                return new ParsedRdfImport(format, triples);
            }
            catch (RdfImportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{format}: {ex.Message}");
                if (!string.Equals(NormalizeFormat(requestedFormat), "auto", StringComparison.Ordinal)) break;
            }
        }
        throw new RdfImportException($"Could not parse RDF ({(errors.Count == 0 ? "unknown parser error" : errors[0])})");
    }

    public RdfImportPartition Partition(IReadOnlyList<OntoTriple> triples, string target)
    {
        var normalized = target.Trim().ToLowerInvariant();
        if (normalized == "tbox") return new RdfImportPartition(triples, Array.Empty<OntoTriple>());
        if (normalized == "abox") return new RdfImportPartition(Array.Empty<OntoTriple>(), triples);
        if (normalized != "auto") throw new RdfImportException($"Unsupported RDF import target: {target}");
        return SplitTBoxABox(triples);
    }

    public static string NormalizeFormat(string value)
    {
        var key = value.Trim().ToLowerInvariant();
        if (key == "auto") return key;
        if (FormatAliases.TryGetValue(key, out var normalized)) return normalized;
        throw new RdfImportException($"Unsupported RDF format: {value}");
    }

    private static IReadOnlyList<string> CandidateFormats(byte[] data, string filename, string requested)
    {
        var normalized = NormalizeFormat(requested);
        if (normalized != "auto") return [normalized];
        var ext = Path.GetExtension(filename ?? string.Empty);
        var first = ExtensionFormats.TryGetValue(ext, out var byExt) ? byExt : SniffFormat(data);
        return new[] { first }.Concat(FormatAliases.Values.Distinct(StringComparer.Ordinal).Where(f => f != first)).ToList();
    }

    private static string SniffFormat(byte[] data)
    {
        var head = Encoding.UTF8.GetString(data).TrimStart().ToLowerInvariant();
        if (head.StartsWith("{") || head.StartsWith("[")) return "jsonld";
        if (head.StartsWith("<?xml", StringComparison.Ordinal) || head.Contains("<rdf:rdf", StringComparison.Ordinal)) return "rdfxml";
        if (head.StartsWith("@prefix", StringComparison.Ordinal) || head.StartsWith("prefix ", StringComparison.Ordinal) || head.Contains("@prefix ", StringComparison.Ordinal)) return "turtle";
        return head.StartsWith("<", StringComparison.Ordinal) ? "ntriples" : "turtle";
    }

    private static IReadOnlyList<OntoTriple> ParseWithDotNetRdf(byte[] data, string format, string? baseIri, int? maxTriples, string blankNodeScope)
    {
        var graph = new Graph();
        if (!string.IsNullOrWhiteSpace(baseIri)) graph.BaseUri = new Uri(baseIri, UriKind.RelativeOrAbsolute);
        var text = Encoding.UTF8.GetString(data);
        IRdfReader parser = format switch
        {
            "turtle" => new TurtleParser(),
            "rdfxml" => new RdfXmlParser(),
            "ntriples" => new NTriplesParser(),
            "jsonld" => throw new RdfImportException("Could not parse RDF (jsonld: JSON-LD parser is unavailable)"),
            _ => throw new RdfImportException($"Unsupported RDF format: {format}"),
        };
        parser.Load(graph, new StringReader(text));

        var blankNodes = new Dictionary<string, OntoBlankNode>(StringComparer.Ordinal);
        var triples = new List<OntoTriple>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var triple in graph.Triples)
        {
            if (maxTriples is not null && triples.Count + 1 > maxTriples.Value)
            {
                throw new RdfImportException($"RDF file exceeds the {maxTriples.Value:N0}-triple import limit");
            }
            var converted = new OntoTriple(
                ToSubject(triple.Subject, blankNodes, blankNodeScope),
                ToPredicate(triple.Predicate),
                ToObject(triple.Object, blankNodes, blankNodeScope));
            var key = $"{converted.Subject}|{converted.Predicate}|{converted.Object}";
            if (seen.Add(key)) triples.Add(converted);
        }
        return triples;
    }

    private static Oxigraph.INamedOrBlankNode ToSubject(INode node, Dictionary<string, OntoBlankNode> blanks, string scope) => node.NodeType switch
    {
        NodeType.Uri => new OntoNamedNode(((IUriNode)node).Uri.AbsoluteUri),
        NodeType.Blank => blanks.TryGetValue(((IBlankNode)node).InternalID, out var existing)
            ? existing
            : blanks[((IBlankNode)node).InternalID] = new OntoBlankNode($"rdfimport_{scope}_{blanks.Count}"),
        _ => throw new RdfImportException($"Unsupported RDF subject node: {node.NodeType}"),
    };

    private static OntoNamedNode ToPredicate(INode node)
    {
        if (node is IUriNode uri) return new OntoNamedNode(uri.Uri.AbsoluteUri);
        throw new RdfImportException($"Unsupported RDF predicate node: {node.NodeType}");
    }

    private static object ToObject(INode node, Dictionary<string, OntoBlankNode> blanks, string scope) => node.NodeType switch
    {
        NodeType.Uri => new OntoNamedNode(((IUriNode)node).Uri.AbsoluteUri),
        NodeType.Blank => blanks.TryGetValue(((IBlankNode)node).InternalID, out var existing)
            ? existing
            : blanks[((IBlankNode)node).InternalID] = new OntoBlankNode($"rdfimport_{scope}_{blanks.Count}"),
        NodeType.Literal => ToLiteral((ILiteralNode)node),
        _ => throw new RdfImportException($"Unsupported RDF object node: {node.NodeType}"),
    };

    private static OntoLiteral ToLiteral(ILiteralNode literal)
    {
        if (!string.IsNullOrEmpty(literal.Language)) return new OntoLiteral(literal.Value, Language: literal.Language);
        if (literal.DataType is not null) return new OntoLiteral(literal.Value, Datatype: new OntoNamedNode(literal.DataType.AbsoluteUri));
        return new OntoLiteral(literal.Value);
    }

    private static RdfImportPartition SplitTBoxABox(IReadOnlyList<OntoTriple> triples)
    {
        var schemaNodes = new HashSet<object>();
        foreach (var triple in triples)
        {
            var predicate = triple.Predicate.Value;
            var objectIri = triple.Object is OntoNamedNode node ? node.Value : null;
            if (predicate == Vocabulary.RdfType.Value && objectIri is not null && SchemaTypes.Contains(objectIri)) schemaNodes.Add(triple.Subject);
            if (SchemaSubjectPredicates.Contains(predicate)) schemaNodes.Add(triple.Subject);
            if ((ClassLinkPredicates.Contains(predicate) || PropertyLinkPredicates.Contains(predicate)) && triple.Object is Oxigraph.INamedOrBlankNode linked) schemaNodes.Add(linked);
        }
        var tbox = new List<OntoTriple>();
        var abox = new List<OntoTriple>();
        foreach (var triple in triples) (schemaNodes.Contains(triple.Subject) ? tbox : abox).Add(triple);
        return new RdfImportPartition(tbox, abox);
    }

    private static string Owl(string local) => Vocabulary.Owl.Value + local;

    private static readonly HashSet<string> SchemaTypes = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfProperty.Value, Vocabulary.RdfsClass.Value, Vocabulary.RdfsDatatype.Value,
        Owl("Class"), Owl("Restriction"), Owl("Ontology"), Owl("ObjectProperty"), Owl("DatatypeProperty"),
        Owl("AnnotationProperty"), Owl("OntologyProperty"), Owl("FunctionalProperty"), Owl("InverseFunctionalProperty"),
        Owl("TransitiveProperty"), Owl("SymmetricProperty"), Owl("AsymmetricProperty"), Owl("ReflexiveProperty"),
        Owl("IrreflexiveProperty"), Owl("DeprecatedClass"), Owl("DeprecatedProperty"), Owl("AllDisjointClasses"),
        Owl("AllDisjointProperties"), "http://www.w3.org/ns/shacl#NodeShape", "http://www.w3.org/ns/shacl#PropertyShape",
    };

    private static readonly HashSet<string> ClassLinkPredicates = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfsSubClassOf.Value, Vocabulary.RdfsDomain.Value, Vocabulary.RdfsRange.Value,
        Owl("equivalentClass"), Owl("disjointWith"), Owl("complementOf"), Owl("onClass"), Owl("onDataRange"),
        Owl("someValuesFrom"), Owl("allValuesFrom"), "http://www.w3.org/ns/shacl#class",
        "http://www.w3.org/ns/shacl#targetClass", "http://www.w3.org/ns/shacl#datatype",
    };

    private static readonly HashSet<string> PropertyLinkPredicates = new(StringComparer.Ordinal)
    {
        Vocabulary.RdfsSubPropertyOf.Value, Owl("equivalentProperty"), Owl("propertyDisjointWith"),
        Owl("inverseOf"), Owl("onProperty"), "http://www.w3.org/ns/shacl#path",
    };

    private static readonly HashSet<string> SchemaSubjectPredicates = new(ClassLinkPredicates.Concat(PropertyLinkPredicates), StringComparer.Ordinal);
}
```

If `ValueException` is not available, change `RdfImportException : Exception`; do not change tests.

- [ ] **Step 4: Run parser tests and fix compile details only**

Run:

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~RdfImportParserTests" --no-restore
```

Expected: PASS after correcting any exact dotNetRDF type names discovered by the compiler.

- [ ] **Step 5: Commit this task**

```powershell
git add src/OnToPilot/Ontology/RdfImportParser.cs src/OnToPilot.Tests/Ontology/RdfImportParserTests.cs
git commit -m "feat: parse rdf import payloads"
```

---

### Task 3: Add Reusable Audit Writer

**Files:**
- Create: `src/OnToPilot/Audit/AuditLogService.cs`
- Modify: `src/OnToPilot/Program.cs`
- Test: `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`

**Interfaces:**
- Produces:
  - `Task RecordAsync(Guid ksId, UserEntity actor, string action, string summary, IReadOnlyDictionary<string, object?>? detail, string? graph, byte[] added, byte[] removed, string? groupId, CancellationToken ct)`

- [ ] **Step 1: Write failing audit test**

Add to `OntologyApiTests.cs`:

```csharp
[Fact]
public async Task Rdf_import_writes_grouped_audit_rows_for_changed_graphs()
{
    await using var app = new AuthTestWebApplicationFactory();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var ksId = await CreateKsAsync(client, "rdf-import-audit");
    var multipart = new MultipartFormDataContent
    {
        { new StringContent("""
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
            <urn:Pump> a owl:Class .
            <urn:p101> rdf:type <urn:Pump> .
            """), "file", "mixed.ttl" },
        { new StringContent("auto"), "target" },
        { new StringContent("merge"), "strategy" },
        { new StringContent("turtle"), "format" },
    };

    var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var audits = LookupAuditEventsFor(app, ksId).Where(a => a.Action == "rdf.import").ToList();
    Assert.Equal(2, audits.Count);
    Assert.All(audits, a => Assert.False(string.IsNullOrWhiteSpace(a.GroupId)));
    Assert.Single(audits.Select(a => a.GroupId).Distinct());
    Assert.Contains(audits, a => a.Summary.Contains("ontology", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(audits, a => a.Summary.Contains("instances", StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 2: Run and verify failure**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import_writes_grouped_audit_rows_for_changed_graphs" --no-restore
```

Expected: FAIL because no real import/audit workflow exists.

- [ ] **Step 3: Implement `AuditLogService`**

Create `src/OnToPilot/Audit/AuditLogService.cs`:

```csharp
using System.Text.Json;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Infrastructure.Persistence.Legacy;

namespace OnToPilot.Audit;

public sealed class AuditLogService
{
    private readonly LegacyIdAllocator _allocator;
    private readonly TimeProvider _clock;

    public AuditLogService(LegacyIdAllocator allocator, TimeProvider clock)
    {
        _allocator = allocator;
        _clock = clock;
    }

    public async Task RecordAsync(
        Guid ksId,
        UserEntity actor,
        string action,
        string summary,
        IReadOnlyDictionary<string, object?>? detail,
        string? graph,
        byte[] added,
        byte[] removed,
        string? groupId,
        CancellationToken ct)
    {
        JsonDocument? detailDoc = null;
        if (detail is not null)
        {
            detailDoc = JsonDocument.Parse(JsonSerializer.Serialize(detail));
        }

        await _allocator.AllocateAndPersistAsync(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId,
            ActorId = actor.Id,
            ActorName = actor.DisplayName ?? actor.Username,
            Action = action,
            Summary = summary,
            Detail = detailDoc,
            Graph = graph,
            GroupId = groupId,
            Added = added.Length == 0 ? null : added,
            Removed = removed.Length == 0 ? null : removed,
            CreatedAt = _clock.GetUtcNow(),
        }, ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Register the service**

In `src/OnToPilot/Program.cs`, after `builder.Services.AddScoped<LegacyIdAllocator>();`, add:

```csharp
builder.Services.AddScoped<OnToPilot.Audit.AuditLogService>();
```

- [ ] **Step 5: Do not expect the audit test to pass yet**

Run the same audit test.

Expected: still FAIL because the workflow has not called `AuditLogService`.

- [ ] **Step 6: Commit this task**

```powershell
git add src/OnToPilot/Audit/AuditLogService.cs src/OnToPilot/Program.cs src/OnToPilot.Tests/Ontology/OntologyApiTests.cs
git commit -m "feat: add shared audit log writer"
```

---

### Task 4: Expose Non-Semantic Conflict Sync

**Files:**
- Modify: `src/OnToPilot/Conflicts/ConflictService.cs`
- Test: `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`

**Interfaces:**
- Produces:
  - `Task<IReadOnlyList<ConflictOut>> SyncAfterOntologyMutationAsync(Guid ksId, bool semantic, CancellationToken ct)`

- [ ] **Step 1: Add a response conflict field assertion to the shape test**

In `Rdf_import_returns_python_compatible_response_shape`, replace the simple `open_conflicts` check with:

```csharp
var conflicts = body.GetProperty("open_conflicts");
Assert.Equal(JsonValueKind.Array, conflicts.ValueKind);
```

- [ ] **Step 2: Run the response-shape test**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import_returns_python_compatible_response_shape" --no-restore
```

Expected: still FAIL on missing real response fields.

- [ ] **Step 3: Add public sync method**

In `src/OnToPilot/Conflicts/ConflictService.cs`, add this public method just below `DetectAsync(...)`:

```csharp
public async Task<IReadOnlyList<ConflictOut>> SyncAfterOntologyMutationAsync(
    Guid ksId,
    bool semantic,
    CancellationToken ct)
{
    var ks = await ResolveKnowledgeSystemAsync(ksId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Knowledge system {ksId} not found.");
    return semantic
        ? await DetectAsync(ksId, ct).ConfigureAwait(false)
        : await DetectAndSyncWithoutSemanticAsync(ks, ct).ConfigureAwait(false);
}
```

- [ ] **Step 4: Run conflict service tests or compile slice**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~Conflict" --no-restore
```

Expected: PASS or `No test matches` with build success. If no conflict tests exist, run:

```powershell
dotnet build .\src\OnToPilot.sln --no-restore
```

Expected: build succeeds.

- [ ] **Step 5: Commit this task**

```powershell
git add src/OnToPilot/Conflicts/ConflictService.cs src/OnToPilot.Tests/Ontology/OntologyApiTests.cs
git commit -m "feat: expose non-semantic conflict sync"
```

---

### Task 5: Implement Full RDF Import Workflow

**Files:**
- Modify: `src/OnToPilot/Ontology/RdfImportService.cs`
- Modify: `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs`
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`
- Test: `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`

**Interfaces:**
- Consumes:
  - `RdfImportParser.Parse(...)`
  - `RdfImportParser.Partition(...)`
  - `AuditLogService.RecordAsync(...)`
  - `ConflictService.SyncAfterOntologyMutationAsync(...)`
  - `KnowledgeStatsService.RefreshAsync(...)`
  - `ITerminologySync.SyncAsync(...)`
  - `ABoxValidator.Validate(...)`
  - `ExtractionJobStore.FindActiveJobAsync(...)`
- Produces:
  - `public sealed record RdfImportRequest(...)`
  - `public sealed class RdfImportService`
  - `Task<object> ImportAsync(Guid ksId, RdfImportRequest request, Actor actor, CancellationToken ct)`

- [ ] **Step 1: Add graph-write assertions to the HTTP test**

In `Rdf_import_returns_python_compatible_response_shape`, after reading `body`, add:

```csharp
var store = app.Services.GetRequiredService<StoreWrapper>();
var graphIri = LookupKsGraphIri(app, ksId);
Assert.Single(store.Match(
    subjectIri: "urn:Pump",
    predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
    objectIri: "http://www.w3.org/2002/07/owl#Class",
    graphIri: graphIri));
Assert.Equal(1, body.GetProperty("tbox_triples").GetInt32());
Assert.Equal(0, body.GetProperty("abox_triples").GetInt32());
Assert.Equal(1, body.GetProperty("tbox_added").GetInt32());
```

- [ ] **Step 2: Add invalid-field tests**

Add to `OntologyApiTests.cs`:

```csharp
[Theory]
[InlineData("bad", "merge", "turtle", "target must be auto, tbox, or abox")]
[InlineData("auto", "bad", "turtle", "strategy must be merge or replace")]
[InlineData("auto", "merge", "bad", "Unsupported RDF format: bad")]
public async Task Rdf_import_rejects_invalid_form_fields(string target, string strategy, string format, string expected)
{
    await using var app = new AuthTestWebApplicationFactory();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var ksId = await CreateKsAsync(client, "rdf-import-invalid");
    var multipart = new MultipartFormDataContent
    {
        { new StringContent("<urn:s> <urn:p> <urn:o> ."), "file", "data.nt" },
        { new StringContent(target), "target" },
        { new StringContent(strategy), "strategy" },
        { new StringContent(format), "format" },
    };

    var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", multipart);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Contains(expected, body.GetProperty("detail").GetString(), StringComparison.Ordinal);
}
```

- [ ] **Step 3: Run workflow tests and verify they fail**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import" --no-restore
```

Expected: FAIL because `rdf.import` is still routed to `EmptyImportResponse()`.

- [ ] **Step 4: Replace `RdfImportService.cs` with workflow implementation**

Keep `ImportMode`, then add these records and constructor dependencies:

```csharp
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnToPilot.Application.Foundation;
using OnToPilot.Audit;
using OnToPilot.Authorization;
using OnToPilot.Configuration;
using OnToPilot.Conflicts;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Knowledge;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;
using OntoTriple = Oxigraph.Triple;

namespace OnToPilot.Ontology;

public sealed record RdfImportRequest(
    byte[] File,
    string Filename,
    string? ContentType,
    string Target,
    string Strategy,
    string Format,
    string? BaseIri);

public sealed class RdfImportService
{
    private readonly OnToPilotDbContext _db;
    private readonly KnowledgeSystemAccessService _access;
    private readonly ExtractionJobStore _jobs;
    private readonly StoreWrapper _store;
    private readonly RdfImportParser _parser;
    private readonly KnowledgeStatsService _stats;
    private readonly ConflictService _conflicts;
    private readonly ITerminologySync _terminology;
    private readonly ABoxValidator _validator;
    private readonly OntologyViewBuilder _viewBuilder;
    private readonly AuditLogService _audit;
    private readonly OnToPilotOptions _options;

    public RdfImportService(
        OnToPilotDbContext db,
        KnowledgeSystemAccessService access,
        ExtractionJobStore jobs,
        StoreWrapper store,
        RdfImportParser parser,
        KnowledgeStatsService stats,
        ConflictService conflicts,
        ITerminologySync terminology,
        ABoxValidator validator,
        OntologyViewBuilder viewBuilder,
        AuditLogService audit,
        IOptions<OnToPilotOptions> options)
    {
        _db = db;
        _access = access;
        _jobs = jobs;
        _store = store;
        _parser = parser;
        _stats = stats;
        _conflicts = conflicts;
        _terminology = terminology;
        _validator = validator;
        _viewBuilder = viewBuilder;
        _audit = audit;
        _options = options.Value;
    }
}
```

Then add `ImportAsync(...)` with this behavior:

```csharp
public async Task<object> ImportAsync(Guid ksId, RdfImportRequest request, Actor actor, CancellationToken ct)
{
    var (user, ks) = await RequireEditorAsync(ksId, actor, ct).ConfigureAwait(false);
    var activeJob = await _jobs.FindActiveJobAsync(ksId, ct).ConfigureAwait(false);
    if (activeJob is not null)
    {
        throw new GraphWriteConflictException("An extraction is in progress; try again after it finishes.", activeJob.Value);
    }

    var target = request.Target.Trim().ToLowerInvariant();
    var strategy = request.Strategy.Trim().ToLowerInvariant();
    if (target is not ("auto" or "tbox" or "abox")) throw new ArgumentException("target must be auto, tbox, or abox");
    if (strategy is not ("merge" or "replace")) throw new ArgumentException("strategy must be merge or replace");
    if (request.File.Length > _options.RdfImportMaxBytes)
    {
        throw new InvalidOperationException($"RDF file exceeds the {_options.RdfImportMaxBytes:N0}-byte upload limit");
    }

    var effectiveBaseIri = string.IsNullOrWhiteSpace(request.BaseIri) ? ks.BaseIri : request.BaseIri.Trim();
    var filename = Path.GetFileName(string.IsNullOrWhiteSpace(request.Filename) ? "import.rdf" : request.Filename);
    var sha = Convert.ToHexString(SHA256.HashData(request.File)).ToLowerInvariant();
    var blankScope = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{ks.GraphIri}\0{effectiveBaseIri}\0{target}\0{sha}"))).ToLowerInvariant()[..24];
    var parsed = _parser.Parse(request.File, filename, request.Format, effectiveBaseIri, _options.RdfImportMaxTriples, blankScope);
    if (parsed.Triples.Count == 0) throw new ArgumentException("The RDF document contains no triples");
    var partition = _parser.Partition(parsed.Triples, target);
    var ksContext = KsContext.FromEntity(ks);

    var tboxResult = await ApplyLayerAsync(ksContext.TBoxGraph, partition.TBox, strategy, ct).ConfigureAwait(false);
    var aboxResult = await ApplyLayerAsync(ksContext.ABoxGraph, partition.ABox, strategy, ct).ConfigureAwait(false);

    IReadOnlyList<ConflictOut> openConflicts;
    if (tboxResult.Changed)
    {
        await _stats.RefreshAsync(ksId, ct).ConfigureAwait(false);
        openConflicts = await _conflicts.SyncAfterOntologyMutationAsync(ksId, semantic: false, ct).ConfigureAwait(false);
    }
    else
    {
        openConflicts = await _conflicts.ListAsync(ksId, "open", ctype: null, ct).ConfigureAwait(false);
    }

    var terminology = new Dictionary<string, object?>
    {
        ["terms_added"] = 0,
        ["terms_mapped"] = 0,
        ["terminology_error"] = null,
    };
    if (tboxResult.Changed && _options.AutomaticTerminology)
    {
        try
        {
            var termResult = _terminology.SyncAsync(ksContext, ct);
            terminology["terms_added"] = termResult.TermsAdded;
            terminology["terms_mapped"] = termResult.TermsMapped;
        }
        catch (Exception ex)
        {
            terminology["terminology_error"] = ex.Message;
        }
    }

    var detail = new Dictionary<string, object?>
    {
        ["filename"] = filename,
        ["sha256"] = sha,
        ["format"] = parsed.Format,
        ["target"] = target,
        ["strategy"] = strategy,
        ["base_iri"] = effectiveBaseIri,
        ["parsed_triples"] = parsed.Triples.Count,
        ["tbox_triples"] = partition.TBox.Count,
        ["abox_triples"] = partition.ABox.Count,
    };

    var changedGraphs = (tboxResult.Changed ? 1 : 0) + (aboxResult.Changed ? 1 : 0);
    var groupId = changedGraphs > 1 ? Random.Shared.NextInt64().ToString("x") : null;
    if (tboxResult.Changed)
    {
        await _audit.RecordAsync(ksId, user, "rdf.import", $"Imported RDF ontology from \"{filename}\"", AddGraphTarget(detail, "tbox"), ks.GraphIri, tboxResult.Added, tboxResult.Removed, groupId, ct).ConfigureAwait(false);
    }
    if (aboxResult.Changed)
    {
        await _audit.RecordAsync(ksId, user, "rdf.import", $"Imported RDF instances from \"{filename}\"", AddGraphTarget(detail, "abox"), ksContext.ABoxGraph, aboxResult.Added, aboxResult.Removed, groupId, ct).ConfigureAwait(false);
    }
    if (!tboxResult.Changed && !aboxResult.Changed)
    {
        await _audit.RecordAsync(ksId, user, "rdf.import", $"RDF import from \"{filename}\" made no changes", detail, graph: null, Array.Empty<byte>(), Array.Empty<byte>(), groupId: null, ct).ConfigureAwait(false);
    }

    var validation = _validator.Validate(ksContext);
    var view = await _viewBuilder.BuildFromStoreAsync(_store, ks.GraphIri, ct).ConfigureAwait(false);
    return new Dictionary<string, object?>
    {
        ["filename"] = filename,
        ["sha256"] = sha,
        ["format"] = parsed.Format,
        ["target"] = target,
        ["strategy"] = strategy,
        ["base_iri"] = effectiveBaseIri,
        ["parsed_triples"] = parsed.Triples.Count,
        ["tbox_triples"] = partition.TBox.Count,
        ["abox_triples"] = partition.ABox.Count,
        ["tbox_added"] = CountDiff(tboxResult.Added),
        ["tbox_removed"] = CountDiff(tboxResult.Removed),
        ["abox_added"] = CountDiff(aboxResult.Added),
        ["abox_removed"] = CountDiff(aboxResult.Removed),
        ["view"] = view,
        ["open_conflicts"] = openConflicts,
        ["validation"] = new Dictionary<string, object?>
        {
            ["counts"] = new Dictionary<string, object?> { ["error"] = validation.ErrorCount, ["warning"] = validation.WarningCount },
            ["truncated"] = validation.Truncated,
        },
        ["terminology"] = terminology,
    };
}
```

Add private helpers in the same class:

```csharp
private sealed record LayerApplyResult(byte[] Added, byte[] Removed)
{
    public bool Changed => Added.Length > 0 || Removed.Length > 0;
}

private async Task<LayerApplyResult> ApplyLayerAsync(string graphIri, IReadOnlyList<OntoTriple> triples, string strategy, CancellationToken ct)
{
    await using var capture = await _store.CaptureAsync(graphIri, revertOnError: false, cancellationToken: ct).ConfigureAwait(false);
    try
    {
        var graph = new OntoNamedNode(graphIri);
        if (strategy == "replace") _store.ReplaceGraph(graph, Array.Empty<OntoQuad>());
        var quads = triples.Select(t => new OntoQuad(t.Subject, t.Predicate, t.Object, graph)).ToList();
        _store.AddQuads(graph, quads);
        var post = _store.DumpNQuads(graph);
        var diff = StoreWrapper.DiffNQuads(capture.SnapshotNQuads.ToArray(), post);
        return new LayerApplyResult(diff.Added, diff.Removed);
    }
    catch
    {
        capture.MarkError();
        throw;
    }
}

private async Task<(UserEntity User, KnowledgeSystemEntity Ks)> RequireEditorAsync(Guid ksId, Actor actor, CancellationToken ct)
{
    var userGuid = Guid.Parse(actor.Id);
    var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userGuid && u.Active, ct).ConfigureAwait(false)
        ?? throw new UnauthorizedAccessException("User not found or inactive.");
    var ks = await _db.KnowledgeSystems.FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Knowledge system {ksId} not found.");
    if (!await _access.HasAtLeastAsync(user.Id, ks.Id, KSRole.Editor, ct).ConfigureAwait(false))
    {
        throw new UnauthorizedAccessException("Editor role required.");
    }
    return (user, ks);
}

private static IReadOnlyDictionary<string, object?> AddGraphTarget(IReadOnlyDictionary<string, object?> detail, string graphTarget)
{
    var copy = new Dictionary<string, object?>(detail, StringComparer.Ordinal) { ["graph_target"] = graphTarget };
    return copy;
}

private static int CountDiff(byte[] nQuads)
{
    if (nQuads.Length == 0) return 0;
    return System.Text.Encoding.UTF8.GetString(nQuads).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}
```

- [ ] **Step 5: Register parser and workflow**

In `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs`, inside `AddOntologyServices()`, add:

```csharp
services.AddSingleton<RdfImportParser>();
services.AddScoped<RdfImportService>();
```

- [ ] **Step 6: Wire dispatcher to workflow**

In `src/OnToPilot/Integration/InternalOperationDispatcher.cs`, replace:

```csharp
"rdf.import" => Task.FromResult<object?>(EmptyImportResponse()),
```

with:

```csharp
"rdf.import" => InvokeRdfImportAsync(request, cancellationToken),
```

Add this method near other invoke helpers:

```csharp
private async Task<object?> InvokeRdfImportAsync(InternalRequest request, CancellationToken ct)
{
    var svc = _services.GetService<RdfImportService>();
    if (svc is null || request.KnowledgeSystemGuid is null || request.Body is null)
    {
        throw new InvalidOperationException("RDF import service unavailable.");
    }
    var body = request.Body;
    var importRequest = new RdfImportRequest(
        File: body.TryGetValue("file", out var file) && file is byte[] bytes ? bytes : Array.Empty<byte>(),
        Filename: body.TryGetValue("filename", out var filename) ? Convert.ToString(filename) ?? "import.rdf" : "import.rdf",
        ContentType: body.TryGetValue("content_type", out var contentType) ? Convert.ToString(contentType) : null,
        Target: body.TryGetValue("target", out var target) ? Convert.ToString(target) ?? "auto" : "auto",
        Strategy: body.TryGetValue("strategy", out var strategy) ? Convert.ToString(strategy) ?? "merge" : "merge",
        Format: body.TryGetValue("format", out var format) ? Convert.ToString(format) ?? "auto" : "auto",
        BaseIri: body.TryGetValue("base_iri", out var baseIri) ? Convert.ToString(baseIri) : null);
    return await svc.ImportAsync(request.KnowledgeSystemGuid.Value, importRequest, request.Actor, ct).ConfigureAwait(false);
}
```

- [ ] **Step 7: Run RDF import HTTP tests**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import" --no-restore
```

Expected: PASS for multipart, response shape, invalid fields, and audit tests.

- [ ] **Step 8: Commit this task**

```powershell
git add src/OnToPilot/Ontology/RdfImportService.cs src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs src/OnToPilot/Integration/InternalOperationDispatcher.cs src/OnToPilot.Tests/Ontology/OntologyApiTests.cs
git commit -m "feat: wire full rdf import workflow"
```

---

### Task 6: Add Replace, Rollback, And Active Extraction Coverage

**Files:**
- Modify: `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`
- Modify: `src/OnToPilot/Ontology/RdfImportService.cs`

**Interfaces:**
- Consumes: `RdfImportService.ImportAsync(...)` from Task 5.
- Produces: verified replace semantics, rollback semantics, and KS-scoped active extraction conflict behavior.

- [ ] **Step 1: Add replace behavior test**

Add to `OntologyApiTests.cs`:

```csharp
[Fact]
public async Task Rdf_import_replace_clears_target_layer_before_import()
{
    await using var app = new AuthTestWebApplicationFactory();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var ksId = await CreateKsAsync(client, "rdf-import-replace");
    var store = app.Services.GetRequiredService<StoreWrapper>();
    var graphIri = LookupKsGraphIri(app, ksId);

    await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", new MultipartFormDataContent
    {
        { new StringContent("<urn:Old> a <http://www.w3.org/2002/07/owl#Class> ."), "file", "old.nt" },
        { new StringContent("tbox"), "target" },
        { new StringContent("merge"), "strategy" },
        { new StringContent("ntriples"), "format" },
    });

    var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", new MultipartFormDataContent
    {
        { new StringContent("<urn:New> a <http://www.w3.org/2002/07/owl#Class> ."), "file", "new.nt" },
        { new StringContent("tbox"), "target" },
        { new StringContent("replace"), "strategy" },
        { new StringContent("ntriples"), "format" },
    });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Empty(store.Match(subjectIri: "urn:Old", graphIri: graphIri));
    Assert.Single(store.Match(subjectIri: "urn:New", graphIri: graphIri));
}
```

- [ ] **Step 2: Add active extraction guard test**

Add to `OntologyApiTests.cs`:

```csharp
[Fact]
public async Task Rdf_import_returns_conflict_when_extraction_is_active_for_same_ks()
{
    await using var app = new AuthTestWebApplicationFactory();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var ksId = await CreateKsAsync(client, "rdf-import-active-job");
    using (var db = app.CreateDbContext())
    {
        db.ExtractionJobs.Add(new ExtractionJobEntity
        {
            Id = Guid.NewGuid(),
            LegacyId = TestLegacyIds.Next("extractionjob"),
            KnowledgeSystemId = ksId,
            Kind = "all",
            Status = JobStatus.Running.ToWire(),
            Phase = "tbox",
            ProcessedChunks = 0,
            TotalChunks = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    var response = await client.PostAsync($"/api/knowledge/{ksId}/rdf/import", new MultipartFormDataContent
    {
        { new StringContent("<urn:Pump> a <http://www.w3.org/2002/07/owl#Class> ."), "file", "pump.nt" },
        { new StringContent("tbox"), "target" },
        { new StringContent("merge"), "strategy" },
        { new StringContent("ntriples"), "format" },
    });

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
}
```

Add required usings if missing:

```csharp
using OnToPilot.Extraction;
```

- [ ] **Step 3: Run tests and verify failures identify missing behavior**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import" --no-restore
```

Expected: FAIL if replace or conflict guard are incomplete.

- [ ] **Step 4: Fix workflow locally**

Ensure `ApplyLayerAsync(...)` clears only the requested touched graph when `strategy == "replace"`. Ensure `FindActiveJobAsync(ksId)` is checked before parsing or writing. Ensure thrown `GraphWriteConflictException` is handled by existing middleware as HTTP 409.

- [ ] **Step 5: Re-run tests**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import" --no-restore
```

Expected: PASS.

- [ ] **Step 6: Commit this task**

```powershell
git add src/OnToPilot/Ontology/RdfImportService.cs src/OnToPilot.Tests/Ontology/OntologyApiTests.cs
git commit -m "test: cover rdf import replace and active job guard"
```

---

### Task 7: Final Validation And Deployment

**Files:**
- Read/verify only unless tests expose a defect.

**Interfaces:**
- Consumes all previous tasks.
- Produces a deployed backend and verified runtime behavior.

- [ ] **Step 1: Run parser tests**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~RdfImportParserTests" --no-restore
```

Expected: all parser tests pass.

- [ ] **Step 2: Run RDF import HTTP tests**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests.Rdf_import" --no-restore
```

Expected: all RDF import HTTP tests pass.

- [ ] **Step 3: Run ontology and RDF round-trip regression suites**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OnToPilot.Tests.Ontology" --no-restore
```

Expected: all ontology tests pass.

- [ ] **Step 4: Run extraction API tests because RDF import shares graph locks and extraction guards**

```powershell
dotnet test .\src\OnToPilot.Tests\OnToPilot.Tests.csproj --filter "FullyQualifiedName~OnToPilot.Tests.Extraction.ExtractionRunApiTests" --no-restore
```

Expected: all extraction run API tests pass.

- [ ] **Step 5: Build the solution**

```powershell
dotnet build .\src\OnToPilot.sln --no-restore
```

Expected: build succeeds with exit code 0.

- [ ] **Step 6: Check diff hygiene**

```powershell
git diff --check
```

Expected: no whitespace errors. Existing LF-to-CRLF warnings are acceptable on this Windows checkout.

- [ ] **Step 7: Rebuild backend container**

```powershell
docker compose up -d --build backend
```

Expected: backend image builds and `ontopilot-backend-1` is recreated.

- [ ] **Step 8: Verify runtime health**

```powershell
docker compose ps backend
```

Expected: backend status includes `healthy`.

- [ ] **Step 9: Verify unauthenticated multipart reaches auth, not media-type rejection**

```powershell
curl.exe -s -o NUL -w "%{http_code}" -F "file=@README.md;filename=test.ttl;type=text/turtle" -F "target=auto" -F "strategy=merge" -F "format=turtle" "http://localhost:8080/api/knowledge/3abc2e3d-dffd-48cd-8798-3b0289f6c879/rdf/import"
```

Expected: `401`, not `415`.

- [ ] **Step 10: Commit final validation updates if any files changed**

```powershell
git status --short
git add src/OnToPilot docs/superpowers/plans/2026-08-21-rdf-import-complete.md
git commit -m "feat: complete rdf import workflow"
```

Only run the commit if the user asked this worker to commit. If not, leave the changes staged or unstaged according to the repo's current workflow.

---

## Self-Review

- Spec coverage: parser, multipart request, Python response shape, target/strategy/format validation, merge/replace, active extraction guard, stats, conflict sync, terminology, validation, audit, and deployment verification are each mapped to a task.
- Placeholder scan: no task uses `TBD`, `TODO`, or open-ended edge-case language without concrete commands or code.
- Type consistency: `RdfImportRequest`, `RdfImportParser`, `RdfImportService.ImportAsync`, `AuditLogService.RecordAsync`, and `ConflictService.SyncAfterOntologyMutationAsync` are introduced before downstream tasks consume them.
- Residual risk: JSON-LD parser availability in dotNetRDF must be compiler-verified during Task 2. If unavailable, the code must return a deterministic 400 for JSON-LD rather than silently accepting it.
