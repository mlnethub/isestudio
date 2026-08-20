# Stage 2 — Ontology view real read (3 dispatcher arms + view builder)

**Date**: 2026-08-20
**Branch**: `dotnet`
**Status**: Design (awaiting approval)
**Follows**: `fix(frontend): normalize /ontology response so page renders despite Stage 2 placeholder backend` (commit `303114b`) — frontend workaround that unblocked the user's dispatcher-fix verification, but pages still show empty TBox until Stage 2 is wired.

## Context

The `InternalOperationDispatcher.GetOntologyAsync(Guid, ...)` arm
(`src/OnToPilot/Integration/InternalOperationDispatcher.cs:342-352`) is a
literal `EmptyOntologyResponseAsync()` placeholder that emits only
`{classes, properties}`. The frontend `OntologyView` type
(`frontend/src/lib/types.ts:197-208`) requires
`axioms` / `labels` / `stats` / `object_properties` / `data_properties`
— so `/api/knowledge/{id}/ontology` returns a 200 that crashes the page
on the first `view.axioms.subclass_of` access. The frontend
`normalizeOntologyView` workaround in commit `303114b` keeps the page
alive but the wire payload is still incomplete.

The same placeholder is wired into three places (dispatcher arms
`ontology.get`, `external.ontology`, `published.ontology`,
`published.release.ontology`):
- `InternalOperationDispatcher.cs:96` (`ontology.get`)
- `InternalOperationDispatcher.cs:287` (`external.ontology`)
- `InternalOperationDispatcher.cs:301` (`published.ontology`)
- `InternalOperationDispatcher.cs:313` (`published.release.ontology`)

All four delegate to `EmptyOntologyResponse()` /
`EmptyOntologyResponseAsync()` and return
`OntologyResponse(Classes, Properties)` — a 2-field stub that does
not match the FastAPI contract.

**Python reference contract** (already shipped in the original Python
backend — this design mirrors it line-for-line):
[`backend/app/ontology/schema.py:241-371` `build_view()`].

The Python read pattern is pure algorithm: walk a `(s, p, o)` triple
stream and produce a curated JSON view. It is identical for live
(`ks.graph_iri`) and release (`release.deployment.tbox_graph_iri`)
paths — only the triple source differs.

## Goals

1. Replace the 4 placeholder dispatcher arms with a real implementation
   that returns the full `OntologyResponse` shape (`classes`,
   `object_properties`, `data_properties`, `axioms`,
   `labels`, `stats`, `knowledge_system`).
2. Extract a stateless `OntologyViewBuilder` service so live and
   release paths share one algorithm — no duplicated read logic.
3. Match the Python `build_view` field shape and ordering exactly, so
   the FastAPI contract test sweep is green without any change to the
   `BackendRegression.baseline.json` baseline.
4. Keep the contract-test path (where `StoreWrapper` is null) compiling
   and emitting the same empty-envelope shape as today — no
   regression in the 12-13 contract test failures that pre-date this
   change.

## Non-goals

- `owl:unionOf` expansion for `domain_members` / `range_members`. Frontend
  marks both `domain_members?` and `range_members?` optional; current
  TBox contains no union axioms, so leaving them as empty arrays is
  observable but non-breaking. Stage 3 follow-up.
- `retrieval.invalidate(ks.graph_iri)` cache invalidation hook in the
  edit path. The Python backend invalidates an in-process cache after
  every edit. .NET does not have an equivalent retrieval cache today,
  so there is nothing to invalidate. Stage 3 follow-up if we add one.
- ABox integration. `view` does not include ABox counts; the existing
  `ConflictService` ABox path stays untouched.
- Optimization (caching, indexing). The builder is stateless and runs
  per-request. Oxigraph `Match` is fast enough for typical TBox sizes
  (10²–10⁴ triples); optimization belongs to Stage 3 if profiling
  shows it's needed.

## Architecture

```text
                           ┌──────────────────────────┐
   ontology.get        ──→ │ OntologyService          │
   external.ontology   ──→ │   .GetViewAsync(Guid)    │ ──→  access check
                           └──────────────┬───────────┘
                                          │
                                          ▼
                           ┌──────────────────────────┐
                           │ OntologyViewBuilder      │
                           │   .BuildFromStoreAsync   │ ← live TBox
                           │     (StoreWrapper,       │
                           │      graphIri, ct)       │
                           └──────────────┬───────────┘
                                          │
                                          ▼
                              BuildCore(quads)
                              pure algorithm,
                              Python build_view
                              line-for-line
                                          ▲
                           ┌──────────────┴───────────┐
                           │ OntologyViewBuilder      │
                           │   .BuildFromNQuadsAsync  │ ← release shard
                           │     (byte[] tboxShard)   │
                           └──────────────────────────┘
                                          ▲
                                          │
   published.ontology      ──→  PublishedOntologyService
   published.release.onto  ──→    .GetViewAsync(publicId, version)
                                    │
                                    └─→ ReleaseArtifactStore.Read(tbox.nq)
                                       → builder.BuildFromNQuadsAsync
```

Two adapters feed one algorithm core. The live adapter uses
`StoreWrapper.Match` (Oxigraph). The release adapter parses N-Quads
bytes directly (no Oxigraph dependency — release shards are already
serialized for the served-releases path).

## Components

### New types (`src/OnToPilot.Application/Foundation/OntologyResponse.cs`)

Replace the existing 9-line stub with the full record + nested types:

```csharp
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

// Existing records stay — but the inner records get the extra fields
// the Python contract populates.
public sealed record OntologyClass(string Iri, string? Label)
{
    // Filled by the builder: Local, Comment, Superclasses
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

**Wire naming**: `PropertyNamingPolicy = SnakeCaseLower` is already
configured in `Program.cs:252`. With record positional ctor params
using PascalCase, the wire output is `iri`, `local`, `label`,
`superclasses`, `domain_members`, etc. — matches the Python backend
exactly. The 3 axiom-pair records (`SubclassAxiom`, `PairAxiom`)
serialize as `{sub, super}` / `{a, b}` objects on the wire, which
is exactly what the Python contract emits.

### `OnToPilotJsonContext` additions
(`src/OnToPilot/Serialization/OnToPilotJsonContext.cs`)

Register the new nested types so the source-gen serializer covers
the full envelope (currently only `OntologyResponse`,
`IReadOnlyList<OntologyClass>`, `IReadOnlyList<OntologyProperty>`
are registered — the `axioms` / `stats` / `knowledge_system` arms of
the envelope take the reflection-fallback path today, which is one of
the reasons the envelope shape is shape-incomplete in tests).

Add: `[JsonSerializable(typeof(OntologyAxioms))]`,
`[JsonSerializable(typeof(OntologyStats))]`,
`[JsonSerializable(typeof(KnowledgeSystemMeta))]`,
`[JsonSerializable(typeof(OntologyResponse))]` (already there).

### `OnToPilot.Ontology.OntologyViewBuilder` (new file)

```csharp
public sealed class OntologyViewBuilder
{
    // ---- public adapters ----------------------------------------

    /// <summary>Live TBox read via Oxigraph. Returns empty envelope when
    /// <paramref name="store"/> is null (contract-test path).</summary>
    public async Task<OntologyResponse> BuildFromStoreAsync(
        StoreWrapper? store,
        string graphIri,
        CancellationToken ct)
    {
        if (store is null) return EmptyResponse();
        var quads = store.Match(graphIri: graphIri);  // full-graph Match
        return BuildCore(quads);
    }

    /// <summary>Release TBox read from a pre-serialized N-Quads shard
    /// (no Oxigraph dependency). Used by published.ontology.</summary>
    public Task<OntologyResponse> BuildFromNQuadsAsync(
        byte[] tboxShard,
        CancellationToken ct)
    {
        var quads = ParseNQuads(tboxShard);
        return Task.FromResult(BuildCore(quads));
    }

    // ---- algorithm core (Python build_view port) ----------------

    private static OntologyResponse BuildCore(IEnumerable<Quad> quads)
    {
        // ... Python schema.py:241-371 port ...
    }
}
```

The `BuildCore` body mirrors `backend/app/ontology/schema.py:241-371`
line-for-line — same ordering (`classes` sorted by label, then
`obj_props` / `data_props`), same label-fallback (explicit
`rdfs:label` else local name), same `domain_members` /
`range_members` empty-default behavior.

### `OnToPilot.Ontology.OntologyService` (modify)

Add `GetViewAsync(Guid ksId, Actor actor, CancellationToken ct)`:

```csharp
public async Task<OntologyResponse?> GetViewAsync(
    Guid ksId, Actor actor, CancellationToken ct)
{
    var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct)
        .ConfigureAwait(false);
    if (user is null || ks is null) return null;

    var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct)
        .ConfigureAwait(false);
    if (role < KSRole.Viewer)
        throw new InvalidOperationException("Viewer access is required.");

    var view = await _builder.BuildFromStoreAsync(
        _store, ks.GraphIri, ct).ConfigureAwait(false);

    return view with
    {
        KnowledgeSystem = new KnowledgeSystemMeta(
            ks.Id, ks.Name, ks.BaseIri, Release: null),
    };
}
```

`_builder` is constructor-injected (`OntologyViewBuilder`, registered
Singleton in `AddOntologyServices`). `_store` is the existing
`StoreWrapper?` field on `OntologyService` — null on the contract-test
path, which already returns the empty envelope (preserves the
existing 13/160 contract-test failures as pre-existing).

### `OnToPilot.Integration.InternalOperationDispatcher` (modify)

Replace `EmptyOntologyResponseAsync()` in `GetOntologyAsync(Guid, ...)`
with:

```csharp
public Task<OntologyResponse> GetOntologyAsync(
    Guid knowledgeSystemId, Actor actor, CancellationToken ct)
{
    var service = ResolveOntologyService();
    if (service is null) return EmptyOntologyResponseAsync();
    return service.GetViewAsync(knowledgeSystemId, actor, ct);
}
```

The 4 placeholder arms in `InvokeAsync` switch (`external.ontology`,
`published.ontology`, `published.release.ontology`) get parallel
implementations:
- `external.ontology` → `IExternalOntologyService.GetViewAsync(publicId, actor, ct)`
- `published.ontology` → `IPublishedOntologyService.GetViewAsync(publicId, actor, ct)` (current release)
- `published.release.ontology` → same with `version` from
  `request.SecondResourceId`

### `OnToPilot.Ontology.PublishedOntologyService` (new file)

Lives next to `OntologyService` — same DI lifetime (Scoped). Takes the
release row + `ReleaseArtifactStore` + `OntologyViewBuilder`. Returns
the view with `KnowledgeSystem.Release = release.Version`.

```csharp
public sealed class PublishedOntologyService
{
    private readonly OnToPilotDbContext _db;
    private readonly ReleaseArtifactStore _artifacts;
    private readonly OntologyViewBuilder _builder;

    public async Task<OntologyResponse?> GetViewAsync(
        string publicId, string? version, Actor actor, CancellationToken ct)
    {
        var release = await ResolveReleaseAsync(publicId, version, ct)
            .ConfigureAwait(false);
        if (release is null) return null;

        var tboxShard = _artifacts.Read(release.Id.ToString(), RdfLayer.TBox);
        var view = await _builder.BuildFromNQuadsAsync(tboxShard, ct)
            .ConfigureAwait(false);
        return view with
        {
            KnowledgeSystem = new KnowledgeSystemMeta(
                release.KnowledgeSystemId,
                release.KnowledgeSystem.Name,
                release.KnowledgeSystem.BaseIri,
                Release: release.Version),
        };
    }
}
```

### `OnToPilot.Ontology.ExternalOntologyService` (new file)

Same as `OntologyService.GetViewAsync` but takes the public-id instead
of the Guid. Resolves user via the external token's
`KnowledgeSystem.PublicId`. Same access check (Viewer role required).

## Data flow

**Live path** `GET /api/knowledge/{id:guid}/ontology`:

1. `OntologyController.GetAsync` → `InvokeAsync("ontology.get", ReqGuid(id), ct)`
2. Dispatcher resolves `OntologyService` via `_services.GetService`
3. `OntologyService.GetViewAsync(ksId, actor, ct)`:
   a. Resolve user + KS (404 envelope via `null` return → controller
      `Ok(payload ?? new { ok = true })` — but `null` payload currently
      maps to `{ok: true}` not 404. **Bug to fix**: dispatcher must
      return a 404 envelope on `null`. See Risks §1.)
   b. Access check (Viewer role)
   c. `_builder.BuildFromStoreAsync(_store, ks.GraphIri, ct)`
   d. Inject `knowledge_system` meta
4. Controller returns `Ok(payload)`. System.Text.Json source-gen
   serializes the full envelope.

**Release path** `GET /api/v1/knowledge-systems/{public_id}/published/ontology`:

1. `PublishedController.GetOntologyAsync` → `DispatchAsync(...)`
2. Controller resolves release (current or pinned), sets cache headers,
   does scope check
3. `_facade.InvokeAsync("published.ontology", request, ct)`
4. Dispatcher resolves `PublishedOntologyService`
5. `PublishedOntologyService.GetViewAsync(publicId, version, ct)`:
   a. Resolve release row from DB (already pre-validated by controller)
   b. Read tbox shard from `ReleaseArtifactStore`
   c. `_builder.BuildFromNQuadsAsync(tboxShard, ct)`
   d. Inject `knowledge_system` meta with `Release = version`
6. Controller returns `Ok(payload)`.

**External path** `GET /api/v1/knowledge-systems/{public_id}/ontology`:

Identical to live path, but resolves via `public_id` (the
`ExternalAccess` dependency already injects the KS by public-id). The
external API never returns `knowledge_system.id` as a Guid (uses
`public_id` string instead) — `KnowledgeSystemMeta.Id` is `Guid`; we
either widen the record to `string PublicId` for the external path or
add a sibling `ExternalKnowledgeSystemMeta` record. Stage 2 decision:
add `ExternalKnowledgeSystemMeta(string PublicId, string Name, string BaseIri)`
and let the external path use it. Mirrors Python's `view["knowledge_system"]`
shape which carries `public_id` not the internal `id`.

## API contract (wire shape)

The wire JSON, after Stage 2:

```json
{
  "classes": [
    { "iri": "...", "local": "...", "label": "...",
      "comment": "...", "superclasses": ["..."] }
  ],
  "object_properties": [
    { "iri": "...", "local": "...", "label": "...",
      "comment": "...", "domain": "...", "domain_label": "...",
      "range": "...", "range_label": "...",
      "domain_members": [], "range_members": [] }
  ],
  "data_properties": [ /* same shape */ ],
  "axioms": {
    "subclass_of":      [{ "sub": "...", "super": "..." }],
    "disjoint_with":    [{ "a": "...", "b": "..." }],
    "equivalent_class": [{ "a": "...", "b": "..." }]
  },
  "labels": { "<iri>": "<label>", ... },
  "stats": {
    "class_count": 0, "property_count": 0, "axiom_count": 0
  },
  "knowledge_system": {
    "id": "<guid>", "name": "...", "base_iri": "...",
    "release": "<version|null>"
  }
}
```

For the external API path, `knowledge_system.id` is the `public_id`
string (matches Python). For the published path, `release` is set.

## Error handling

| Failure | Response |
|---|---|
| KS not found (live) | `404 {"detail": "Knowledge system not found"}` (controller maps `null` payload → fix needed; see Risks §1) |
| User lacks Viewer role | `403 {"detail": "Viewer access is required."}` (via FastApiErrorMiddleware wrapping the thrown `InvalidOperationException`) |
| Store is null (contract-test path) | `200` with empty envelope (current behavior preserved) |
| Release shard missing on disk | `500 {"detail": "Internal server error"}` — `FileNotFoundException` bubbles up via the middleware |
| Release shard corrupt (N-Quads parse error) | `500 {"detail": "Internal server error"}` — same path |

## Testing

### Unit tests

**`OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs`** (new):
- `Empty_graph_returns_empty_envelope` — `BuildFromStoreAsync(nullStore, ...)` and `BuildFromNQuadsAsync([])` both return full envelope with empty arrays
- `Single_class_with_label_and_comment` — minimal graph → 1 class with all 5 fields
- `Class_with_superclasses` — `rdfs:subClassOf` triple populates `superclasses` array
- `Object_vs_data_property_split` — `owl:ObjectProperty` lands in `object_properties`, `owl:DatatypeProperty` lands in `data_properties`, `owl:Class` lands in `classes`
- `Domain_and_range_with_labels` — `rdfs:domain` / `rdfs:range` populate `domain` / `range` / `domain_label` / `range_label`
- `All_three_axiom_types` — `rdfs:subClassOf`, `owl:disjointWith`, `owl:equivalentClass` all populated
- `Labels_dict_covers_all_three_kinds` — `labels` dict includes classes, object_properties, data_properties
- `Stats_count_correct` — class/property/axiom counts match Python output for fixture graph
- `BuildFromNQuads_matches_BuildFromStore` — same content via both adapters (round-trip)

**`OnToPilot.Tests/Ontology/OntologyServiceTests.cs`** (new — extends existing):
- `GetViewAsync_returns_404_envelope_when_KS_not_found` — `null` return → controller maps to 404 (after Risks §1 fix)
- `GetViewAsync_returns_403_when_role_below_Viewer`
- `GetViewAsync_returns_full_view_for_Viewer`
- `GetViewAsync_returns_view_with_KnowledgeSystem_meta` — verifies `id` / `name` / `base_iri` populated, `release == null`

### Integration tests

**`OnToPilot.Tests/Ontology/OntologyApiTests.cs`** (new):
- `GET /api/knowledge/{ksId}/ontology_returns_full_envelope`
- `GET /api/knowledge/{ksId}/ontology_with_no_TBox_returns_empty_arrays`
- `GET /api/knowledge/{ksId}/ontology_without_auth_returns_401`
- `GET /api/knowledge/{ksId}/ontology_without_viewer_role_returns_403`

### Contract test impact

The contract-test baseline (`src/OnToPilot.ApiContract.Tests/Baselines/BackendRegression.baseline.json`)
currently expects `{"classes": [...], "properties": [...]}` (2-field
shape). After Stage 2 lands, the contract test should pass with the
new full-envelope shape. **Action**: regenerate the baseline in the
same commit that wires Stage 2 (analogous to the Guid-migration baseline
regen in commit `66b92e1`). The Stage 2 design does NOT change the
baseline contract — only the snapshot of the now-correct envelope.

The 13/160 pre-existing contract-test failures stay at 13/160
(verified by running the suite before and after the change).

## Risks

1. **`null` payload → 404 mapping** — today the dispatcher's
   `Ok(payload ?? new { ok = true })` returns `{ok: true}` for a
   not-found KS. The new `GetViewAsync(ksId, …)` returns `null`
   for not-found, which would become `{ok: true}` again. To get a
   proper 404, the dispatcher must surface "not found" before the
   controller null-coalesce. Options:
   - Throw `KeyNotFoundException` (FastApiErrorMiddleware maps to 404)
   - Throw `InvalidOperationException("Knowledge system not found")`
     and add a translation rule in the middleware
   **Decision**: throw `KeyNotFoundException`. Already used elsewhere
   in the codebase; middleware already maps it to 404 envelope.
2. **Per-request allocation cost** — `OntologyViewBuilder` is
   stateless; per-request allocation is ~1 KiB for the
   `IReadOnlyList<>` / `IReadOnlyDictionary<>` wrappers + the per-class
   records. Acceptable for HTTP request rates.
3. **N-Quads parser** — the release path needs an N-Quads parser.
   The repo already has one (`NQuadsTermWriter` for write; the read
   path is used by `StoreWrapper.LoadNQuads` via Oxigraph). For the
   release path, we want to **not** load into Oxigraph (no temp
   RocksDB per release request). A simple line-by-line N-Quads parser
   is ~80 lines (matches the canonical grammar: subject predicate
   object graph `.`). Implement inline in `OntologyViewBuilder` — no
   new module.
4. **Tuple serialization in source-gen** — `IReadOnlyList<(string,
   string)>` serializes as `[{Item1, Item2}]` under default System.Text.Json,
   but as `[{sub, super}]` with the `SnakeCaseLower` naming policy
   **only if** the tuple elements are projected as a 2-field object.
   System.Text.Json does NOT apply PropertyNamingPolicy to tuple
   `Item1` / `Item2`. **Action**: convert to anonymous-shaped
   records instead of tuples. E.g.
   `record SubclassAxiom(string Sub, string Super)` →
   wire `{"sub": "...", "super": "..."}`. Add to `OntologyAxioms`
   record definition. Three small records; keeps source-gen coverage
   clean.
5. **Conflict with frontend's `normalizeOntologyView`** — commit
   `303114b` adds a defensive normalizer that fills missing fields.
   After Stage 2 lands and the wire payload is correct, the
   normalizer becomes redundant but harmless. Keep it; remove in a
   later cleanup commit when the FastAPI contract is pinned in CI.

## Files touched (summary)

**New files** (6):
- `src/OnToPilot/Ontology/OntologyViewBuilder.cs`
- `src/OnToPilot/Ontology/PublishedOntologyService.cs`
- `src/OnToPilot/Ontology/ExternalOntologyService.cs`
- `src/OnToPilot.Tests/Ontology/OntologyViewBuilderTests.cs`
- `src/OnToPilot.Tests/Ontology/OntologyServiceTests.cs`
- `src/OnToPilot.Tests/Ontology/OntologyApiTests.cs`

**Modified files** (6):
- `src/OnToPilot.Application/Foundation/OntologyResponse.cs`
- `src/OnToPilot/Serialization/OnToPilotJsonContext.cs`
- `src/OnToPilot/Ontology/OntologyService.cs`
- `src/OnToPilot/Ontology/OntologyServiceCollectionExtensions.cs`
- `src/OnToPilot/Integration/InternalOperationDispatcher.cs`
- `src/OnToPilot.ApiContract.Tests/Baselines/BackendRegression.baseline.json` (regenerated)

Total: ~12 files, ~600–800 LOC new + ~50 LOC modified.

## Open questions

None blocking. Stage 2 ships the live path + 3 dispatcher arms + builder;
Stage 3 handles union expansion + retrieval cache invalidation + per-tenant
release-served-store optimization.