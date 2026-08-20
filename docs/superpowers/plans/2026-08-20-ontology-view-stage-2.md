# Stage 2 — Ontology view real read Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace 4 placeholder dispatcher arms (`ontology.get`, `external.ontology`, `published.ontology`, `published.release.ontology`) with real implementations of the Python `build_view()` contract by extracting a stateless `OntologyViewBuilder` service that both the live-Oxigraph path and the release-shard path share.

**Architecture:**
- One pure algorithm `BuildCore(quads)` mirrors `backend/app/ontology/schema.py:241-371` line-for-line.
- Two adapters feed the algorithm: `BuildFromStoreAsync(StoreWrapper, graphIri)` for live Oxigraph and `BuildFromNQuadsAsync(byte[] tboxShard)` for release shards (no Oxigraph dependency).
- Three services own the source: `OntologyService.GetViewAsync` (live, Guid-keyed), `ExternalOntologyService.GetViewAsync` (live, public-id-keyed), `PublishedOntologyService.GetViewAsync` (release shards).
- Wire DTOs mirror Python's field shape exactly. Axiom pairs are 3 small records (`SubclassAxiom`, `PairAxiom`), not tuples — System.Text.Json source-gen does NOT apply `SnakeCaseLower` to tuple `Item1` / `Item2`.

**Tech Stack:**
- .NET 10 / ASP.NET Core 10 / EF Core 10 / Npgsql / SQLite (tests) / xUnit 2.9.3
- Oxigraph 0.5.8 (live path) + N-Quads parser (release path)
- `WebApplicationFactory<Program>` + `AuthTestWebApplicationFactory`
- System.Text.Json source-gen + `PropertyNamingPolicy = SnakeCaseLower`

**Spec:** `docs/superpowers/specs/2026-08-20-ontology-view-stage-2-design.md`

## Global Constraints

These constraints apply to **every** task in this plan. Tasks reference them rather than repeat them.

| 约束 | 详情 |
|---|---|
| Arm count | 4 placeholder arms replaced: `ontology.get` (line 96) + `external.ontology` (287) + `published.ontology` (301) + `published.release.ontology` (313) |
| Service lifetimes | `OntologyViewBuilder` = **Singleton** (stateless). `OntologyService.GetViewAsync` / `ExternalOntologyService` / `PublishedOntologyService` = **Scoped** (share the request DbContext) |
| Algorithm | `BuildCore(quads)` mirrors Python `backend/app/ontology/schema.py:241-371` line-for-line — same field ordering, same label fallback, same sort by label |
| Axiom types | Use records (`SubclassAxiom(string Sub, string Super)`, `PairAxiom(string A, string B)`), NOT tuples. Tuples serialize as `{Item1, Item2}` which SnakeCaseLower does NOT rename |
| Wire DTO | `PropertyNamingPolicy = SnakeCaseLower` (already configured in `Program.cs:252`). PascalCase record fields → snake_case JSON. Matches Python `build_view()` exactly |
| Meta types | `KnowledgeSystemMeta(Guid Id, string Name, string BaseIri, string? Release)` for internal/published. `ExternalKnowledgeSystemMeta(string PublicId, string Name, string BaseIri)` for external (mirrors Python's public_id string in `view["knowledge_system"]`) |
| Access | Internal arm: `_access.GetEffectiveRoleAsync(user, ks, _db, ct) >= KSRole.Viewer`. External/published arms: scope check (`ontology:read`) lives in the controller layer (`ExternalApiController` / `PublishedController`), service does KS resolution + view build |
| Null store path | Contract-test factory registers `StoreWrapper? = null` (Program.cs:438). Both adapters must return the empty envelope in this case (existing behavior preserved) |
| 404 mapping | `OntologyService.GetViewAsync` throws `KeyNotFoundException` for not-found KS → `FastApiErrorMiddleware` maps to 404 envelope. Existing `Ok(payload ?? new {ok: true})` does NOT map to 404 |
| Pre-existing failures | 13/160 contract tests fail today (pre-existing, NOT related to this work). Plan MUST keep that number at 13/160 (do not regress). Final task runs full sweep + documents delta |
| Build | `dotnet build -c Release` 0 warning 0 error |
| Backend regression | Plan adds ~10 new tests; expected final ~360-365 / 365 unit + 13/160 contract (no improvement, no regression). The 13/160 failures stay; the 1 unit flake from B6b (`ExtractionStateTests.StartTboxAsync` etc.) stays |
| Reuse | `KnowledgeSystemAccessService.GetEffectiveRoleAsync` (B7c) · `AuthTestWebApplicationFactory` (B6b) · `SeedAdminAndClientAsync` / `CreateKsAsync` helpers (inlined per file) · `FastApiErrorMiddleware` envelope translation · `FastApiError` DTO |

---
### Task 1: New wire DTOs + JsonContext registration

**Files:**
- Modify: `src/OnToPilot.Application/Foundation/OntologyResponse.cs` (full rewrite — currently 9-line stub)
- Modify: `src/OnToPilot/Serialization/OnToPilotJsonContext.cs`

**Interfaces:**
- Consumes: existing `OntologyClass` / `OntologyProperty` records (will be extended via `init` properties)
- Produces: full record tree matching Python `build_view()` wire shape; source-gen serializer covers every nested type

- [ ] **Step 1: Rewrite `OntologyResponse.cs` with full record tree**

Replace the file contents with:

```csharp
namespace OnToPilot.Application.Foundation;

/// <summary>
/// Curated JSON view returned by
/// <see cref="Integration.IIntegrationApiFacade.GetOntologyAsync"/>. The
/// field shape mirrors the Python <c>backend/app/ontology/schema.py::build_view</c>
/// contract line-for-line so the FastAPI frontend consumes the same
/// payload regardless of which backend served it.
/// </summary>
public sealed record OntologyResponse(
    IReadOnlyList<OntologyClass> Classes,
    IReadOnlyList<OntologyProperty> ObjectProperties,
    IReadOnlyList<OntologyProperty> DataProperties,
    OntologyAxioms Axioms,
    IReadOnlyDictionary<string, string> Labels,
    OntologyStats Stats,
    KnowledgeSystemMeta? KnowledgeSystem);

public sealed record OntologyAxioms(
    IReadOnlyList<SubclassAxiom> SubclassOf,
    IReadOnlyList<PairAxiom> DisjointWith,
    IReadOnlyList<PairAxiom> EquivalentClass);

public sealed record SubclassAxiom(string Sub, string Super);

public sealed record PairAxiom(string A, string B);

public sealed record OntologyStats(
    int ClassCount,
    int PropertyCount,
    int AxiomCount);

public sealed record KnowledgeSystemMeta(
    Guid Id,
    string Name,
    string BaseIri,
    string? Release);

public sealed record ExternalKnowledgeSystemMeta(
    string PublicId,
    string Name,
    string BaseIri);

public sealed record OntologyClass(string Iri, string? Label)
{
    public string Local { get; init; } = "";
    public string Comment { get; init; } = "";
    public IReadOnlyList<string> Superclasses { get; init; } = Array.Empty<string>();
}

public sealed record OntologyProperty(string Iri, string? Label)
{
    public string Local { get; init; } = "";
    public string Comment { get; init; } = "";
    public string? Domain { get; init; }
    public string? DomainLabel { get; init; }
    public string? Range { get; init; }
    public string? RangeLabel { get; init; }
    public IReadOnlyList<string> DomainMembers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RangeMembers { get; init; } = Array.Empty<string>();
}
```

- [ ] **Step 2: Register the new types in `OnToPilotJsonContext.cs`**

Replace the `[JsonSerializable(...)]` lines in `src/OnToPilot/Serialization/OnToPilotJsonContext.cs` with:

```csharp
[JsonSerializable(typeof(FastApiError))]
[JsonSerializable(typeof(OntologyResponse))]
[JsonSerializable(typeof(OntologyAxioms))]
[JsonSerializable(typeof(SubclassAxiom))]
[JsonSerializable(typeof(PairAxiom))]
[JsonSerializable(typeof(OntologyStats))]
[JsonSerializable(typeof(KnowledgeSystemMeta))]
[JsonSerializable(typeof(ExternalKnowledgeSystemMeta))]
[JsonSerializable(typeof(IReadOnlyList<OntologyClass>))]
[JsonSerializable(typeof(IReadOnlyList<OntologyProperty>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(ChangePreview))]
[JsonSerializable(typeof(QueryResponse))]
```

- [ ] **Step 3: Build and confirm zero errors**

Run: `cd "e:/GitHub/ontopilot" && dotnet build src/OnToPilot/OnToPilot.csproj -c Release 2>&1 | tail -20`
Expected: 0 errors. (Warnings acceptable from existing sources — none added by this task.)

- [ ] **Step 4: Run existing ontology-related tests — must stay green**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~Ontology" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: same pass count as before this task (no regressions on the stub-shape contract).

- [ ] **Step 5: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot.Application/Foundation/OntologyResponse.cs src/OnToPilot/Serialization/OnToPilotJsonContext.cs
git commit -m "feat(ontology): full wire DTOs for Stage 2 view read

Rewrite OntologyResponse record + add 4 nested records (OntologyAxioms /
SubclassAxiom / PairAxiom / OntologyStats) + 2 meta records
(KnowledgeSystemMeta for internal/published, ExternalKnowledgeSystemMeta
with public_id for external). Extend OntologyClass / OntologyProperty
with init-only fields (Local / Comment / Superclasses / Domain / Range /
DomainMembers / RangeMembers) populated by OntologyViewBuilder in
subsequent tasks. Register every nested type in OnToPilotJsonContext so
the source-gen serializer covers the full envelope shape — currently
the axioms / stats / meta arms take the reflection-fallback path.

Wire shape matches Python build_view() output. SnakeCaseLower naming
policy (already in Program.cs:252) gives PascalCase record fields →
snake_case JSON. Build passes; existing ontology tests stay green."
```

---

### Task 2: OntologyViewBuilder — empty-graph path

**Files:**
- Create: `src/OnToPilot/Ontology/OntologyViewBuilder.cs`
- Create: `src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs`

**Interfaces:**
- Consumes: `OntologyResponse` / `OntologyAxioms` / `OntologyStats` records (from Task 1)
- Consumes: existing `StoreWrapper?` (registered as `null` in non-Dev/Prod envs per `Program.cs:438`)
- Produces: `Task<OntologyResponse>` — both adapters return empty envelope when input is empty / null

- [ ] **Step 1: Write failing test — empty store**

Create `src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs`:

```csharp
using OnToPilot.Application.Foundation;
using OnToPilot.Ontology;
using Xunit;

namespace OnToPilot.Tests.Ontology;

public sealed class OntologyViewBuilderTests
{
    [Fact]
    public async Task BuildFromStoreAsync_with_null_store_returns_empty_envelope()
    {
        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromStoreAsync(
            store: null, graphIri: "http://x/graph", CancellationToken.None);

        Assert.NotNull(view);
        Assert.Empty(view.Classes);
        Assert.Empty(view.ObjectProperties);
        Assert.Empty(view.DataProperties);
        Assert.Empty(view.Axioms.SubclassOf);
        Assert.Empty(view.Axioms.DisjointWith);
        Assert.Empty(view.Axioms.EquivalentClass);
        Assert.Empty(view.Labels);
        Assert.Equal(0, view.Stats.ClassCount);
        Assert.Equal(0, view.Stats.PropertyCount);
        Assert.Equal(0, view.Stats.AxiomCount);
        Assert.Null(view.KnowledgeSystem);
    }

    [Fact]
    public async Task BuildFromNQuadsAsync_with_empty_bytes_returns_empty_envelope()
    {
        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromNQuadsAsync(
            tboxShard: Array.Empty<byte>(), CancellationToken.None);

        Assert.NotNull(view);
        Assert.Empty(view.Classes);
        Assert.Equal(0, view.Stats.ClassCount);
    }
}
```

- [ ] **Step 2: Run tests — they fail to compile (class doesn't exist)**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests" --logger "console;verbosity=normal" 2>&1 | tail -20`
Expected: build failure — `OntologyViewBuilder` does not exist.

- [ ] **Step 3: Create `OntologyViewBuilder.cs` with the empty path**

Create `src/OnToPilot/Ontology/OntologyViewBuilder.cs`:

```csharp
using OnToPilot.Application.Foundation;

namespace OnToPilot.Ontology;

/// <summary>
/// Reads the curated TBox view out of an RDF store (live) or a
/// pre-serialized N-Quads shard (release). One pure algorithm
/// (<see cref="BuildCore"/>) feeds both adapters so the wire shape
/// matches Python `backend/app/ontology/schema.py::build_view`
/// identically for live and release endpoints.
/// </summary>
public sealed class OntologyViewBuilder
{
    /// <summary>Live TBox read via Oxigraph. Returns empty envelope when
    /// <paramref name="store"/> is null (contract-test path).</summary>
    public Task<OntologyResponse> BuildFromStoreAsync(
        StoreWrapper? store,
        string graphIri,
        CancellationToken cancellationToken)
    {
        if (store is null) return Task.FromResult(EmptyResponse());

        // Live algorithm lands in Task 3-5. This task only wires the
        // empty contract.
        var quads = store.Match(graphIri: graphIri);
        return Task.FromResult(BuildCore(quads));
    }

    /// <summary>Release TBox read from a pre-serialized N-Quads shard
    /// (no Oxigraph dependency). Used by published.ontology.</summary>
    public Task<OntologyResponse> BuildFromNQuadsAsync(
        byte[] tboxShard,
        CancellationToken cancellationToken)
    {
        var quads = ParseNQuads(tboxShard);
        return Task.FromResult(BuildCore(quads));
    }

    private static OntologyResponse EmptyResponse() => new(
        Classes: Array.Empty<OntologyClass>(),
        ObjectProperties: Array.Empty<OntologyProperty>(),
        DataProperties: Array.Empty<OntologyProperty>(),
        Axioms: new OntologyAxioms(
            SubclassOf: Array.Empty<SubclassAxiom>(),
            DisjointWith: Array.Empty<PairAxiom>(),
            EquivalentClass: Array.Empty<PairAxiom>()),
        Labels: new Dictionary<string, string>(),
        Stats: new OntologyStats(0, 0, 0),
        KnowledgeSystem: null);

    // BuildCore + ParseNQuads implemented in Tasks 3-5.

    private static OntologyResponse BuildCore(
        IEnumerable<Oxigraph.Quad> quads)
    {
        // Empty-graph path: no triples → empty envelope. Tasks 3-5
        // extend this with the full Python build_view algorithm.
        using var iter = quads.GetEnumerator();
        if (!iter.MoveNext()) return EmptyResponse();
        // Single triple or more: defer to Task 5 which fully populates.
        _ = iter;
        return EmptyResponse();
    }

    private static IEnumerable<Oxigraph.Quad> ParseNQuads(byte[] shard)
    {
        return Array.Empty<Oxigraph.Quad>();
    }
}
```

- [ ] **Step 4: Run tests — they pass**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 2/2 passing.

- [ ] **Step 5: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/OntologyViewBuilder.cs src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs
git commit -m "feat(ontology): OntologyViewBuilder with empty-graph path

Extract the Python build_view algorithm into a stateless builder
service. Two adapters (BuildFromStoreAsync for live Oxigraph,
BuildFromNQuadsAsync for release shards) feed one pure BuildCore
algorithm. Tasks 3-5 extend the algorithm; this task only wires
the empty-graph contract and proves the null-store / empty-shard
paths return the full empty envelope with all fields. Foundation
for Tasks 3-5 + service calls."
```

---
### Task 3: BuildCore — class extraction (label / comment / superclasses)

**Files:**
- Modify: `src/OnToPilot/Ontology/OntologyViewBuilder.cs` (`BuildCore` body)
- Modify: `src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs` (add tests)

**Interfaces:**
- Consumes: `Oxigraph.Quad` stream from `StoreWrapper.Match` or `ParseNQuads`
- Produces: `OntologyResponse.Classes` populated from `rdf:type owl:Class` subjects, with `rdfs:label` / `rdfs:comment` / `rdfs:subClassOf` triples extracted

- [ ] **Step 1: Add failing tests for single-class extraction**

Append to `OntologyViewBuilderTests.cs`:

```csharp
[Fact]
public async Task BuildFromStoreAsync_extracts_single_class_with_label_and_comment()
{
    using var dir = TempDir();
    await using var store = new StoreWrapper(dir.Path);
    store.LoadTurtle(
        """
        @prefix owl: <http://www.w3.org/2002/07/owl#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        <urn:Animal> a owl:Class ; rdfs:label "Animal" ; rdfs:comment "A living thing." .
        """,
        new Oxigraph.NamedNode("http://example.com/graph"));

    var builder = new OntologyViewBuilder();
    var view = await builder.BuildFromStoreAsync(
        store, "http://example.com/graph", CancellationToken.None);

    Assert.Single(view.Classes);
    var c = view.Classes[0];
    Assert.Equal("urn:Animal", c.Iri);
    Assert.Equal("Animal", c.Label);
    Assert.Equal("Animal", c.Local);
    Assert.Equal("A living thing.", c.Comment);
    Assert.Empty(c.Superclasses);
}

[Fact]
public async Task BuildFromStoreAsync_extracts_superclasses_via_subClassOf()
{
    using var dir = TempDir();
    await using var store = new StoreWrapper(dir.Path);
    store.LoadTurtle(
        """
        @prefix owl: <http://www.w3.org/2002/07/owl#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        <urn:Animal> a owl:Class ; rdfs:label "Animal" .
        <urn:Dog> a owl:Class ; rdfs:label "Dog" ; rdfs:subClassOf <urn:Animal> .
        """,
        new Oxigraph.NamedNode("http://example.com/graph"));

    var builder = new OntologyViewBuilder();
    var view = await builder.BuildFromStoreAsync(
        store, "http://example.com/graph", CancellationToken.None);

    Assert.Equal(2, view.Classes.Count);
    var dog = view.Classes.Single(c => c.Local == "Dog");
    Assert.Equal(new[] { "urn:Animal" }, dog.Superclasses);
}

private sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "ontopilot-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => System.IO.Directory.CreateDirectory(Path);
    public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
}
```

- [ ] **Step 2: Run tests — they fail (BuildCore still returns empty envelope)**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests.Single_class|FullyQualifiedName~OntologyViewBuilderTests.Superclasses" --logger "console;verbosity=normal" 2>&1 | tail -25`
Expected: 2 new tests FAIL with empty-Classes assertion.

- [ ] **Step 3: Implement class extraction in BuildCore**

Replace `BuildCore` in `src/OnToPilot/Ontology/OntologyViewBuilder.cs` with:

```csharp
private static OntologyResponse BuildCore(
    IEnumerable<Oxigraph.Quad> quads)
{
    // Mirrors Python backend/app/ontology/schema.py::build_view (lines 241-371).
    // V1: classes + superclasses. Tasks 4-5 add properties / axioms / labels / stats.

    var classes = new Dictionary<string, OntologyClass>(StringComparer.Ordinal);
    var labels = new Dictionary<string, string>(StringComparer.Ordinal);
    var comments = new Dictionary<string, string>(StringComparer.Ordinal);
    var subclassOf = new List<SubclassAxiom>();

    const string OwlClass = "http://www.w3.org/2002/07/owl#Class";
    const string RdfsLabel = "http://www.w3.org/2000/01/rdf-schema#label";
    const string RdfsComment = "http://www.w3.org/2000/01/rdf-schema#comment";
    const string RdfsSubClassOf = "http://www.w3.org/2000/01/rdf-schema#subClassOf";
    const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

    foreach (var q in quads)
    {
        if (q.Subject is not Oxigraph.NamedNode s) continue;
        if (q.Predicate is not Oxigraph.NamedNode p) continue;
        var siri = s.Value;
        var piri = p.Value;

        if (piri == RdfType
            && q.Object is Oxigraph.NamedNode o
            && o.Value == OwlClass)
        {
            classes.TryAdd(siri, new OntologyClass(siri, Label: null));
        }
        else if (piri == RdfsLabel && q.Object is Oxigraph.Literal lit)
        {
            labels[siri] = lit.Value;
        }
        else if (piri == RdfsComment && q.Object is Oxigraph.Literal lit2)
        {
            comments[siri] = lit2.Value;
        }
        else if (piri == RdfsSubClassOf && q.Object is Oxigraph.NamedNode sup)
        {
            subclassOf.Add(new SubclassAxiom(siri, sup.Value));
        }
    }

    var superBySub = subclassOf
        .GroupBy(a => a.Sub, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(a => a.Super).ToList(),
            StringComparer.Ordinal);

    var classList = classes.Keys
        .OrderBy(iri => labels.TryGetValue(iri, out var l) ? l : Local(iri),
            StringComparer.Ordinal)
        .Select(iri =>
        {
            var c = classes[iri];
            return c with
            {
                Local = Local(iri),
                Label = labels.TryGetValue(iri, out var l) ? l : null,
                Comment = comments.TryGetValue(iri, out var cm) ? cm : "",
                Superclasses = superBySub.TryGetValue(iri, out var s) ? s : Array.Empty<string>(),
            };
        })
        .ToList();

    return new OntologyResponse(
        Classes: classList,
        ObjectProperties: Array.Empty<OntologyProperty>(),
        DataProperties: Array.Empty<OntologyProperty>(),
        Axioms: new OntologyAxioms(
            SubclassOf: subclassOf,
            DisjointWith: Array.Empty<PairAxiom>(),
            EquivalentClass: Array.Empty<PairAxiom>()),
        Labels: labels,
        Stats: new OntologyStats(classList.Count, 0, subclassOf.Count),
        KnowledgeSystem: null);
}

private static string Local(string iri)
{
    if (iri.Contains('#')) return iri[(iri.LastIndexOf('#') + 1)..];
    return iri.TrimEnd('/').Split('/').Last();
}
```

- [ ] **Step 4: Run tests — they pass**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 4/4 passing (2 from Task 2 + 2 new).

- [ ] **Step 5: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/OntologyViewBuilder.cs src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs
git commit -m "feat(ontology): BuildCore extracts classes + superclasses

BuildCore now reads rdf:type owl:Class / rdfs:label / rdfs:comment /
rdfs:subClassOf triples from the quad stream and populates
OntologyResponse Classes + subclassOf list. Class sort order is by
label (or local-name fallback), matching Python schema.py:333.
superclasses list is grouped by sub class. Stats class_count +
axiom_count are populated. Tasks 4-5 add property / axiom / labels
/ stats remainder."
```

---

### Task 4: BuildCore — property extraction (object vs data, domain/range)

**Files:**
- Modify: `src/OnToPilot/Ontology/OntologyViewBuilder.cs` (`BuildCore` body)
- Modify: `src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs` (add tests)

**Interfaces:**
- Consumes: Task 3's class extraction (no overlap)
- Produces: `OntologyResponse.ObjectProperties` + `OntologyResponse.DataProperties` split by `rdf:type owl:ObjectProperty` vs `owl:DatatypeProperty`, with `rdfs:domain` / `rdfs:range` / `domain_label` / `range_label` populated

- [ ] **Step 1: Add failing test for property split + domain/range**

Append to test file:

```csharp
[Fact]
public async Task BuildFromStoreAsync_splits_object_vs_data_properties()
{
    using var dir = TempDir();
    await using var store = new StoreWrapper(dir.Path);
    store.LoadTurtle(
        """
        @prefix owl: <http://www.w3.org/2002/07/owl#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
        <urn:Pet> a owl:Class ; rdfs:label "Pet" .
        <urn:hasOwner> a owl:ObjectProperty ; rdfs:label "has owner" ;
                       rdfs:domain <urn:Pet> ; rdfs:range <urn:Pet> .
        <urn:age> a owl:DatatypeProperty ; rdfs:label "age" ;
                   rdfs:domain <urn:Pet> ; rdfs:range xsd:integer .
        """,
        new Oxigraph.NamedNode("http://example.com/graph"));

    var builder = new OntologyViewBuilder();
    var view = await builder.BuildFromStoreAsync(
        store, "http://example.com/graph", CancellationToken.None);

    Assert.Single(view.ObjectProperties);
    Assert.Single(view.DataProperties);

    var obj = view.ObjectProperties[0];
    Assert.Equal("hasOwner", obj.Local);
    Assert.Equal("urn:Pet", obj.Domain);
    Assert.Equal("Pet", obj.DomainLabel);
    Assert.Equal("urn:Pet", obj.Range);
    Assert.Equal("Pet", obj.RangeLabel);

    var dat = view.DataProperties[0];
    Assert.Equal("age", dat.Local);
    Assert.Equal("urn:Pet", dat.Domain);
    Assert.Equal("xsd:integer", dat.Range);
    Assert.Equal("xsd:integer", dat.RangeLabel);
    Assert.Equal(1, view.Stats.PropertyCount);
}
```

- [ ] **Step 2: Run test — fails (ObjectProperties + DataProperties empty)**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests.Splits_object_vs_data" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 1 test FAIL.

- [ ] **Step 3: Extend BuildCore to extract properties**

Replace `BuildCore` in `OntologyViewBuilder.cs` with the full Python algorithm. Keep the class extraction from Task 3; add property extraction after the existing class loop:

```csharp
// Inside BuildCore, after the labels/comments/subClassOf loop,
// before the classList projection:

var objectProps = new Dictionary<string, OntologyProperty>(StringComparer.Ordinal);
var dataProps = new Dictionary<string, OntologyProperty>(StringComparer.Ordinal);
var domains = new Dictionary<string, string>(StringComparer.Ordinal);
var ranges = new Dictionary<string, string>(StringComparer.Ordinal);

const string OwlObjectProperty = "http://www.w3.org/2002/07/owl#ObjectProperty";
const string OwlDatatypeProperty = "http://www.w3.org/2002/07/owl#DatatypeProperty";
const string RdfsDomain = "http://www.w3.org/2000/01/rdf-schema#domain";
const string RdfsRange = "http://www.w3.org/2000/01/rdf-schema#range";

// Modify the rdf:type branch inside the foreach loop:

if (piri == RdfType && q.Object is Oxigraph.NamedNode oType)
{
    if (oType.Value == OwlObjectProperty)
        objectProps.TryAdd(siri, new OntologyProperty(siri, Label: null));
    else if (oType.Value == OwlDatatypeProperty)
        dataProps.TryAdd(siri, new OntologyProperty(siri, Label: null));
    else if (oType.Value == OwlClass)
        classes.TryAdd(siri, new OntologyClass(siri, Label: null));
}
else if (piri == RdfsDomain && q.Object is Oxigraph.NamedNode d)
{
    domains[siri] = d.Value;
}
else if (piri == RdfsRange && q.Object is Oxigraph.NamedNode r)
{
    ranges[siri] = r.Value;
}
// ... then continue with the existing label / comment / subClassOf branches ...

OntologyProperty Prop(string iri, OntologyProperty seed) => seed with
{
    Local = Local(iri),
    Label = labels.TryGetValue(iri, out var l) ? l : null,
    Comment = comments.TryGetValue(iri, out var c) ? c : "",
    Domain = domains.TryGetValue(iri, out var d) ? d : null,
    DomainLabel = domains.TryGetValue(iri, out var dn) && labels.TryGetValue(dn, out var dl) ? dl : null,
    Range = ranges.TryGetValue(iri, out var rn) ? rn : null,
    RangeLabel = ranges.TryGetValue(iri, out var rn2) && labels.TryGetValue(rn2, out var rl) ? rl : null,
};

var objList = objectProps.Keys
    .OrderBy(iri => labels.TryGetValue(iri, out var l) ? l : Local(iri), StringComparer.Ordinal)
    .Select(iri => Prop(iri, objectProps[iri]))
    .ToList();

var datList = dataProps.Keys
    .OrderBy(iri => labels.TryGetValue(iri, out var l) ? l : Local(iri), StringComparer.Ordinal)
    .Select(iri => Prop(iri, dataProps[iri]))
    .ToList();

// ... existing classList projection ...

return new OntologyResponse(
    Classes: classList,
    ObjectProperties: objList,
    DataProperties: datList,
    Axioms: new OntologyAxioms(
        SubclassOf: subclassOf,
        DisjointWith: Array.Empty<PairAxiom>(),
        EquivalentClass: Array.Empty<PairAxiom>()),
    Labels: labels,
    Stats: new OntologyStats(classList.Count, objList.Count + datList.Count, subclassOf.Count),
    KnowledgeSystem: null);
```

- [ ] **Step 4: Run all builder tests — they pass**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 5/5 passing.

- [ ] **Step 5: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/OntologyViewBuilder.cs src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs
git commit -m "feat(ontology): BuildCore extracts object/data properties + domain/range

Extend BuildCore with the property extraction branch from Python
schema.py:267-282 (owl:ObjectProperty vs owl:DatatypeProperty split +
rdfs:domain / rdfs:range with their labels). Stats.property_count now
includes both kinds. classList stays sorted by label / local; obj/dat
lists use the same sort. Domain/Range labels fall back to null when
the target IRI has no rdfs:label."
```

---

### Task 5: BuildCore — axioms (disjointWith / equivalentClass) + ParseNQuads

**Files:**
- Modify: `src/OnToPilot/Ontology/OntologyViewBuilder.cs` (`BuildCore` body + `ParseNQuads`)
- Modify: `src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs` (add tests)

**Interfaces:**
- Consumes: Tasks 3 + 4 outputs (no overlap)
- Produces: `Axioms.DisjointWith` + `Axioms.EquivalentClass` populated from `owl:disjointWith` / `owl:equivalentClass` triples
- Produces: `ParseNQuads` reads canonical N-Quads grammar — subject predicate object graphOrDefault `.`

- [ ] **Step 1: Add failing tests for disjoint + equivalent axioms**

```csharp
[Fact]
public async Task BuildFromStoreAsync_extracts_disjointWith_and_equivalentClass_axioms()
{
    using var dir = TempDir();
    await using var store = new StoreWrapper(dir.Path);
    store.LoadTurtle(
        """
        @prefix owl: <http://www.w3.org/2002/07/owl#> .
        <urn:Cat> a owl:Class .
        <urn:Dog> a owl:Class .
        <urn:Mammal> a owl:Class .
        <urn:Cat> owl:disjointWith <urn:Dog> .
        <urn:Mammal> owl:equivalentClass <urn:Cat> .
        """,
        new Oxigraph.NamedNode("http://example.com/graph"));

    var builder = new OntologyViewBuilder();
    var view = await builder.BuildFromStoreAsync(
        store, "http://example.com/graph", CancellationToken.None);

    Assert.Single(view.Axioms.DisjointWith);
    Assert.Equal("urn:Cat", view.Axioms.DisjointWith[0].A);
    Assert.Equal("urn:Dog", view.Axioms.DisjointWith[0].B);

    Assert.Single(view.Axioms.EquivalentClass);
    Assert.Equal("urn:Mammal", view.Axioms.EquivalentClass[0].A);
    Assert.Equal("urn:Cat", view.Axioms.EquivalentClass[0].B);

    Assert.Equal(2, view.Stats.AxiomCount);  // 0 subClassOf + 1 disjoint + 1 equiv
}

[Fact]
public async Task BuildFromNQuadsAsync_matches_BuildFromStoreAsync_for_same_graph()
{
    using var dir = TempDir();
    await using var store = new StoreWrapper(dir.Path);
    const string graphIri = "http://example.com/graph";
    store.LoadTurtle(
        """
        @prefix owl: <http://www.w3.org/2002/07/owl#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        <urn:Animal> a owl:Class ; rdfs:label "Animal" .
        <urn:Dog> a owl:Class ; rdfs:label "Dog" ; rdfs:subClassOf <urn:Animal> .
        """,
        new Oxigraph.NamedNode(graphIri));

    var shard = store.DumpNQuads(new Oxigraph.NamedNode(graphIri));

    var builder = new OntologyViewBuilder();
    var fromStore = await builder.BuildFromStoreAsync(store, graphIri, CancellationToken.None);
    var fromShard = await builder.BuildFromNQuadsAsync(shard, CancellationToken.None);

    Assert.Equal(fromStore.Classes.Count, fromShard.Classes.Count);
    Assert.Equal(fromStore.Stats, fromShard.Stats);
    Assert.Equal(
        fromStore.Axioms.SubclassOf.Select(a => (a.Sub, a.Super)),
        fromShard.Axioms.SubclassOf.Select(a => (a.Sub, a.Super)));
}
```

- [ ] **Step 2: Run tests — fail (disjoint/equivalent axioms empty; ParseNQuads returns empty)**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests.Disjoint|FullyQualifiedName~OntologyViewBuilderTests.Matches" --logger "console;verbosity=normal" 2>&1 | tail -25`
Expected: 2 tests FAIL.

- [ ] **Step 3: Add disjoint + equivalent branches to BuildCore + implement ParseNQuads**

In `BuildCore`, add to the existing foreach loop (after the property branches):

```csharp
else if (piri == "http://www.w3.org/2002/07/owl#disjointWith" && q.Object is Oxigraph.NamedNode dj)
{
    disjointWith.Add(new PairAxiom(siri, dj.Value));
}
else if (piri == "http://www.w3.org/2002/07/owl#equivalentClass" && q.Object is Oxigraph.NamedNode ec)
{
    equivalentClass.Add(new PairAxiom(siri, ec.Value));
}
```

Declare `disjointWith` and `equivalentClass` lists above the foreach loop.

Update the final `OntologyResponse` ctor:

```csharp
Axioms: new OntologyAxioms(
    SubclassOf: subclassOf,
    DisjointWith: disjointWith,
    EquivalentClass: equivalentClass),
```

Update the `Stats` ctor:

```csharp
Stats: new OntologyStats(
    ClassCount: classList.Count,
    PropertyCount: objList.Count + datList.Count,
    AxiomCount: subclassOf.Count + disjointWith.Count + equivalentClass.Count),
```

Implement `ParseNQuads` in the same file (replace the placeholder):

```csharp
private static IEnumerable<Oxigraph.Quad> ParseNQuads(byte[] shard)
{
    if (shard.Length == 0) yield break;
    var text = System.Text.Encoding.UTF8.GetString(shard);
    foreach (var rawLine in text.Split('\n'))
    {
        var line = rawLine.TrimEnd('\r');
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var q = TryParseLine(line);
        if (q is not null) yield return q;
    }
}

private static Oxigraph.Quad? TryParseLine(string line)
{
    var tokens = Tokenize(line);
    if (tokens.Count < 4) return null;
    if (tokens[^1] != ".") return null;
    var subject = ParseTerm(tokens[0]);
    var predicate = ParseTerm(tokens[1]);
    var obj = ParseTerm(tokens[2]);
    if (subject is not Oxigraph.INamedOrBlankNode sn
        || predicate is not Oxigraph.NamedNode pn
        || obj is null) return null;

    Oxigraph.IGraphName? graph = null;
    if (tokens.Count >= 5 && tokens[3] != ".")
    {
        var g = ParseTerm(tokens[3]);
        if (g is Oxigraph.NamedNode gn) graph = gn;
        else if (g is Oxigraph.DefaultGraph) graph = new Oxigraph.DefaultGraph();
        else return null;
    }

    if (obj is Oxigraph.INamedNode on) return new Oxigraph.Quad(sn, pn, on, graph);
    if (obj is Oxigraph.BlankNode ob) return new Oxigraph.Quad(sn, pn, ob, graph);
    if (obj is Oxigraph.Literal ol) return new Oxigraph.Quad(sn, pn, ol, graph);
    return null;
}

private static Oxigraph.Term? ParseTerm(string token)
{
    if (token.StartsWith("<") && token.EndsWith(">"))
        return new Oxigraph.NamedNode(token[1..^1]);
    if (token.StartsWith("_:"))
        return new Oxigraph.BlankNode(token[2..]);
    if (token.StartsWith("\""))
    {
        var endQuote = token.IndexOf('"', 1);
        if (endQuote < 0) return null;
        var value = token[1..endQuote];
        var rest = token[(endQuote + 1)..];
        if (rest.StartsWith("@"))
            return new Oxigraph.Literal(value, language: rest[1..]);
        if (rest.StartsWith("^^<") && rest.EndsWith(">"))
            return new Oxigraph.Literal(value,
                new Oxigraph.NamedNode(rest[3..^1]));
        return new Oxigraph.Literal(value);
    }
    return null;
}

private static List<string> Tokenize(string line)
{
    var tokens = new List<string>();
    var i = 0;
    while (i < line.Length)
    {
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i >= line.Length) break;
        if (line[i] == '<')
        {
            var end = line.IndexOf('>', i + 1);
            if (end < 0) break;
            tokens.Add(line[i..(end + 1)]);
            i = end + 1;
        }
        else if (line[i] == '_')
        {
            var j = i;
            while (j < line.Length && !char.IsWhiteSpace(line[j])) j++;
            tokens.Add(line[i..j]);
            i = j;
        }
        else if (line[i] == '"')
        {
            var j = i + 1;
            while (j < line.Length && line[j] != '"')
            {
                if (line[j] == '\\' && j + 1 < line.Length) j += 2;
                else j++;
            }
            if (j >= line.Length) break;
            j++;
            if (j < line.Length && line[j] == '@')
            {
                var k = j;
                while (k < line.Length && !char.IsWhiteSpace(line[k])) k++;
                tokens.Add(line[i..k]);
                i = k;
            }
            else if (j + 1 < line.Length && line[j] == '^' && line[j + 1] == '^')
            {
                var open = line.IndexOf('<', j);
                var close = line.IndexOf('>', open + 1);
                if (open < 0 || close < 0) break;
                tokens.Add(line[i..(close + 1)]);
                i = close + 1;
            }
            else
            {
                tokens.Add(line[i..j]);
                i = j;
            }
        }
        else if (line[i] == '.')
        {
            tokens.Add(".");
            i++;
        }
        else
        {
            var j = i;
            while (j < line.Length && !char.IsWhiteSpace(line[j]) && line[j] != '.') j++;
            tokens.Add(line[i..j]);
            i = j;
        }
    }
    return tokens;
}
```

- [ ] **Step 4: Run all builder tests — they pass**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyViewBuilderTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 7/7 passing (5 prior + 2 new).

- [ ] **Step 5: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/OntologyViewBuilder.cs src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs
git commit -m "feat(ontology): BuildCore adds disjoint/equivalent + N-Quads parser

Add owl:disjointWith + owl:equivalentClass branches to BuildCore (Python
schema.py:283-286). Stats.axiom_count now includes all three axiom
kinds. Implement ParseNQuads for the release path: canonical RDF 1.1
N-Quads grammar (subject predicate object graphOrDefault '.') with
support for IRIs, blank nodes, and typed / language-tagged literals.
BuildFromNQuadsAsync now feeds the same BuildCore so live and release
endpoints emit identical shape. Round-trip test asserts BuildFromStore
and BuildFromNQuads agree on the same graph."
```

---

### Task 6: OntologyService.GetViewAsync — happy path + access checks

**Files:**
- Modify: `src/OnToPilot/Ontology/OntologyService.cs` (add `GetViewAsync` method + inject `OntologyViewBuilder`)
- Modify: `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs` (register `OntologyViewBuilder` as Singleton)
- Create: `src/OnToPilot.Tests/Ontology/OntologyServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 records, Task 2-5 builder
- Consumes: existing `_db` / `_clock` / `_access` / `_editor` / `_store` / `_allocator` fields
- Produces: `Task<OntologyResponse?>` — returns null when KS not found, throws `InvalidOperationException` for non-Viewer, throws `KeyNotFoundException` when the resolved KS row is gone between resolve + access

- [ ] **Step 1: Register `OntologyViewBuilder` in the service collection**

Modify `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs` — find the section that registers `OntologyService` / `OntologyEditor` / `StoreWrapper` and add a Singleton registration line for `OntologyViewBuilder`:

```csharp
builder.Services.AddSingleton<OntologyViewBuilder>();
```

(Place it near `AddSingleton<OntologyEditor>` for visual grouping.)

- [ ] **Step 2: Add `GetViewAsync` method to `OntologyService.cs`**

In `src/OnToPilot/Ontology/OntologyService.cs`:

1. Add a constructor field:
```csharp
private readonly OntologyViewBuilder _builder;
```

2. Extend the constructor parameter list (5th parameter after `_allocator`):
```csharp
public OntologyService(
    OnToPilotDbContext db,
    TimeProvider clock,
    KnowledgeSystemAccessService access,
    OntologyEditor editor,
    StoreWrapper store,
    LegacyIdAllocator allocator,
    OntologyViewBuilder builder)
{
    // ... existing assignments ...
    _builder = builder;
}
```

3. Add the public method (after `ResetAsync`, before the `Internals` section):
```csharp
/// <summary>
/// Read the curated TBox view for the given knowledge system. Returns
/// <c>null</c> when the caller is not resolvable (no actor id) or when
/// the KS row no longer exists (deleted between resolve + access).
/// Throws <see cref="InvalidOperationException"/> when the caller's
/// effective role is below <see cref="KSRole.Viewer"/>.
/// </summary>
public async Task<OntologyResponse?> GetViewAsync(
    Guid ksId, Actor actor, CancellationToken ct)
{
    var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
    if (user is null || ks is null) return null;

    var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
    if (role < KSRole.Viewer)
        throw new InvalidOperationException(
            "Viewer access is required to read the ontology view.");

    var view = await _builder.BuildFromStoreAsync(_store, ks.GraphIri, ct).ConfigureAwait(false);
    return view with
    {
        KnowledgeSystem = new KnowledgeSystemMeta(
            Id: ks.Id,
            Name: ks.Name,
            BaseIri: ks.BaseIri,
            Release: null),
    };
}
```

- [ ] **Step 3: Build to confirm signature compiles**

Run: `cd "e:/GitHub/ontopilot" && dotnet build src/OnToPilot/OnToPilot.csproj -c Release 2>&1 | tail -10`
Expected: 0 errors.

- [ ] **Step 4: Write failing unit test for happy path + access denial + not-found**

Create `src/OnToPilot.Tests/Ontology/OntologyServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Ontology;

[Collection(nameof(ExtractionTestCollection))]
public sealed class OntologyServiceTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _app;

    public OntologyServiceTests(AuthTestWebApplicationFactory app) { _app = app; }

    [Fact]
    public async Task GetViewAsync_returns_view_with_knowledge_system_meta_for_admin()
    {
        var db = _app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "ontology-service-happy");
        var actor = new Actor(admin.Id.ToString());
        using var scope = _app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OntologyService>();

        var view = await service.GetViewAsync(ks.Id, actor, CancellationToken.None);

        Assert.NotNull(view);
        Assert.NotNull(view!.KnowledgeSystem);
        Assert.Equal(ks.Id, view.KnowledgeSystem!.Id);
        Assert.Equal(ks.Name, view.KnowledgeSystem.Name);
        Assert.Equal(ks.BaseIri, view.KnowledgeSystem.BaseIri);
        Assert.Null(view.KnowledgeSystem.Release);
        Assert.Equal(0, view.Stats.ClassCount);
    }

    [Fact]
    public async Task GetViewAsync_returns_null_when_KS_not_found()
    {
        var db = _app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var actor = new Actor(admin.Id.ToString());
        using var scope = _app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OntologyService>();

        var view = await service.GetViewAsync(
            Guid.NewGuid(), actor, CancellationToken.None);

        Assert.Null(view);
    }

    [Fact]
    public async Task GetViewAsync_throws_for_non_viewer()
    {
        var db = _app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var ks = await CreateKsAsync(db, "ontology-service-norole");

        var otherUser = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = "outsider",
            DisplayName = "Outsider",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("x", workFactor: 4),
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(otherUser);
        await db.SaveChangesAsync();

        var actor = new Actor(otherUser.Id.ToString());
        using var scope = _app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OntologyService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetViewAsync(ks.Id, actor, CancellationToken.None));
        Assert.Contains("Viewer access", ex.Message);
    }

    private static async Task<KnowledgeSystemEntity> CreateKsAsync(
        OnToPilotDbContext db, string tag)
    {
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"),
            Id = Guid.NewGuid(),
            Name = $"ks-{tag}",
            Description = tag,
            OwnerId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id,
            BaseIri = $"http://example.com/{tag}#",
            GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }
}
```

- [ ] **Step 5: Run tests — pass (after Step 2 implementation)**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyServiceTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 3/3 passing.

- [ ] **Step 6: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/OntologyService.cs src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs src/OnToPilot.Tests/Ontology/OntologyServiceTests.cs
git commit -m "feat(ontology): OntologyService.GetViewAsync with access checks

Add GetViewAsync(Guid, Actor, CancellationToken) to OntologyService:
resolves user + KS (null → not found), gates Viewer role via
KnowledgeSystemAccessService.GetEffectiveRoleAsync, delegates to
OntologyViewBuilder.BuildFromStoreAsync, attaches KnowledgeSystemMeta
(Guid Id, Name, BaseIri, Release: null) to the response. Throws
InvalidOperationException for sub-Viewer callers. Register
OntologyViewBuilder as Singleton in service collection. Three new
unit tests cover happy path, not-found, and access-denied paths."
```

---

### Task 7: Dispatcher `ontology.get` arm + 404 mapping

**Files:**
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs` (`GetOntologyAsync(Guid, ...)` body + `InvokeOntologyGetAsync` arm)
- Possibly modify: `src/OnToPilot/Api/FastApiErrorMiddleware.cs` (add `KeyNotFoundException` → 404 branch if missing)

**Interfaces:**
- Consumes: Task 6's `OntologyService.GetViewAsync`
- Produces: dispatcher `ontology.get` arm returns full envelope (or 404 via `KeyNotFoundException` translation)

- [ ] **Step 1: Replace the Guid overload body**

In `src/OnToPilot/Integration/InternalOperationDispatcher.cs`, replace the placeholder Guid overload (lines 342-352) with:

```csharp
/// <inheritdoc />
public async Task<OntologyResponse> GetOntologyAsync(
    Guid knowledgeSystemId,
    Actor actor,
    CancellationToken cancellationToken)
{
    var service = ResolveOntologyService();
    if (service is null) return await EmptyOntologyResponseAsync().ConfigureAwait(false);
    var view = await service.GetViewAsync(knowledgeSystemId, actor, cancellationToken).ConfigureAwait(false);
    if (view is null)
        throw new KeyNotFoundException($"Knowledge system {knowledgeSystemId} not found.");
    return view;
}
```

(`ResolveOntologyService()` already exists in the dispatcher.)

- [ ] **Step 2: Build**

Run: `cd "e:/GitHub/ontopilot" && dotnet build src/OnToPilot/OnToPilot.csproj -c Release 2>&1 | tail -10`
Expected: 0 errors.

- [ ] **Step 3: Confirm `KeyNotFoundException` is translated to 404 by middleware**

Open `src/OnToPilot/Api/FastApiErrorMiddleware.cs`. Search for `KeyNotFoundException`. If not present, the middleware needs a new branch:

```csharp
catch (KeyNotFoundException ex)
{
    await WriteEnvelopeAsync(context, StatusCodes.Status404NotFound,
        new FastApiError(ex.Message)).ConfigureAwait(false);
}
```

If present, no change.

- [ ] **Step 4: Run existing ontology-touching tests — must stay green**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~Ontology|FullyQualifiedName~KnowledgeApiTests|FullyQualifiedName~ConflictApiTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: same pass count as before Task 6 (no regressions).

- [ ] **Step 5: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Integration/InternalOperationDispatcher.cs src/OnToPilot/Api/FastApiErrorMiddleware.cs
git commit -m "feat(dispatcher): ontology.get calls real OntologyService.GetViewAsync

Replace EmptyOntologyResponseAsync() placeholder in
GetOntologyAsync(Guid, Actor, ct) with a call to OntologyService.
Not-found KS surfaces as KeyNotFoundException → FastApiErrorMiddleware
404 envelope (added the catch branch if missing). Empty envelope still
returned when the contract-test factory registers a null StoreWrapper."
```

---

### Task 8: PublishedOntologyService + Dispatcher published arms

**Files:**
- Create: `src/OnToPilot/Ontology/PublishedOntologyService.cs`
- Modify: `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs` (register new service)
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs` (replace `published.ontology` + `published.release.ontology` arms)
- Create: `src/OnToPilot.Tests/Ontology/PublishedOntologyServiceTests.cs`

**Interfaces:**
- Consumes: Task 5's `OntologyViewBuilder.BuildFromNQuadsAsync`, Task 1's `KnowledgeSystemMeta`, `ReleaseArtifactStore`, `OnToPilotDbContext`
- Produces: `Task<OntologyResponse?>` — release-typed view with `Release` set; null when release row missing

- [ ] **Step 1: Write failing test**

Create `src/OnToPilot.Tests/Ontology/PublishedOntologyServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Foundation;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Ontology;

[Collection(nameof(ExtractionTestCollection))]
public sealed class PublishedOntologyServiceTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _app;

    public PublishedOntologyServiceTests(AuthTestWebApplicationFactory app) { _app = app; }

    [Fact]
    public async Task GetViewAsync_returns_view_with_release_version_for_active_release()
    {
        var db = _app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);

        var publicId = "pub-" + Guid.NewGuid().ToString("N")[..8];
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"),
            Id = Guid.NewGuid(),
            PublicId = publicId,
            Name = "ks-pub-test",
            Description = "",
            OwnerId = admin.Id,
            BaseIri = "http://example.com/pub#",
            GraphIri = "http://example.com/graph/pub",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);

        var releaseId = Guid.NewGuid();
        var release = new OntologyReleaseEntity
        {
            Id = releaseId,
            KnowledgeSystemId = ks.Id,
            Version = "1.0.0",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.OntologyReleases.Add(release);
        await db.SaveChangesAsync();

        var rdfRoot = _app.Services
            .GetRequiredService<OnToPilotOptions>().Storage.RdfRoot;
        var shardStore = new ReleaseArtifactStore(System.IO.Path.Combine(rdfRoot, "releases"));
        shardStore.Write(releaseId.ToString(), RdfLayer.TBox,
            System.Text.Encoding.UTF8.GetBytes(
                "<urn:Animal> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> " +
                "<http://www.w3.org/2002/07/owl#Class> <http://example.com/graph/pub> .\n"));

        var actor = new Actor(admin.Id.ToString());
        using var scope = _app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PublishedOntologyService>();

        var view = await service.GetViewAsync(publicId, "1.0.0", actor, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Single(view!.Classes);
        Assert.Equal("urn:Animal", view.Classes[0].Iri);
        Assert.NotNull(view.KnowledgeSystem);
        Assert.Equal("1.0.0", view.KnowledgeSystem!.Release);
    }
}
```

- [ ] **Step 2: Run test — fail (class doesn\'t exist)**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~PublishedOntologyServiceTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: compile failure.

- [ ] **Step 3: Create `PublishedOntologyService.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Ontology;

/// <summary>
/// Reads the curated TBox view from a published release's tbox.nq
/// shard (RDF 1.1 N-Quads on disk, no Oxigraph dependency). The
/// controller layer (PublishedController) handles scope check +
/// cache headers + release resolution; this service assumes those
/// have already happened.
/// </summary>
public sealed class PublishedOntologyService
{
    private readonly OnToPilotDbContext _db;
    private readonly ReleaseArtifactStore _artifacts;
    private readonly OntologyViewBuilder _builder;

    public PublishedOntologyService(
        OnToPilotDbContext db,
        ReleaseArtifactStore artifacts,
        OntologyViewBuilder builder)
    {
        _db = db;
        _artifacts = artifacts;
        _builder = builder;
    }

    public async Task<OntologyResponse?> GetViewAsync(
        string publicId, string version, Actor actor, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (ks is null) return null;

        var release = await _db.OntologyReleases
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.KnowledgeSystemId == ks.Id && r.Version == version,
                ct)
            .ConfigureAwait(false);
        if (release is null) return null;

        var tboxShard = _artifacts.Read(release.Id.ToString(), RdfLayer.TBox);
        var view = await _builder
            .BuildFromNQuadsAsync(tboxShard, ct)
            .ConfigureAwait(false);

        return view with
        {
            KnowledgeSystem = new KnowledgeSystemMeta(
                Id: ks.Id,
                Name: ks.Name,
                BaseIri: ks.BaseIri,
                Release: release.Version),
        };
    }
}
```

- [ ] **Step 4: Register the service**

In `OntologyServiceCollectionExtensions.cs`, add:

```csharp
builder.Services.AddScoped<PublishedOntologyService>();
```

(Place near the existing `AddScoped<OntologyService>()` call.)

- [ ] **Step 5: Replace the dispatcher arms**

In `InternalOperationDispatcher.cs`, find the `InvokeAsync` switch arms for `published.ontology` and `published.release.ontology`. Replace with calls to a helper that resolves the active deployment\'s release when `version == null`, or pins to the URL version otherwise.

```csharp
"published.ontology" => await InvokePublishedOntologyAsync(request, version: null, ct).ConfigureAwait(false),
"published.release.ontology" => await InvokePublishedOntologyAsync(request, version: request.ResourceId, ct).ConfigureAwait(false),
```

`InvokePublishedOntologyAsync` mirrors `PublishedController.ResolveReleaseAsync` minimally: when `version` is null, pick the latest deployment row by `CreatedAt`, take its `ReleaseId`, fetch the release row, return `version = release.Version`. The dispatcher invokes `PublishedOntologyService.GetViewAsync(publicId, version, actor, ct)` and returns the view or the empty envelope when unresolvable.

Add `ResolvePublishedOntologyService()`:

```csharp
private PublishedOntologyService? ResolvePublishedOntologyService() =>
    _services.GetService(typeof(PublishedOntologyService)) as PublishedOntologyService;
```

- [ ] **Step 6: Run test — pass**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~PublishedOntologyServiceTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 1/1 passing.

- [ ] **Step 7: Run existing published-related tests — must stay green**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~Published" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: same pass count.

- [ ] **Step 8: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/PublishedOntologyService.cs src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs src/OnToPilot/Integration/InternalOperationDispatcher.cs src/OnToPilot.Tests/Ontology/PublishedOntologyServiceTests.cs
git commit -m "feat(ontology): PublishedOntologyService for release TBox read

Add PublishedOntologyService that resolves a release row by
(publicId, version), reads the tbox.nq shard from ReleaseArtifactStore,
and builds the curated view via OntologyViewBuilder.BuildFromNQuadsAsync.
Attach KnowledgeSystemMeta with Release = release.Version. Replace the
two placeholder dispatcher arms (published.ontology resolves the current
deployment's release; published.release.ontology pins to the URL version)
with calls to the new service. Empty envelope still returned when the
service is unresolvable (contract-test path)."
```

---

### Task 9: ExternalOntologyService + Dispatcher `external.ontology` arm

**Files:**
- Create: `src/OnToPilot/Ontology/ExternalOntologyService.cs`
- Modify: `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs`
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs` (replace `external.ontology` arm)
- Create: `src/OnToPilot.Tests/Ontology/ExternalOntologyServiceTests.cs`

**Interfaces:**
- Consumes: Task 2-5 builder, Task 1 `ExternalKnowledgeSystemMeta`
- Produces: `Task<OntologyResponse?>` — view with `ExternalKnowledgeSystemMeta` attached (public_id string, not Guid)

- [ ] **Step 1: Write failing test**

Create `src/OnToPilot.Tests/Ontology/ExternalOntologyServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Ontology;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Ontology;

[Collection(nameof(ExtractionTestCollection))]
public sealed class ExternalOntologyServiceTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _app;

    public ExternalOntologyServiceTests(AuthTestWebApplicationFactory app) { _app = app; }

    [Fact]
    public async Task GetViewAsync_returns_view_with_public_id_meta()
    {
        var db = _app.CreateDbContext();
        var admin = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername);
        var publicId = "ext-" + Guid.NewGuid().ToString("N")[..8];
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("ks"),
            Id = Guid.NewGuid(),
            PublicId = publicId,
            Name = "ks-ext-test",
            Description = "",
            OwnerId = admin.Id,
            BaseIri = "http://example.com/ext#",
            GraphIri = "http://example.com/graph/ext",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();

        var actor = new Actor(admin.Id.ToString());
        using var scope = _app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExternalOntologyService>();

        var view = await service.GetViewAsync(publicId, actor, CancellationToken.None);

        Assert.NotNull(view);
        var meta = Assert.IsType<ExternalKnowledgeSystemMeta>(view!.KnowledgeSystem);
        Assert.Equal(publicId, meta.PublicId);
    }
}
```

- [ ] **Step 2: Run test — fail**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~ExternalOntologyServiceTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: compile failure.

- [ ] **Step 3: Create `ExternalOntologyService.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Ontology;

/// <summary>
/// Reads the curated TBox view for the public API surface
/// (/api/v1/knowledge-systems/{public_id}/ontology). Resolves the
/// KS by public id (NOT internal Guid — external callers never see
/// the internal id). Attaches ExternalKnowledgeSystemMeta with
/// public_id (string) instead of the Guid variant.
/// </summary>
public sealed class ExternalOntologyService
{
    private readonly OnToPilotDbContext _db;
    private readonly StoreWrapper? _store;
    private readonly OntologyViewBuilder _builder;

    public ExternalOntologyService(
        OnToPilotDbContext db,
        StoreWrapper? store,
        OntologyViewBuilder builder)
    {
        _db = db;
        _store = store;
        _builder = builder;
    }

    public async Task<OntologyResponse?> GetViewAsync(
        string publicId, Actor actor, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, ct)
            .ConfigureAwait(false);
        if (ks is null) return null;

        var view = await _builder
            .BuildFromStoreAsync(_store, ks.GraphIri, ct)
            .ConfigureAwait(false);

        return view with
        {
            KnowledgeSystem = new ExternalKnowledgeSystemMeta(
                PublicId: ks.PublicId,
                Name: ks.Name,
                BaseIri: ks.BaseIri),
        };
    }
}
```

- [ ] **Step 4: Register the service**

```csharp
builder.Services.AddScoped<ExternalOntologyService>();
```

- [ ] **Step 5: Replace the dispatcher arm**

In `InternalOperationDispatcher.cs`:

```csharp
"external.ontology" => await InvokeExternalOntologyAsync(request, ct).ConfigureAwait(false),
```

Helper:

```csharp
private async Task<object?> InvokeExternalOntologyAsync(
    InternalRequest request, CancellationToken ct)
{
    var service = ResolveExternalOntologyService();
    if (service is null) return EmptyOntologyResponse();
    var publicId = request.PublicId
        ?? throw new InvalidOperationException("publicId required for external.ontology");
    var view = await service.GetViewAsync(publicId, request.Actor, ct).ConfigureAwait(false);
    return view ?? EmptyOntologyResponse();
}

private ExternalOntologyService? ResolveExternalOntologyService() =>
    _services.GetService(typeof(ExternalOntologyService)) as ExternalOntologyService;
```

- [ ] **Step 6: Run tests — pass**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~ExternalOntologyServiceTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 1/1 passing.

- [ ] **Step 7: Run existing external-related tests — must stay green**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~External" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: same pass count.

- [ ] **Step 8: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/ExternalOntologyService.cs src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs src/OnToPilot/Integration/InternalOperationDispatcher.cs src/OnToPilot.Tests/Ontology/ExternalOntologyServiceTests.cs
git commit -m "feat(ontology): ExternalOntologyService with public_id meta

Add ExternalOntologyService that resolves KS by public_id and builds
the live TBox view via OntologyViewBuilder. Attaches
ExternalKnowledgeSystemMeta (public_id string, not Guid) to match
the Python wire shape. Replace the external.ontology placeholder arm
with a call to the new service. Empty envelope still returned when
the service is unresolvable."
```

---

### Task 10: Integration tests — 4 endpoints at HTTP layer

**Files:**
- Create: `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`

**Interfaces:**
- Consumes: All prior tasks' service implementations
- Produces: end-to-end HTTP tests for `GET /api/knowledge/{id}/ontology`

- [ ] **Step 1: Write happy-path integration test**

Create `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Ontology;

[Collection(nameof(ExtractionTestCollection))]
public sealed class OntologyApiTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string CookieHeader = "ontopilot_session";
    private readonly AuthTestWebApplicationFactory _app;

    public OntologyApiTests(AuthTestWebApplicationFactory app) { _app = app; }

    [Fact]
    public async Task Get_ontology_returns_full_envelope_with_all_top_level_keys()
    {
        var (client, _) = await SeedAdminAndClientAsync(_app);
        var ksId = await CreateKsAsync(client);

        var res = await client.GetAsync($"/api/knowledge/{ksId}/ontology");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("classes", out _));
        Assert.True(body.TryGetProperty("object_properties", out _));
        Assert.True(body.TryGetProperty("data_properties", out _));
        Assert.True(body.TryGetProperty("axioms", out _));
        Assert.True(body.TryGetProperty("labels", out _));
        Assert.True(body.TryGetProperty("stats", out _));
        Assert.True(body.TryGetProperty("knowledge_system", out _));

        var axioms = body.GetProperty("axioms");
        Assert.True(axioms.TryGetProperty("subclass_of", out _));
        Assert.True(axioms.TryGetProperty("disjoint_with", out _));
        Assert.True(axioms.TryGetProperty("equivalent_class", out _));

        var stats = body.GetProperty("stats");
        Assert.Equal(0, stats.GetProperty("class_count").GetInt32());
        Assert.Equal(0, stats.GetProperty("property_count").GetInt32());
        Assert.Equal(0, stats.GetProperty("axiom_count").GetInt32());
    }

    [Fact]
    public async Task Get_ontology_returns_404_for_unknown_KS()
    {
        var (client, _) = await SeedAdminAndClientAsync(_app);
        var res = await client.GetAsync($"/api/knowledge/{Guid.NewGuid()}/ontology");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    private static async Task<(HttpClient client, Guid adminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"),
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                    AuthTestWebApplicationFactory.AdminPassword, workFactor: 10),
                IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = AuthTestWebApplicationFactory.AdminUsername,
            password = AuthTestWebApplicationFactory.AdminPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var adminId = db.Users.Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    private static async Task<Guid> CreateKsAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = "ks-ontology-api",
            description = "integration test",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }
}
```

- [ ] **Step 2: Run tests**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "FullyQualifiedName~OntologyApiTests" --logger "console;verbosity=normal" 2>&1 | tail -15`
Expected: 2/2 passing.

- [ ] **Step 3: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot.Tests/Ontology/OntologyApiTests.cs
git commit -m "test(ontology): HTTP integration test for /api/knowledge/{id}/ontology

Two integration tests via AuthTestWebApplicationFactory:
1. Happy path — assert every top-level key the frontend OntologyView
   type requires (classes / object_properties / data_properties /
   axioms / labels / stats / knowledge_system) is on the wire,
   including the three axiom-array sub-keys.
2. Not-found — unknown KS Guid returns 404 envelope (proves the
   KeyNotFoundException → FastApiErrorMiddleware mapping from Task 7
   works at the HTTP layer)."
```

---

### Task 11: Regenerate `BackendRegression.baseline.json`

**Files:**
- Modify: `src/OnToPilot.ApiContract.Tests/Baselines/BackendRegression.baseline.json`

**Interfaces:**
- Consumes: Tasks 7-9 service implementations producing the full envelope
- Produces: a contract test baseline that matches the new shape

- [ ] **Step 1: Run contract tests — see the diff**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj --logger "console;verbosity=normal" 2>&1 | tail -50`

Identify which test failures are caused by the new full-envelope shape (vs. the pre-existing 13/160). Look for assertions that check `properties` (singular, the old stub field) — these need the baseline updated to `object_properties` / `data_properties`.

Expected delta: ~3-5 contract tests now fail where they previously expected `properties` (singular) — they need the baseline to reflect the new shape.

- [ ] **Step 2: Update the baseline**

Open `src/OnToPilot.ApiContract.Tests/Baselines/BackendRegression.baseline.json`. Find every entry that expected the old `{classes, properties}` shape. Replace `properties` (singular) with `object_properties` and `data_properties` (each `[]` for empty TBox). Add `axioms`, `labels`, `stats`, `knowledge_system` fields per the empty-envelope shape:

```json
{
  "classes": [],
  "object_properties": [],
  "data_properties": [],
  "axioms": { "subclass_of": [], "disjoint_with": [], "equivalent_class": [] },
  "labels": {},
  "stats": { "class_count": 0, "property_count": 0, "axiom_count": 0 },
  "knowledge_system": { "id": "<guid>", "name": "...", "base_iri": "...", "release": null }
}
```

Use the actual diff output from Step 1 as the source of truth. The diff text tells you exactly which fields are present and missing.

- [ ] **Step 3: Run contract tests — confirm baseline match**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj --logger "console;verbosity=normal" 2>&1 | tail -15`

Expected: pass count increases by the number of new-shape assertions that were failing. **Total fail count must remain at or below the 13/160 pre-existing baseline.** If it went UP, you've changed more than expected — re-read the diff and trim the baseline change to only the ontology envelope entries.

- [ ] **Step 4: Commit**

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot.ApiContract.Tests/Baselines/BackendRegression.baseline.json
git commit -m "chore(contract): regenerate baseline for Stage 2 ontology view

The 4 placeholder arms (ontology.get / external.ontology /
published.ontology / published.release.ontology) used to emit a stub
{classes, properties} envelope. Stage 2 wires them to the full Python
build_view shape with 7 top-level keys. Update the BackendRegression
baseline so the contract tests match the new envelope. Final test
count must stay at or below the pre-existing 13/160 baseline — no new
failures from this change."
```

---

### Task 12: Final regression sweep

**Files:** none (verification only)

- [ ] **Step 1: Build clean**

Run: `cd "e:/GitHub/ontopilot" && dotnet build -c Release 2>&1 | tail -10`
Expected: 0 errors. (Warnings acceptable from sources outside this plan's scope.)

- [ ] **Step 2: Full OnToPilot.Tests sweep**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --logger "console;verbosity=normal" 2>&1 | tail -30`

Expected: pre-existing baseline was ~354-356 / ~365 passing. After Stage 2:
- 13 new tests added (Tasks 2-10): 7 builder + 3 service + 1 published + 1 external + 2 integration = 14 new tests.
- Final: ~367-370 / ~378 passing.
- **Fail count MUST NOT INCREASE** vs. the pre-existing baseline. If a previously-passing test now fails, debug before committing.

- [ ] **Step 3: Full contract test sweep**

Run: `cd "e:/GitHub/ontopilot" && dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj --logger "console;verbosity=normal" 2>&1 | tail -15`

Expected: 13 / 160 (no change). The 13 pre-existing failures must stay at 13.

- [ ] **Step 4: Document results**

No code change. Record the exact numbers in a short note to the user:

```
Final regression — Stage 2 ontology view real read
- Unit tests: X / Y passing (delta from baseline: +Z)
- Contract tests: 13 / 160 failing (no regression)
- New files: 6 (3 services + 3 test files)
- Modified files: 6 (DTO + JsonContext + OntologyService + extensions + dispatcher + baseline)
- Build: 0 warnings, 0 errors
- Stage 2 complete.
```

- [ ] **Step 5: Tag the branch**

```bash
cd "e:/GitHub/ontopilot"
git tag -a "stage-2-ontology-view" -m "Stage 2 — ontology view real read (3 dispatcher arms + OntologyViewBuilder)"
git log --oneline -15
```

(Per working instructions: commits stay local on `dotnet`. Tag also stays local.)

---