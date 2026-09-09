
# Remove Oxigraph Runtime Dependency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove Oxigraph and RocksDB from the `ISEStudio` production runtime while retaining `Oxigraph 0.5.8` only in `ISEStudio.Migration` as a one-time reader for legacy pyoxigraph/RocksDB data.

**Architecture:** `RdfStatement` and `RdfTerm` become the runtime RDF boundary. dotNetRDF parses and serializes RDF documents in memory, while PostgreSQL owns workspace and release state. Replace all Oxigraph-shaped service APIs and algorithms with statement operations, then enforce the dependency boundary with a PowerShell gate.

**Tech Stack:** .NET 10, ASP.NET Core 10, EF Core 10, Npgsql 10.0.3, PostgreSQL 16 Testcontainers, dotNetRDF 3.5.2, xUnit, PowerShell.

## Global Constraints

- `src/ISEStudio/ISEStudio.csproj` retains `dotNetRDF` `3.5.2` and has no `Oxigraph` or `Oxigraph.Extensions.DotNetRDF` reference.
- `src/ISEStudio.Migration/ISEStudio.Migration.csproj` retains `Oxigraph` `0.5.8` as the legacy reader.
- PostgreSQL is runtime-authoritative; runtime code creates no RocksDB handle, `data/rdf`, or `serving/{releaseId}` directory.
- Runtime API/repository types use `RdfStatement`, `RdfTerm`, strings, and DTOs, never `Oxigraph.Quad` or Oxigraph nodes.
- Import/export preserves graph IRIs, blank nodes per document, language tags, datatypes, escaped literals, merge/replace, audits, and release immutability.
- RDF parsing uses dotNetRDF rather than string splitting.
- Preserve existing migration direct-read and N-Quads fallback behavior; do not move it into `ISEStudio`.
- Do not alter unrelated dirty worktree changes. Do not commit; commit commands are implementation handoff only.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/ISEStudio/Ontology/RdfDotNetRdfCodec.cs` | dotNetRDF node/dataset conversion to `RdfTerm` and `RdfStatement`. |
| `src/ISEStudio/Ontology/RdfStatementSet.cs` | Pure statement matching, merge, deletion, graph replacement, and canonical diff. |
| `src/ISEStudio/Ontology/RdfImportParser.cs` | Direct statement parsing and TBox/ABox partition. |
| `src/ISEStudio/Ontology/PostgresRdfGraphStore.cs` | PostgreSQL facade using statements only. |
| `src/ISEStudio/Ontology/*.cs` | Statement-based ontology, ABox, SKOS, conflict, SHACL, history, and release behavior. |
| `scripts/verify-postgresql-authoritative-storage.ps1` | Dependency boundary gate. |

### Task 1: Add dotNetRDF Codec and Statement Operations

**Files:**
- Create: `src/ISEStudio/Ontology/RdfDotNetRdfCodec.cs`
- Create: `src/ISEStudio/Ontology/RdfStatementSet.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfDotNetRdfCodecTests.cs`

**Interfaces:**
- `public sealed record ParsedRdfDataset(IReadOnlyList<RdfStatement> Statements)`
- `public static ParsedRdfDataset ParseNQuads(byte[] bytes)`
- `public static byte[] SerializeNQuads(IEnumerable<RdfStatement> statements)`
- `public static IReadOnlyList<RdfStatement> ParseDocument(byte[] bytes, IRdfReader reader, string? baseIri, string blankNodeScope, string? graphIri)`

- [ ] **Step 1: Write the failing codec test**

```csharp
[Fact]
public void NQuads_round_trip_preserves_terms_and_graph()
{
    var input = new RdfStatement[]
    {
        new(new RdfBlankNode("b1"), "urn:p", new RdfLiteral("a\\b\"c\nd", "en"), "urn:g"),
        new(new RdfIri("urn:s"), "urn:count", new RdfLiteral("42", null,
            "http://www.w3.org/2001/XMLSchema#integer"), "urn:g"),
    };

    var actual = RdfDotNetRdfCodec.ParseNQuads(RdfDotNetRdfCodec.SerializeNQuads(input));
    Assert.Equal(input.ToHashSet(), actual.Statements.ToHashSet());
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~RdfDotNetRdfCodecTests --no-restore`

Expected: FAIL because `RdfDotNetRdfCodec` does not exist.

- [ ] **Step 3: Implement dotNetRDF conversion and dataset I/O**

```csharp
public static RdfTerm ToTerm(INode node, IDictionary<string, RdfBlankNode> blanks, string scope) => node switch
{
    IUriNode uri => new RdfIri(uri.Uri.AbsoluteUri),
    IBlankNode blank => GetBlank(blank.InternalID, blanks, scope),
    ILiteralNode literal => new RdfLiteral(literal.Value,
        string.IsNullOrEmpty(literal.Language) ? null : literal.Language,
        literal.DataType?.AbsoluteUri),
    _ => throw new RdfImportException($"Unsupported RDF node: {node.NodeType}"),
};
```

Use `TripleStore`, `NQuadsParser`, and `NQuadsWriter`; apply the caller's graph IRI to triple-only documents.

- [ ] **Step 4: Add pure set operations and tests**

```csharp
public static IReadOnlyList<RdfStatement> Merge(
    IEnumerable<RdfStatement> current, IEnumerable<RdfStatement> incoming) =>
    current.Concat(incoming).Distinct().ToList();

public static IReadOnlyList<RdfStatement> ReplaceGraph(
    IEnumerable<RdfStatement> current, string graphIri, IEnumerable<RdfStatement> replacement) =>
    current.Where(s => s.GraphIri != graphIri)
        .Concat(replacement.Select(s => s with { GraphIri = graphIri }))
        .Distinct().ToList();
```

- [ ] **Step 5: Run focused tests**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~RdfDotNetRdfCodecTests --no-restore`

Expected: PASS for graph, blank node, language, datatype, and escaping cases.

- [ ] **Step 6: Commit**

```powershell
git add src/ISEStudio/Ontology/RdfDotNetRdfCodec.cs src/ISEStudio/Ontology/RdfStatementSet.cs src/ISEStudio.Tests/Ontology/RdfDotNetRdfCodecTests.cs
git commit -m "feat: add dotnetrdf runtime codec"
```

### Task 2: Convert RDF Import, Export, History, and PostgreSQL Facade

**Files:**
- Modify: `src/ISEStudio/Ontology/RdfImportParser.cs`
- Modify: `src/ISEStudio/Ontology/RdfImportService.cs`
- Modify: `src/ISEStudio/Ontology/RdfExportService.cs`
- Modify: `src/ISEStudio/Ontology/PostgresRdfGraphStore.cs`
- Modify: `src/ISEStudio/Ontology/HistoryService.cs`
- Modify: `src/ISEStudio/Ontology/NQuadsTermWriter.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfImportParserTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfRoundTripTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/HistoryServiceTests.cs`

**Interfaces:**
- `ParsedRdfImport` and `RdfImportPartition` carry `IReadOnlyList<RdfStatement>`.
- `PostgresRdfGraphStore.Match` returns `List<RdfStatement>`; mutation methods accept graph IRI plus statements.
- `ConflictDetector.Signature` accepts statement lists and raw N-Quads bytes.

- [ ] **Step 1: Write failing statement-only parser and rollback tests**

```csharp
[Fact]
public void Parse_scopes_blank_nodes_as_runtime_terms()
{
    var parsed = _parser.Parse("_:b <urn:p> <urn:o> ."u8.ToArray(), "data.nt", "ntriples", null, 10, "scope");
    Assert.Equal("rdfimport_scope_0", Assert.IsType<RdfBlankNode>(
        Assert.Single(parsed.Statements).Subject).Id);
}
```

Replace RocksDB fixtures with `PostgresRdfFixture` and assert `RdfStatement` values after merge, replace, and rollback.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter "FullyQualifiedName~RdfImportParserTests|FullyQualifiedName~RdfRoundTripTests|FullyQualifiedName~HistoryServiceTests" --no-restore`

Expected: FAIL because current methods expose Oxigraph triples/quads.

- [ ] **Step 3: Parse and restore exclusively through the codec**

```csharp
var incoming = RdfDotNetRdfCodec.ParseNQuads(nQuads).Statements
    .Select(s => s with { GraphIri = graphIri }).ToList();
var next = mode == ImportMode.Replace ? incoming : RdfStatementSet.Merge(existing, incoming);
```

Apply this to `RdfImportService`, `HistoryService.ParseStatements`, `PostgresRdfGraphStore.Restore`, and the N-Quads overload of `ConflictDetector.Signature`.

- [ ] **Step 4: Convert the term writer and exporter to `RdfTerm`**

```csharp
internal static void Append(StringBuilder builder, RdfTerm term) => term switch
{
    RdfIri iri => builder.Append('<').Append(iri.Value).Append('>'),
    RdfBlankNode blank => builder.Append("_:").Append(blank.Id),
    RdfLiteral literal => AppendLiteral(builder, literal),
    _ => throw new InvalidOperationException("Unsupported RDF term."),
};
```

- [ ] **Step 5: Run focused tests**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter "FullyQualifiedName~RdfImportParserTests|FullyQualifiedName~RdfRoundTripTests|FullyQualifiedName~HistoryServiceTests|FullyQualifiedName~NQuadsTermWriterTests|FullyQualifiedName~ConflictDetectorTests" --no-restore`

Expected: PASS without `StoreWrapper` or in-memory Oxigraph stores.

- [ ] **Step 6: Commit**

```powershell
git add src/ISEStudio/Ontology/RdfImportParser.cs src/ISEStudio/Ontology/RdfImportService.cs src/ISEStudio/Ontology/RdfExportService.cs src/ISEStudio/Ontology/PostgresRdfGraphStore.cs src/ISEStudio/Ontology/HistoryService.cs src/ISEStudio/Ontology/NQuadsTermWriter.cs src/ISEStudio.Tests/Ontology/RdfImportParserTests.cs src/ISEStudio.Tests/Ontology/RdfRoundTripTests.cs src/ISEStudio.Tests/Ontology/HistoryServiceTests.cs
git commit -m "refactor: use rdf statements for runtime io"
```

### Task 3: Convert Ontology Domain Algorithms to Statements

**Files:**
- Modify: `src/ISEStudio/Ontology/Vocabulary.cs`
- Modify: `src/ISEStudio/Ontology/SchemaBuilder.cs`
- Modify: `src/ISEStudio/Ontology/ABoxManager.cs`
- Modify: `src/ISEStudio/Ontology/SkosManager.cs`
- Modify: `src/ISEStudio/Ontology/ShaclValidator.cs`
- Modify: `src/ISEStudio/Ontology/ConflictDetection.cs`
- Modify: `src/ISEStudio/Ontology/DuplicateJudge.cs`
- Modify: `src/ISEStudio/Ontology/OntologyViewBuilder.cs`
- Modify: `src/ISEStudio/Extraction/ExtractionMerger.cs`
- Modify: `src/ISEStudio/Extraction/TerminologyService.cs`
- Test: `src/ISEStudio.Tests/Ontology/SchemaBuilderTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/ABoxManagerTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/SkosManagerTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/ShaclValidatorTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/DuplicateJudgeTests.cs`

**Interfaces:**
- Vocabulary IRIs are strings.
- `SchemaBuilder.BuildMutation` returns `IReadOnlyList<RdfStatement>`.
- Conflict and SHACL readers accept statement lists.
- `DuplicateJudge.DetectAsync` accepts `IReadOnlyList<RdfStatement>`.

- [ ] **Step 1: Write a failing statement-only mutation test**

```csharp
[Fact]
public void BuildMutation_emits_runtime_statement_terms()
{
    var statements = SchemaBuilder.BuildMutation("urn:onto#", mutation, "urn:tbox");
    Assert.Contains(statements, s => s.Subject == new RdfIri("urn:onto#Pump")
        && s.PredicateIri == Vocabulary.RdfType
        && s.Object == new RdfIri(Vocabulary.OwlClass));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter "FullyQualifiedName~SchemaBuilderTests|FullyQualifiedName~ABoxManagerTests|FullyQualifiedName~SkosManagerTests|FullyQualifiedName~ShaclValidatorTests|FullyQualifiedName~ConflictDetectorTests|FullyQualifiedName~DuplicateJudgeTests" --no-restore`

Expected: FAIL at Oxigraph term/quad interfaces.

- [ ] **Step 3: Convert mutation construction and graph readers**

```csharp
new RdfStatement(new RdfIri(subjectIri), Vocabulary.RdfType,
    new RdfIri(Vocabulary.OwlNamedIndividual), ks.ABoxGraph)
```

Rewrite SHACL/Conflict matching as LINQ over `RdfStatement`; preserve all current named-graph isolation, idempotency, literal language/datatype comparisons, SHACL subset rules, and duplicate eligibility logic.

- [ ] **Step 4: Unify ontology view construction**

`BuildFromStatementsAsync` filters statements and invokes `BuildCore`. `BuildFromNQuadsAsync` parses with `RdfDotNetRdfCodec`, then invokes the same `BuildCore`; equal inputs must produce equal responses.

- [ ] **Step 5: Run ontology-core tests**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter "FullyQualifiedName~SchemaBuilderTests|FullyQualifiedName~ABoxManagerTests|FullyQualifiedName~SkosManagerTests|FullyQualifiedName~ShaclValidatorTests|FullyQualifiedName~ConflictDetectorTests|FullyQualifiedName~DuplicateJudgeTests|FullyQualifiedName~OntologyViewBuilderTests" --no-restore`

Expected: PASS with no Oxigraph import in these runtime files.

- [ ] **Step 6: Commit**

```powershell
git add src/ISEStudio/Ontology/Vocabulary.cs src/ISEStudio/Ontology/SchemaBuilder.cs src/ISEStudio/Ontology/ABoxManager.cs src/ISEStudio/Ontology/SkosManager.cs src/ISEStudio/Ontology/ShaclValidator.cs src/ISEStudio/Ontology/ConflictDetection.cs src/ISEStudio/Ontology/DuplicateJudge.cs src/ISEStudio/Ontology/OntologyViewBuilder.cs src/ISEStudio/Extraction/ExtractionMerger.cs src/ISEStudio/Extraction/TerminologyService.cs src/ISEStudio.Tests/Ontology
git commit -m "refactor: remove oxigraph terms from ontology runtime"
```

### Task 4: Remove Runtime RocksDB and Release-serving Paths

**Files:**
- Delete: `src/ISEStudio/Ontology/StoreWrapper.cs`
- Delete: `src/ISEStudio/Ontology/QuadChangeCapture.cs`
- Delete: `src/ISEStudio/Ontology/GraphWriteCoordinator.cs`
- Modify: `src/ISEStudio/Ontology/ReleaseManager.cs`
- Modify: `src/ISEStudio/Ontology/PublishedDataService.cs`
- Modify: `src/ISEStudio/Ontology/PublishedOntologyService.cs`
- Modify: `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs`
- Modify: `src/ISEStudio/Program.cs`
- Test: `src/ISEStudio.Tests/Ontology/ReleaseManagerTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/PublishedDataServiceTests.cs`

**Interfaces:**
- `ReleaseManager.ReadPublished` returns `IReadOnlyList<RdfStatement>` from immutable release rows.
- `ServingContext` contains release metadata plus scoped statements, never a serving-store path or handle.

- [ ] **Step 1: Write the failing immutable-release test**

```csharp
[Fact]
public async Task Published_release_reads_snapshot_rows_not_later_workspace_rows()
{
    var release = await releases.CaptureAsync(ks, Guid.NewGuid().ToString("N"), "v1", actor);
    await releases.PublishAsync(release.Id, actor);
    await statements.ReplaceLayerAsync(ks.KnowledgeSystemId, "TBox", laterWorkspaceStatements);
    Assert.DoesNotContain(releases.ReadPublished(release.Id, RdfLayer.TBox),
        s => s.Subject == new RdfIri("urn:later"));
}
```

- [ ] **Step 2: Run release tests to verify they fail**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter "FullyQualifiedName~ReleaseManagerTests|FullyQualifiedName~PublishedDataServiceTests|FullyQualifiedName~PublishedOntologyServiceTests" --no-restore`

Expected: FAIL while tests/services rely on artifact or serving-store behavior.

- [ ] **Step 3: Read releases from PostgreSQL statement snapshots**

```csharp
return _db.ReleaseStatements.AsNoTracking()
    .Where(row => row.ReleaseId == releaseGuid && row.Layer == layer.ToString())
    .OrderBy(row => row.Subject).ThenBy(row => row.Predicate).ThenBy(row => row.Object)
    .Select(ToStatement).ToList();
```

Retain status checks, deployment state, and snapshot immutability. Remove DI registrations and configuration paths that construct a store or serving directory.

- [ ] **Step 4: Delete obsolete classes and migrate their behavioral tests**

Delete `StoreWrapper`, `QuadChangeCapture`, `GraphWriteCoordinator`, and `StoreWrapperTests` only after all call sites compile. Preserve their useful coverage in codec, PostgreSQL, and release tests.

- [ ] **Step 5: Run release and startup tests**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter "FullyQualifiedName~ReleaseManagerTests|FullyQualifiedName~PublishedDataServiceTests|FullyQualifiedName~PublishedOntologyServiceTests|FullyQualifiedName~OntologyServiceTests" --no-restore`

Expected: PASS without creating an RDF/RocksDB directory.

- [ ] **Step 6: Commit**

```powershell
git add src/ISEStudio/Ontology/ReleaseManager.cs src/ISEStudio/Ontology/PublishedDataService.cs src/ISEStudio/Ontology/PublishedOntologyService.cs src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs src/ISEStudio/Program.cs src/ISEStudio.Tests/Ontology
git rm src/ISEStudio/Ontology/StoreWrapper.cs src/ISEStudio/Ontology/QuadChangeCapture.cs src/ISEStudio/Ontology/GraphWriteCoordinator.cs src/ISEStudio.Tests/Ontology/StoreWrapperTests.cs
git commit -m "refactor: remove runtime rocksdb stores"
```

### Task 5: Remove Packages and Add the Runtime Dependency Gate

**Files:**
- Modify: `src/ISEStudio/ISEStudio.csproj`
- Modify: `src/ISEStudio.Tests/ISEStudio.Tests.csproj`
- Modify: remaining runtime callers reported by an `Oxigraph` search, including `ConflictAgent.cs`, `ABoxService.cs`, `ExternalApiService.cs`, `InternalOperationDispatcher.cs`, `StructureAgent.cs`, and Dovetail inputs.
- Create: `scripts/verify-postgresql-authoritative-storage.ps1`
- Modify: `docs/architecture.md`
- Modify: `docs/migration/production-cutover-record.md`

- [ ] **Step 1: Write the failing dependency gate**

```powershell
$matches = Get-ChildItem src\ISEStudio -Recurse -File -Include *.cs,*.csproj |
    Select-String -Pattern 'Oxigraph|Oxigraph\.Extensions|RocksDB|StoreWrapper'
if ($matches) {
    $matches | ForEach-Object { Write-Error "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    exit 1
}
Write-Host 'Runtime RDF dependency boundary verified.'
```

Add a host smoke test using SQLite that verifies startup does not create `data/rdf`.

- [ ] **Step 2: Run the gate to verify it reports remaining runtime references**

Run: `pwsh -NoProfile -File scripts\verify-postgresql-authoritative-storage.ps1`

Expected: FAIL until every runtime source and package reference is removed.

- [ ] **Step 3: Convert remaining callers, then remove only runtime packages**

Replace each remaining runtime Oxigraph alias with statement/term pattern matching. Remove these two entries from `ISEStudio.csproj` and remove the direct test-project Oxigraph package after tests no longer use its types:

```xml
<PackageReference Include="Oxigraph" Version="0.5.8" />
<PackageReference Include="Oxigraph.Extensions.DotNetRDF" Version="0.5.8" />
```

Do not change `ISEStudio.Migration.csproj` or `ISEStudio.OxigraphProbe.csproj`.

- [ ] **Step 4: Document the explicit migration exception**

State that `ISEStudio.Migration` and `ISEStudio.OxigraphProbe` may reference Oxigraph because neither is hosted by `ISEStudio`; migration retains direct RocksDB validation and N-Quads fallback.

- [ ] **Step 5: Restore, build, gate, and test**

Run: `dotnet restore src\ISEStudio.sln; dotnet build src\ISEStudio\ISEStudio.csproj --no-restore -warnaserror; dotnet build src\ISEStudio.Migration\ISEStudio.Migration.csproj --no-restore -warnaserror; pwsh -NoProfile -File scripts\verify-postgresql-authoritative-storage.ps1; dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --no-restore`

Expected: all commands exit `0`; the host resolves dotNetRDF but not Oxigraph, the migration project still builds against Oxigraph, and the test project continues running its migration-reader coverage through its project reference.

- [ ] **Step 6: Commit**

```powershell
git add src/ISEStudio src/ISEStudio.Tests scripts/verify-postgresql-authoritative-storage.ps1 docs/architecture.md docs/migration/production-cutover-record.md
git commit -m "chore: remove oxigraph from runtime"
```

## Self-Review

- Coverage: codec and RDF I/O are handled in Tasks 1-2; ontology algorithms in Task 3; release and RocksDB removal in Task 4; dependency removal and enforcement in Task 5.
- Scope: legacy direct RocksDB read stays isolated to migration and is verified by the final migration test command.
- Type consistency: every runtime task converges on `RdfStatement` and `RdfTerm`.
