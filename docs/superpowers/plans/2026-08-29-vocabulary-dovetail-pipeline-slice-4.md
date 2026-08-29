# Slice 4: Vocabulary Pipeline (TerminologyPipeline) Dovetail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**: 把 `ExtractionOrchestrator.RunTerminologyAsync` 的 P1-4 手写链(`TerminologyService.SyncAsync` 四遍 + scoped `TerminologyAgent.SuggestAsync`)替换为 5 段 Dovetail DAG(`StaleMappingStep` → `EntitySyncStep` → `AliasStep` → `BroaderStep` → `ProposalStep`),fallback 保留 P1-4 整条链供 hand-built 测试 orchestrator 使用。

**Architecture**: `TerminologyService.SyncCore` 外科拆成 6 个 internal 成员(PrepareCarry + 4 pass + static FoldCarry),public `SyncAsync` 签名/语义零变化(整包 try/catch 保留);5 段 DOVE006 多输入段 + 2 sealed records + 1 partial pipeline class + orchestrator tail-seam ctor + scope 解析优先(Slice 3 R2 模式)。

**Tech Stack**: Dovetail 1.0.0 (NuGet + local E:\GitHub\Dovetail, source-gen) + .NET 10 + xUnit + Dovetail `AddPipelines()` 自动发现。

**Spec**: [docs/superpowers/specs/2026-08-29-vocabulary-dovetail-pipeline-slice-4-design.md](../specs/2026-08-29-vocabulary-dovetail-pipeline-slice-4-design.md)

**Predecessors**: Slice 1 (TBoxJobPipeline commit 57d1753) + Slice 2 (ABoxJobPipeline commit 250af1a) + Slice 3 (AgentChainPipeline commit b13d76c,含 R2 scope 解析 MEDIUM 修复) + P3-1 terminology proposals。

## Global Constraints

- **Dovetail 1.0.0** (NuGet + local source)
- **DOVE006**: every segment input must be pipeline input or another step's output (no bundle records)
- **Concrete step type DI** (no `IPipelineSegment<...>` factories — slice 1 F-1 lesson)
- **Tail-seam ctor** in `ExtractionOrchestrator` (no parameter reordering)
- **Scope resolution-first**(slice 3 R2 MEDIUM lesson):单例 orchestrator 不得 ctor 持有 pipeline;从 per-job scope 解析;steps 注册 **AddScoped**
- **行为零变化**:现有 `TerminologyServiceTests` + contract tests + `TerminologyAgentOrchestrationTests`(P1-4 fallback 覆盖)+ `ExtractionAgentChainTests` DAG e2e **零改动全绿**
- **TerminologyService 保持 AddSingleton + ITerminologySync forwarder**(现有注册不动)
- **QuadChangeCapture.MarkError() best-effort 保留在 orchestrator 层**(DAG 外)
- **四遍顺序执行,不并发化**(pass 间依赖,LOCKED)
- **agent 异常不吞**:ProposalStep 原样传播 → orchestrator 外层 catch → MarkError(P1-4 行为一致)
- **C# 14 / .NET 10** / nullable enabled
- **RTK** for git operations (user preference)
- **Co-Authored-By: Claude <noreply@anthropic.com>** trailer on every commit
- **Main branch direct landing** (slice 1 precedent)

---

## File Structure

| Layer | File | Responsibility |
|------|------|----------------|
| Records | `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyInputs.cs` | `TerminologyInput` + `TermSyncCarry`(spec §4 verbatim) |
| Service split | `src/ISEStudio/Extraction/TerminologyService.cs` (modify) | SyncCore 拆 6 internal 成员,SyncAsync body 重写 |
| Step 1 | `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/StaleMappingStep.cs` | 1 input → carry;PrepareCarry + Pass 1 + try/catch |
| Step 2 | `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/EntitySyncStep.cs` | 2 inputs → carry;Pass 2 |
| Step 3 | `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/AliasStep.cs` | 2 inputs → carry;Pass 3 |
| Step 4 | `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/BroaderStep.cs` | 2 inputs → carry;Pass 4 |
| Step 5 | `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/ProposalStep.cs` | 2 inputs → `TerminologyResult`;gating + agent 搬移 + FoldCarry |
| Pipeline | `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyPipeline.cs` | partial class with 5 `[Segment]` ctor params |
| DI | `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` (modify) | append 5 step registrations (AddScoped) |
| Orchestrator | `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` (modify) | `_terminologyPipeline` field + ctor tail param + `RunTerminologyAsync` body 替换 + 新增 helper;`RunTerminologyAgentAsync` **保留** |
| Tests | 8 new test files | record/pass-step/proposal-step/pipeline/DI/orchestrator/e2e coverage |

---

## Task Decomposition

8 tasks:

| Task | Deliverable | Commit pattern |
|------|-------------|----------------|
| 1 | 2 records + 2 tests (953/0/1/954) | `feat(extraction): add Terminology Dovetail job I/O records (2 records, 2 tests)` |
| 2 | TerminologyService 拆分(0 new tests,gate = 现有全绿 953/0/1/954) | `refactor(extraction): split TerminologyService SyncCore into carry + 4 pass members` |
| 3 | 4 pass step classes + 8 tests (961/0/1/962) | `feat(extraction): add Terminology Dovetail 4 pass step classes (8 tests)` |
| 4 | ProposalStep + 3 tests (964/0/1/965) | `feat(extraction): add Dovetail Terminology ProposalStep (P3-1 agent folding)` |
| 5 | TerminologyPipeline partial + 1 emit test (965/0/1/966) | `feat(extraction): add Dovetail TerminologyPipeline (5-stage DAG)` |
| 6 | DI registrations + 4 tests (969/0/1/970) | `feat(extraction): wire TerminologyPipeline into DI` |
| 7 | orchestrator 接线 + 3 tests (972/0/1/973) | `feat(extraction): wire TerminologyPipeline into RunTerminologyAsync` |
| 8 | dovetail-report HTML | `docs(extraction): add Dovetail Terminology sub-DAG HTML report` |

Baseline: 951 / 0 / 1 / 952 → final: 972 / 0 / 1 / 973(21 new)。

---

### Task 1: Terminology Dovetail job I/O records (2 records + 2 tests)

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyInputs.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyInputsTests.cs`

**Interfaces:**
- Consumes: spec §4 verbatim record definitions;`KsContext` / `OntologyView` / `SkosView` from `ISEStudio.Ontology`
- Produces: `TerminologyInput`, `TermSyncCarry` types (consumed by Tasks 2-7)

- [ ] **Step 1: Write the failing tests**

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyInputsTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology;

public class TerminologyInputsTests
{
    [Fact]
    public void TerminologyInput_EmptyConstruction_RoundTrips()
    {
        var input = new TerminologyInput(
            Ks: new KsContext("http://g/ks", "http://g/ks/onto#"),
            KnowledgeSystemId: Guid.Empty,
            Model: null,
            SuggestEnabled: false);

        Assert.Equal(Guid.Empty, input.KnowledgeSystemId);
        Assert.Null(input.Model);
        Assert.False(input.SuggestEnabled);
        Assert.Equal("http://g/ks/onto#", input.Ks.BaseIri);
        Assert.Equal("http://g/ks", input.Ks.TBoxGraph);
    }

    [Fact]
    public void TermSyncCarry_DefaultConstruction_AllZero()
    {
        var carry = new TermSyncCarry(null, null, null, 0);

        Assert.Null(carry.SchemeIri);
        Assert.Null(carry.View);
        Assert.Null(carry.PreView);
        Assert.Equal(0, carry.PropertyCount);
        Assert.Equal(0, carry.StaleMappingsRemoved);
        Assert.Equal(0, carry.TermsAdded);
        Assert.Equal(0, carry.TermsMapped);
        Assert.Equal(0, carry.MappingConflicts);
        Assert.Equal(0, carry.AliasesAdded);
        Assert.Equal(0, carry.BroaderAdded);
        Assert.Null(carry.Error);
        Assert.False(carry.Skipped);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TerminologyInputsTests" --nologo`
Expected: FAIL with `CS0234`(namespace `ISEStudio.Extraction.Dovetail.Terminology` 不存在)

- [ ] **Step 3: Write minimal implementation**

Create `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyInputs.cs`(spec §4 verbatim):

```csharp
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.Terminology;

/// <summary>
/// Input to the terminology Dovetail pipeline. <c>Ks</c> is the pure-value
/// context (graph IRIs) the deterministic passes need; the knowledge-system
/// id and model flow to the LLM proposal pass; <c>SuggestEnabled</c> is the
/// operator switch (ISEStudioOptions.TerminologySuggestDuringExtraction)
/// folded at the orchestrator — the pipeline itself stays option-free.
/// </summary>
public sealed record TerminologyInput(
    KsContext Ks,
    Guid KnowledgeSystemId,
    string? Model,
    bool SuggestEnabled);

/// <summary>
/// Per-pass carry record threading the SyncCore state through the DAG
/// (parent-spec D3: one record per segment output). <c>View</c> is the TBox
/// snapshot (classes/properties — passes 2-4 read it); <c>PreView</c> is the
/// vocabulary SKOS view captured by the init step (pass 1 iterates it, pass 2
/// builds its conceptByMapping index from it). The per-pass counters
/// accumulate; <c>Error</c> is set by a pass step's catch and makes every
/// downstream step short-circuit (mirrors SyncAsync's whole-pass try/catch).
/// <c>Skipped</c> marks the zero paths where no view can be built
/// (<c>_store</c> null — contract-test path — or an empty ontology), in which
/// case <c>View</c>/<c>PreView</c> are null; <see cref="TerminologyService.FoldCarry"/>
/// restores the original <see cref="TerminologyResult.Zero"/> shape from it.
/// </summary>
public sealed record TermSyncCarry(
    string? SchemeIri,
    OntologyView? View,
    SkosView? PreView,
    int PropertyCount,
    int StaleMappingsRemoved = 0,
    int TermsAdded = 0,
    int TermsMapped = 0,
    int MappingConflicts = 0,
    int AliasesAdded = 0,
    int BroaderAdded = 0,
    string? Error = null,
    bool Skipped = false);
```

NOTE: The `<see cref="TerminologyService.FoldCarry"/>` reference resolves only after Task 2 lands — a broken cref is a compiler WARNING (not error). If the build treats warnings as errors, replace the cref text with plain text `FoldCarry` in Task 1 and restore the cref in Task 2. Document which happened in the task report.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TerminologyInputsTests" --nologo`
Expected: `Passed: 2, Failed: 0`

- [ ] **Step 5: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 953, Failed: 0, Skipped: 1, Total: 954`(951 + 2)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyInputs.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyInputsTests.cs
git commit -m "feat(extraction): add Terminology Dovetail job I/O records (2 records, 2 tests)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: TerminologyService SyncCore 拆分 (0 new tests, gate = 现有全绿)

**Files:**
- Modify: `src/ISEStudio/Extraction/TerminologyService.cs` (lines 114-411: `SyncAsync` + `SyncCore` 替换;顶部加 1 行 using)

**Interfaces:**
- Consumes: `TermSyncCarry` (Task 1)
- Produces: `internal TermSyncCarry PrepareCarry(KsContext ks, CancellationToken cancellationToken)`、`internal TermSyncCarry PassStaleMappings(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)`、`internal TermSyncCarry PassEntitySync(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)`、`internal TermSyncCarry PassAliasAdditions(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)`、`internal TermSyncCarry PassBroaderAdditions(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)`、`internal static TerminologyResult FoldCarry(TermSyncCarry carry)`(Tasks 3-7 使用)

**CRITICAL — 本任务是整个 slice 最重的一块。拆分必须逐行搬移;搬移完成后现有 `TerminologyServiceTests`(约 17 tests,覆盖四遍行为)全绿 = 拆分正确性证据。**

- [ ] **Step 1: Add using**

In `src/ISEStudio/Extraction/TerminologyService.cs`, after the existing usings add:

```csharp
using ISEStudio.Extraction.Dovetail.Terminology;
```

- [ ] **Step 2: Replace `SyncAsync` + `SyncCore` (current lines 114-411) with the split members**

Find `public TerminologyResult SyncAsync(...)` through the end of `private TerminologyResult SyncCore(...)` (the method that ends right before the `EnumerateEntities` XML doc). Replace that whole region with:

```csharp
    /// <summary>Run one sync pass against the TBox graph and vocabulary graph.</summary>
    /// <remarks>
    /// <para>The body sequences the four deterministic passes through the
    /// internal carry members below; the Dovetail terminology pipeline
    /// (<see cref="Dovetail.Terminology.TerminologyPipeline"/>) runs the
    /// same members as segments. Both paths fold through
    /// <see cref="FoldCarry"/>, so the public shape is identical.</para>
    /// </remarks>
    public TerminologyResult SyncAsync(KsContext ks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ks);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var carry = PrepareCarry(ks, cancellationToken);
            carry = PassStaleMappings(ks, carry, cancellationToken);
            carry = PassEntitySync(ks, carry, cancellationToken);
            carry = PassAliasAdditions(ks, carry, cancellationToken);
            carry = PassBroaderAdditions(ks, carry, cancellationToken);
            return FoldCarry(carry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TerminologyResult(0, 0, 0, ex.Message, null);
        }
    }

    /// <summary>
    /// Build the shared state the four deterministic passes read: the TBox
    /// view, the resolved default ConceptScheme, and the vocabulary SKOS
    /// pre-view. Returns a <c>Skipped</c> carry on the zero paths where no
    /// view can be built (<c>_store</c> null — contract-test path) or the
    /// ontology has no entities; <see cref="FoldCarry"/> restores the
    /// original <see cref="TerminologyResult"/> shape from it.
    /// </summary>
    internal TermSyncCarry PrepareCarry(KsContext ks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_store is null)
        {
            // No graph store wired (contract-test path) — vocabulary
            // layer has nothing to scan or write, so report the
            // deterministic zero summary.
            return new TermSyncCarry(null, null, null, 0, Skipped: true);
        }
        var view = SchemaBuilder.BuildView(ks.TBoxGraph, _store);

        // Python parity: `entities = classes + object_properties + data_properties`.
        // The property count surfaces separately in the audit row so reviewers
        // can spot TBoxes that have properties without classes (or vice versa)
        // without re-querying the schema builder.
        var ontologyIris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in view.Classes) ontologyIris.Add(c.Iri);
        foreach (var p in view.ObjectProperties) ontologyIris.Add(p.Iri);
        foreach (var p in view.DataProperties) ontologyIris.Add(p.Iri);
        var propertyCount = view.ObjectProperties.Count + view.DataProperties.Count;

        if (ontologyIris.Count == 0)
            return new TermSyncCarry(null, view, null, propertyCount, Skipped: true);

        // Ensure a default ConceptScheme exists so the vocabulary view has a
        // scheme to anchor the concepts this pass creates. Mirrors the Python
        // backend's ensure_scheme(): reuse the fixed "#scheme-extracted" IRI
        // (or the single / extraction / most-mapped existing scheme), and
        // create it when the vocabulary graph has none yet. Without this a
        // freshly-extracted knowledge system reports scheme_count=0 with a
        // fully-populated concept list, leaving the "New term" button
        // permanently disabled (empty selectedSchemeIri).
        var schemeIri = EnsureScheme(ks, view);

        var skos = new SkosManager(_store);
        var preView = skos.BuildView(ks);
        return new TermSyncCarry(schemeIri, view, preView, propertyCount);
    }

    /// <summary>
    /// Pass 1 — stale mappings. Mirrors Python `terminology_sync.sync_from_ontology`
    /// lines 122-130: any concept whose `mappedEntityIri` no longer exists in
    /// either the ontology or the ABox gets its mapping cleared (but the
    /// concept row itself is preserved so a human can remap or deprecate it).
    /// `valid_mapping_iris = ontology_iris | abox_iris` — the ABox half reads
    /// the subject set of the `…/abox` named graph (every instance IRI),
    /// mirroring `store.read_triples(abox_iri)`. <c>ontologyIris</c> is
    /// rebuilt here from the carry's view (pure traversal of the snapshot the
    /// init step captured — identical result to the original monolith).
    /// </summary>
    internal TermSyncCarry PassStaleMappings(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)
    {
        if (carry.Skipped || carry.Error is not null || carry.SchemeIri is null) return carry;

        var view = carry.View!;
        var ontologyIris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in view.Classes) ontologyIris.Add(c.Iri);
        foreach (var p in view.ObjectProperties) ontologyIris.Add(p.Iri);
        foreach (var p in view.DataProperties) ontologyIris.Add(p.Iri);

        var aboxIris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in _store.Match(graph: new OntoNamedNode(ks.ABoxGraph)))
        {
            if (q.Subject is OntoNamedNode n) aboxIris.Add(n.Value);
        }
        var validMappingIris = new HashSet<string>(ontologyIris, StringComparer.Ordinal);
        validMappingIris.UnionWith(aboxIris);

        var staleMappingsRemoved = 0;
        var preView = carry.PreView!;
        foreach (var concept in preView.Concepts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(concept.MappedEntityIri)) continue;
            if (validMappingIris.Contains(concept.MappedEntityIri!)) continue;
            // The mapping target no longer exists in the ontology or ABox;
            // preserve the concept but drop the op:mapsTo triple so the
            // reviewer can decide (remap or deprecate). Python's
            // update_concept rewrites the whole concept payload; the
            // minimal RemoveQuads here produces the same final graph state
            // without the round-trip.
            var stale = _store.Match(
                subjectIri: concept.Iri,
                predicateIri: SkosVocab.OpMapsTo.Value,
                graphIri: ks.VocabularyGraph);
            if (stale.Count > 0) _store.RemoveQuads(new OntoNamedNode(ks.VocabularyGraph), stale);
            staleMappingsRemoved++;
        }

        return carry with { StaleMappingsRemoved = staleMappingsRemoved };
    }

    /// <summary>
    /// Pass 2 — entity sync. Python decision tree per entity (mirrors
    /// `terminology_sync`):
    /// <list type="number">
    /// <item><c>concept_by_mapping[iri]</c> exists → entity already has a
    /// mapped concept; nothing to create, the alias pass below attaches the
    /// entity label if it's missing.</item>
    /// <item>the entity's label is owned by a mapped concept pointing at a
    /// different IRI → <c>mapping_conflicts += 1; continue</c>.</item>
    /// <item>the entity's label exists as a pref-label on an unmapped
    /// concept → map that concept onto the entity (<c>terms_mapped</c>).</item>
    /// <item>otherwise create a fresh mapped concept (<c>terms_added</c> and
    /// <c>terms_mapped</c>).</item>
    /// </list>
    /// <c>conceptByMapping</c> mirrors Python's <c>concept_by_mapping</c> dict;
    /// <c>mappedIndex</c> mirrors the mapped subset of <c>label_owner</c>
    /// (via MappedAliases). Both are refreshed after each create so a
    /// re-encountered entity in the same pass sees the new state.
    /// </summary>
    internal TermSyncCarry PassEntitySync(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)
    {
        if (carry.Skipped || carry.Error is not null || carry.SchemeIri is null) return carry;

        var view = carry.View!;
        var preView = carry.PreView!;
        var schemeIri = carry.SchemeIri!;

        var skos = new SkosManager(_store);
        var conceptByMapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var mappedIndex = new Dictionary<string, string>(skos.MappedAliases(ks), StringComparer.Ordinal);
        foreach (var c in preView.Concepts)
        {
            if (!string.IsNullOrEmpty(c.MappedEntityIri))
                conceptByMapping[c.MappedEntityIri!] = c.Iri;
        }

        var graph = new OntoNamedNode(ks.VocabularyGraph);
        var now = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var added = 0;
        var mapped = 0;
        var mappingConflicts = 0;

        // Iterate classes first (the order the previous version of this
        // method used), then properties — Python aggregates them in the same
        // order via `dict(entity, entity_kind="class"|"object_property"|...)`.
        foreach (var entity in EnumerateEntities(view))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (iri, label) = entity;
            var normalized = Vocabulary.NormLabel(label);
            if (normalized.Length == 0) continue;

            // Branch 1: entity already has a mapped concept.
            if (conceptByMapping.ContainsKey(iri)) continue;

            // Branch 2: label owned by a mapped concept at a different IRI.
            if (mappedIndex.TryGetValue(normalized, out _))
            {
                mappingConflicts++;
                continue;
            }

            // Branch 3: label exists as an unmapped pref-label → adopt it.
            var existingConcept = FindConceptIriByPrefLabel(ks, label);
            if (existingConcept is not null)
            {
                _store.AddQuads(graph, new[]
                {
                    new Oxigraph.Quad(
                        new OntoNamedNode(existingConcept),
                        SkosVocab.OpMapsTo,
                        new OntoNamedNode(iri),
                        graph),
                });
                mapped++;
                mappedIndex[normalized] = iri;
                conceptByMapping[iri] = existingConcept;
                continue;
            }

            // Branch 4: fresh mapped concept. The label language mirrors
            // Python's `_language(label)` CJK heuristic ("zh-CN" for CJK
            // labels, "en" otherwise) so Chinese TBoxes mint Chinese
            // pref labels.
            var concept = new OntoNamedNode($"{ks.VocabularyGraph}#concept-{LocalName(iri)}");
            _store.AddQuads(graph, new[]
            {
                new Oxigraph.Quad(concept, Vocabulary.RdfType, SkosVocab.Concept, graph),
                new Oxigraph.Quad(concept, SkosVocab.InScheme, new OntoNamedNode(schemeIri), graph),
                new Oxigraph.Quad(concept, SkosVocab.PrefLabel, new OntoLiteral(label, ContainsCjk(label) ? "zh-CN" : "en"), graph),
                new Oxigraph.Quad(concept, SkosVocab.OpStatus, new OntoLiteral("active"), graph),
                new Oxigraph.Quad(concept, SkosVocab.OpMapsTo, new OntoNamedNode(iri), graph),
                new Oxigraph.Quad(concept, SkosVocab.DcCreated, new OntoLiteral(now), graph),
            });
            // Python parity: a fresh concept is both `terms_added` and
            // `terms_mapped` (its mapping exists by construction).
            added++;
            mapped++;
            mappedIndex[normalized] = iri;
            conceptByMapping[iri] = concept.Value;
        }

        return carry with
        {
            TermsAdded = added,
            TermsMapped = mapped,
            MappingConflicts = mappingConflicts,
        };
    }

    /// <summary>
    /// Pass 3 — alias additions. For every mapped concept, ensure its
    /// entity's normalised label is attached as at least one of
    /// <c>pref_labels</c> / <c>alt_labels</c> / <c>hidden_labels</c>. Mirrors
    /// Python's <c>result["aliases_added"] += 1</c> increment after the
    /// <c>existing_keys / label_owner</c> dedup loop.
    /// </summary>
    /// <remarks>
    /// <para>Python parity notes:</para>
    /// <list type="bullet">
    /// <item><c>label_owner</c> contains only labels of concepts in the
    /// resolved scheme, so an alias that another concept in the SAME scheme
    /// already owns is skipped (<c>key not in label_owner</c>).</item>
    /// <item>Python rewrites the whole concept via update_concept; we add the
    /// single <c>skos:altLabel</c> triple directly. Final graph state is
    /// identical and the minimal write avoids SkosManager's
    /// single-prefLabel round-trip (which would drop extra-language
    /// pref labels on concepts this sync did not create).</item>
    /// </list>
    /// </remarks>
    internal TermSyncCarry PassAliasAdditions(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)
    {
        if (carry.Skipped || carry.Error is not null || carry.SchemeIri is null) return carry;

        var view = carry.View!;
        var schemeIri = carry.SchemeIri!;

        var skos = new SkosManager(_store);
        var aliasesAdded = 0;
        var postView = skos.BuildView(ks);
        var labelOwners = new HashSet<(string Norm, string Lang)>(NormLangOrdinalComparer.Instance);
        foreach (var c in postView.Concepts)
        {
            if (c.SchemeIri != schemeIri) continue;
            foreach (var l in c.PrefLabels.Concat(c.AltLabels).Concat(c.HiddenLabels))
                labelOwners.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
        }
        var entityIndex = BuildEntityIndex(view);
        foreach (var concept in postView.Concepts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(concept.MappedEntityIri)) continue;
            if (!entityIndex.TryGetValue(concept.MappedEntityIri!, out var entity)) continue;
            var (label, lang) = entity;
            var key = (Vocabulary.NormLabel(label), lang.ToLowerInvariant());
            var existing = new HashSet<(string Norm, string Lang)>(NormLangOrdinalComparer.Instance);
            foreach (var l in concept.PrefLabels)
                existing.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
            foreach (var l in concept.AltLabels)
                existing.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
            foreach (var l in concept.HiddenLabels)
                existing.Add((Vocabulary.NormLabel(l.Value), l.Language.ToLowerInvariant()));
            if (existing.Contains(key)) continue;
            if (labelOwners.Contains(key)) continue;
            _store.AddQuads(new OntoNamedNode(ks.VocabularyGraph), new[]
            {
                new Oxigraph.Quad(
                    new OntoNamedNode(concept.Iri),
                    SkosVocab.AltLabel,
                    new OntoLiteral(label, lang),
                    new OntoNamedNode(ks.VocabularyGraph)),
            });
            aliasesAdded++;
            // Python refreshes `label_owner[key] = concept` after each
            // alias write so a second concept mapped to the same entity
            // does not attach the same label again.
            labelOwners.Add(key);
        }

        return carry with { AliasesAdded = aliasesAdded };
    }

    /// <summary>
    /// Pass 4 — broader additions. For every class with a superclass
    /// relation, add the corresponding mapped parent concept's IRI to its
    /// <c>skos:broader</c> set (mirrors Python
    /// <c>result["broader_added"] += len(additions)</c>). Relations spanning
    /// different schemes, self-loops, and already-present entries are
    /// skipped. Python funnels the whole batch through update_concept (cycle
    /// check); we add each triple directly because the same-scheme +
    /// non-self filters above already exclude every relation the SKOS
    /// validator would reject except a cycle, which the schema builder's
    /// subclass view does not produce.
    /// </summary>
    internal TermSyncCarry PassBroaderAdditions(KsContext ks, TermSyncCarry carry, CancellationToken cancellationToken)
    {
        if (carry.Skipped || carry.Error is not null || carry.SchemeIri is null) return carry;

        var view = carry.View!;

        var skos = new SkosManager(_store);
        var broaderAdded = 0;
        var finalView = skos.BuildView(ks);
        foreach (var cls in view.Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cls.Superclasses.Count == 0) continue;
            var concept = finalView.Concepts.FirstOrDefault(c => c.MappedEntityIri == cls.Iri);
            if (concept is null) continue;
            var additions = new List<string>();
            foreach (var parentIri in cls.Superclasses)
            {
                var parentConcept = finalView.Concepts.FirstOrDefault(c => c.MappedEntityIri == parentIri);
                if (parentConcept is null) continue;
                if (parentConcept.SchemeIri != concept.SchemeIri) continue;
                if (parentConcept.Iri == concept.Iri) continue;
                if (concept.Broader.Contains(parentConcept.Iri, StringComparer.Ordinal)) continue;
                additions.Add(parentConcept.Iri);
            }
            if (additions.Count == 0) continue;
            var broaderQuads = new List<Oxigraph.Quad>(additions.Count);
            foreach (var parent in additions)
            {
                broaderQuads.Add(new Oxigraph.Quad(
                    new OntoNamedNode(concept.Iri),
                    SkosVocab.Broader,
                    new OntoNamedNode(parent),
                    new OntoNamedNode(ks.VocabularyGraph)));
            }
            _store.AddQuads(new OntoNamedNode(ks.VocabularyGraph), broaderQuads);
            broaderAdded += additions.Count;
        }

        return carry with { BroaderAdded = broaderAdded };
    }

    /// <summary>
    /// Fold the carry into the public result shape. Shared by
    /// <see cref="SyncAsync"/> (legacy whole-sync path) and the Dovetail
    /// <see cref="Dovetail.Terminology.Steps.ProposalStep"/> so both paths
    /// produce identical <see cref="TerminologyResult"/> shapes:
    /// <list type="bullet">
    /// <item><c>Skipped</c> + no error → <see cref="TerminologyResult.Zero"/>
    /// (the <c>_store</c>-null / empty-ontology short circuits);</item>
    /// <item><c>Skipped</c> + error → <c>(0, 0, 0, Error, null)</c> (the
    /// <see cref="SyncAsync"/> catch shape);</item>
    /// <item>otherwise → the counter summary with <c>ProposalsQueued: 0</c>
    /// (the proposal count is added by <see cref="Dovetail.Terminology.Steps.ProposalStep"/>).</item>
    /// </list>
    /// </summary>
    internal static TerminologyResult FoldCarry(TermSyncCarry carry)
    {
        if (carry.Skipped)
        {
            return carry.Error is null
                ? TerminologyResult.Zero
                : new TerminologyResult(0, 0, 0, carry.Error, null);
        }

        return new TerminologyResult(
            TermsAdded: carry.TermsAdded,
            TermsMapped: carry.TermsMapped,
            ProposalsQueued: 0,
            Error: carry.Error,
            SchemeIri: carry.SchemeIri,
            Properties: carry.PropertyCount,
            AliasesAdded: carry.AliasesAdded,
            BroaderAdded: carry.BroaderAdded,
            StaleMappingsRemoved: carry.StaleMappingsRemoved,
            MappingConflicts: carry.MappingConflicts);
    }
```

NOTE: Do NOT touch anything below this region — `EnumerateEntities`, `BuildEntityIndex`, `EnsureScheme`, `SchemeTitle`, `ContainsCjk`, `FindConceptIriByPrefLabel`, `LocalName`, `NormLangOrdinalComparer` all stay exactly as they are.

- [ ] **Step 3: Build**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --nologo`
Expected: 0 errors / 0 warnings. Fix any transcription slip (e.g., a pass body referencing a variable that was not moved with it).

- [ ] **Step 4: Run full suite to verify no regression (THE GATE)**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 953, Failed: 0, Skipped: 1, Total: 954`(Task 1 的 2 个新 test + 现有 951 全绿 — 现有 `TerminologyServiceTests` 全绿是拆分正确性证据)

- [ ] **Step 5: Commit**

```bash
git add src/ISEStudio/Extraction/TerminologyService.cs
git commit -m "refactor(extraction): split TerminologyService SyncCore into carry + 4 pass members

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: 4 pass step classes + 8 tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/StaleMappingStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/EntitySyncStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/AliasStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/BroaderStep.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/StaleMappingStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/EntitySyncStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/AliasStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/BroaderStepTests.cs`

**Interfaces:**
- Consumes: `TerminologyInput` + `TermSyncCarry` (Task 1), `TerminologyService` internal pass members (Task 2)
- Produces: 4 step classes implementing `IPipelineSegment<...>`(Task 5 pipeline + Task 6 DI 使用)

**行为契约(spec §5 D5)**:每个 pass step 的 `ExecuteAsync`:`catch (OperationCanceledException) { throw; } catch (Exception ex) { return new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true); }`。短路判定在 pass 方法内部(不重复);step 层只做 try/catch。

- [ ] **Step 1: Write 8 failing tests**

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/StaleMappingStepTests.cs`:

```csharp
using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Ontology;
using ISEStudio.Tests.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology.Steps;

public class StaleMappingStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public StaleMappingStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step1",
            BaseIri: "http://goodcrew.local/ks/test/term-step1/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void ExecuteAsync_RemovesStaleMappingsAndSeedsCarry()
    {
        // First sync maps Pump + Motor; then the TBox shrinks to Pump only.
        // The init half of the step builds the carry; the pass half must
        // clear the Motor concept's op:mapsTo triple (stale_mappings_removed
        // == 1) exactly like the Sync_clears_stale_mappings whole-sync test.
        SeedClasses("Pump", "Motor");
        var svc = new TerminologyService(_fx.Store);
        svc.SyncAsync(_ks, CancellationToken.None);

        ReplaceTBox("Pump");

        var step = new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance);
        var carry = step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            CancellationToken.None);

        Assert.Null(carry.Error);
        Assert.Equal(1, carry.StaleMappingsRemoved);
        Assert.Equal($"{_ks.VocabularyGraph}#scheme-extracted", carry.SchemeIri);
        Assert.NotNull(carry.View);
        Assert.NotNull(carry.PreView);
        Assert.Equal(0, carry.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        var motor = view.Concepts.Single(c => c.DisplayLabel == "Motor");
        Assert.Null(motor.MappedEntityIri);
    }

    [Fact]
    public void ExecuteAsync_NullKs_ReturnsErrorCarry()
    {
        // PrepareCarry dereferences the KsContext — a null one throws
        // inside the step, which must convert it to an Error carry (D5)
        // instead of propagating.
        var svc = new TerminologyService(_fx.Store);
        var step = new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance);

        var carry = step.ExecuteAsync(
            new TerminologyInput(null!, Guid.NewGuid(), null, false),
            CancellationToken.None);

        Assert.NotNull(carry.Error);
        Assert.True(carry.Skipped);
        Assert.Null(carry.SchemeIri);
    }

    private void SeedClasses(params string[] labels) =>
        SeedMutation(
            classes: labels,
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: Array.Empty<AxiomMutation>());

    private void SeedMutation(
        IReadOnlyList<string> classes,
        IReadOnlyList<string> objectProperties,
        IReadOnlyList<string> dataProperties,
        IReadOnlyList<AxiomMutation> axioms)
    {
        var mutation = new OntologyMutation(
            Classes: classes.Select(l => new ClassMutation(l)).ToArray(),
            ObjectProperties: objectProperties.Select(l => new PropertyMutation(l, "object")).ToArray(),
            DataProperties: dataProperties.Select(l => new PropertyMutation(l, "data")).ToArray(),
            Axioms: axioms);
        var quads = SchemaBuilder.BuildMutation(_ks.BaseIri, mutation, _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), quads);
    }

    private void ReplaceTBox(params string[] labels)
    {
        var existing = _fx.Store.Match(graphIri: _ks.TBoxGraph);
        if (existing.Count > 0)
        {
            _fx.Store.RemoveQuads(new OntoNamedNode(_ks.TBoxGraph), existing);
        }
        SeedClasses(labels);
    }
}
```

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/EntitySyncStepTests.cs`:

```csharp
using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Ontology;
using ISEStudio.Tests.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology.Steps;

public class EntitySyncStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public EntitySyncStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step2",
            BaseIri: "http://goodcrew.local/ks/test/term-step2/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void ExecuteAsync_CreatesMappedConceptsAndCounts()
    {
        SeedClasses("Pump", "Motor");
        var svc = new TerminologyService(_fx.Store);
        var input = new TerminologyInput(_ks, Guid.NewGuid(), null, false);
        var init = new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance)
            .ExecuteAsync(input, CancellationToken.None);

        var step = new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance);
        var carry = step.ExecuteAsync(input, init, CancellationToken.None);

        Assert.Null(carry.Error);
        Assert.Equal(2, carry.TermsAdded);
        Assert.Equal(2, carry.TermsMapped);
        Assert.Equal(0, carry.MappingConflicts);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        Assert.Equal(2, view.Stats.ConceptCount);
        Assert.Equal(2, view.Stats.MappedCount);
    }

    [Fact]
    public void ExecuteAsync_MalformedCarry_ReturnsErrorCarry()
    {
        // SchemeIri non-null passes the guard; the null View then throws
        // inside the pass — the step must convert that to an Error carry
        // (D5) instead of propagating. (Inducing a real store exception is
        // nondeterministic on Windows — Oxigraph handle behavior — so the
        // catch contract is pinned with a synthetic throw.)
        var svc = new TerminologyService(_fx.Store);
        var step = new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance);
        var malformed = new TermSyncCarry("http://x/scheme", null, null, 0);

        var carry = step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            malformed,
            CancellationToken.None);

        Assert.NotNull(carry.Error);
        Assert.True(carry.Skipped);
        Assert.Null(carry.SchemeIri);
    }

    // SeedClasses / SeedMutation helpers — identical to StaleMappingStepTests.
    private void SeedClasses(params string[] labels) =>
        SeedMutation(
            classes: labels,
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: Array.Empty<AxiomMutation>());

    private void SeedMutation(
        IReadOnlyList<string> classes,
        IReadOnlyList<string> objectProperties,
        IReadOnlyList<string> dataProperties,
        IReadOnlyList<AxiomMutation> axioms)
    {
        var mutation = new OntologyMutation(
            Classes: classes.Select(l => new ClassMutation(l)).ToArray(),
            ObjectProperties: objectProperties.Select(l => new PropertyMutation(l, "object")).ToArray(),
            DataProperties: dataProperties.Select(l => new PropertyMutation(l, "data")).ToArray(),
            Axioms: axioms);
        var quads = SchemaBuilder.BuildMutation(_ks.BaseIri, mutation, _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), quads);
    }
}
```

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/AliasStepTests.cs`:

```csharp
using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Ontology;
using ISEStudio.Tests.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology.Steps;

public class AliasStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public AliasStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step3",
            BaseIri: "http://goodcrew.local/ks/test/term-step3/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void ExecuteAsync_AttachesEntityLabelAsAlias()
    {
        // Mirrors Sync_adds_entity_label_as_alias_when_pref_label_differs:
        // a manually-curated concept is mapped to Pump but its pref label
        // is "Fluid Mover" — the alias pass must attach "Pump" as an
        // skos:altLabel without touching the curated pref label.
        SeedClasses("Pump");
        var manager = new SkosManager(_fx.Store);
        SeedDefaultScheme(manager);
        var pumpIri = $"{_ks.BaseIri}Pump";
        manager.CreateConcept(_ks,
            $"{_ks.VocabularyGraph}#scheme-extracted",
            new SkosConceptData(
                Iri: $"{_ks.VocabularyGraph}#concept-FluidMover",
                PrefLabel: "Fluid Mover",
                Language: "en",
                MappedEntityIri: pumpIri));

        var svc = new TerminologyService(_fx.Store);
        var input = new TerminologyInput(_ks, Guid.NewGuid(), null, false);
        var init = new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance)
            .ExecuteAsync(input, CancellationToken.None);
        var synced = new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance)
            .ExecuteAsync(input, init, CancellationToken.None);

        var step = new AliasStep(svc, NullLogger<AliasStep>.Instance);
        var carry = step.ExecuteAsync(input, synced, CancellationToken.None);

        Assert.Null(carry.Error);
        Assert.Equal(1, carry.AliasesAdded);
        Assert.Equal(0, carry.TermsAdded);

        var view = manager.BuildView(_ks);
        var concept = view.Concepts.Single(c => c.MappedEntityIri == pumpIri);
        Assert.Equal("Fluid Mover", concept.DisplayLabel);
        var alias = Assert.Single(concept.AltLabels);
        Assert.Equal("Pump", alias.Value);
    }

    [Fact]
    public void ExecuteAsync_MalformedCarry_ReturnsErrorCarry()
    {
        // Same synthetic-throw pin as EntitySyncStepTests: SchemeIri
        // non-null passes the guard, the null View throws inside the
        // pass, and the step converts it to an Error carry (D5).
        var svc = new TerminologyService(_fx.Store);
        var step = new AliasStep(svc, NullLogger<AliasStep>.Instance);
        var malformed = new TermSyncCarry("http://x/scheme", null, null, 0);

        var carry = step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            malformed,
            CancellationToken.None);

        Assert.NotNull(carry.Error);
        Assert.True(carry.Skipped);
        Assert.Null(carry.SchemeIri);
    }

    private void SeedDefaultScheme(SkosManager manager) =>
        manager.CreateScheme(_ks, new SkosSchemeData(
            Iri: $"{_ks.VocabularyGraph}#scheme-extracted",
            Title: "Step tests terminology",
            DefaultLanguage: "en",
            Origin: "extraction"));

    // SeedClasses / SeedMutation helpers — identical to StaleMappingStepTests.
    private void SeedClasses(params string[] labels) =>
        SeedMutation(
            classes: labels,
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: Array.Empty<AxiomMutation>());

    private void SeedMutation(
        IReadOnlyList<string> classes,
        IReadOnlyList<string> objectProperties,
        IReadOnlyList<string> dataProperties,
        IReadOnlyList<AxiomMutation> axioms)
    {
        var mutation = new OntologyMutation(
            Classes: classes.Select(l => new ClassMutation(l)).ToArray(),
            ObjectProperties: objectProperties.Select(l => new PropertyMutation(l, "object")).ToArray(),
            DataProperties: dataProperties.Select(l => new PropertyMutation(l, "data")).ToArray(),
            Axioms: axioms);
        var quads = SchemaBuilder.BuildMutation(_ks.BaseIri, mutation, _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), quads);
    }
}
```

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/BroaderStepTests.cs`:

```csharp
using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Ontology;
using ISEStudio.Tests.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology.Steps;

public class BroaderStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public BroaderStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step4",
            BaseIri: "http://goodcrew.local/ks/test/term-step4/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void ExecuteAsync_SeedsBroaderFromSubclassRelations()
    {
        // Mirrors Sync_seeds_broader_from_subclass_relations:
        // "Centrifugal Pump" subclasses "Pump" — the broader pass must add
        // a skos:broader triple on the child pointing at the parent concept.
        SeedMutation(
            classes: new[] { "Pump", "Centrifugal Pump" },
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: new[] { new AxiomMutation("subclass", Sub: "Centrifugal Pump", Super: "Pump") });

        var svc = new TerminologyService(_fx.Store);
        var input = new TerminologyInput(_ks, Guid.NewGuid(), null, false);
        var init = new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance)
            .ExecuteAsync(input, CancellationToken.None);
        var synced = new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance)
            .ExecuteAsync(input, init, CancellationToken.None);
        var aliased = new AliasStep(svc, NullLogger<AliasStep>.Instance)
            .ExecuteAsync(input, synced, CancellationToken.None);

        var step = new BroaderStep(svc, NullLogger<BroaderStep>.Instance);
        var carry = step.ExecuteAsync(input, aliased, CancellationToken.None);

        Assert.Null(carry.Error);
        Assert.Equal(1, carry.BroaderAdded);
        Assert.Equal(2, carry.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        var child = view.Concepts.Single(c => c.DisplayLabel == "Centrifugal Pump");
        var parent = view.Concepts.Single(c => c.DisplayLabel == "Pump");
        Assert.Contains(parent.Iri, child.Broader);
    }

    [Fact]
    public void ExecuteAsync_MalformedCarry_ReturnsErrorCarry()
    {
        // Same synthetic-throw pin as EntitySyncStepTests: SchemeIri
        // non-null passes the guard, the null View throws inside the
        // pass, and the step converts it to an Error carry (D5).
        var svc = new TerminologyService(_fx.Store);
        var step = new BroaderStep(svc, NullLogger<BroaderStep>.Instance);
        var malformed = new TermSyncCarry("http://x/scheme", null, null, 0);

        var carry = step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            malformed,
            CancellationToken.None);

        Assert.NotNull(carry.Error);
        Assert.True(carry.Skipped);
        Assert.Null(carry.SchemeIri);
    }

    // SeedMutation helper — identical to StaleMappingStepTests.
    private void SeedMutation(
        IReadOnlyList<string> classes,
        IReadOnlyList<string> objectProperties,
        IReadOnlyList<string> dataProperties,
        IReadOnlyList<AxiomMutation> axioms)
    {
        var mutation = new OntologyMutation(
            Classes: classes.Select(l => new ClassMutation(l)).ToArray(),
            ObjectProperties: objectProperties.Select(l => new PropertyMutation(l, "object")).ToArray(),
            DataProperties: dataProperties.Select(l => new PropertyMutation(l, "data")).ToArray(),
            Axioms: axioms);
        var quads = SchemaBuilder.BuildMutation(_ks.BaseIri, mutation, _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), quads);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Dovetail.Terminology.Steps" --nologo`
Expected: FAIL with `CS0246: 未能找到类型或命名空间名"StaleMappingStep"`(and similar)

- [ ] **Step 3: Write the 4 step classes**

Create `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/StaleMappingStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology init + pass 1 (stale mappings).
/// The init half builds the pass-shared carry
/// (<see cref="TerminologyService.PrepareCarry"/>); the pass half prunes
/// <c>op:mapsTo</c> triples whose target no longer exists in the ontology
/// or ABox. A thrown exception (cancellation aside) becomes an
/// <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class StaleMappingStep : IPipelineSegment<TerminologyInput, TermSyncCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<StaleMappingStep> _logger;

    public StaleMappingStep(TerminologyService terminology, ILogger<StaleMappingStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<TermSyncCarry> ExecuteAsync(TerminologyInput input, CancellationToken cancellationToken)
    {
        try
        {
            var carry0 = _terminology.PrepareCarry(input.Ks, cancellationToken);
            return Task.FromResult(
                _terminology.PassStaleMappings(input.Ks, carry0, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StaleMappingStep: init/pass 1 failed (fail-soft carry)");
            return Task.FromResult(
                new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true));
        }
    }
}
```

Create `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/EntitySyncStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology pass 2 (entity sync). Runs the
/// Python decision tree per entity over the carry built by
/// <see cref="StaleMappingStep"/>. A thrown exception (cancellation aside)
/// becomes an <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class EntitySyncStep : IPipelineSegment<TerminologyInput, TermSyncCarry, TermSyncCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<EntitySyncStep> _logger;

    public EntitySyncStep(TerminologyService terminology, ILogger<EntitySyncStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<TermSyncCarry> ExecuteAsync(
        TerminologyInput input,
        TermSyncCarry carry,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(
                _terminology.PassEntitySync(input.Ks, carry, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EntitySyncStep: pass 2 failed (fail-soft carry)");
            return Task.FromResult(
                new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true));
        }
    }
}
```

Create `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/AliasStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology pass 3 (alias additions). Attaches
/// each mapped concept's entity label as an <c>skos:altLabel</c> when it is
/// not already attached. A thrown exception (cancellation aside) becomes an
/// <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class AliasStep : IPipelineSegment<TerminologyInput, TermSyncCarry, TermSyncCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<AliasStep> _logger;

    public AliasStep(TerminologyService terminology, ILogger<AliasStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<TermSyncCarry> ExecuteAsync(
        TerminologyInput input,
        TermSyncCarry carry,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(
                _terminology.PassAliasAdditions(input.Ks, carry, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AliasStep: pass 3 failed (fail-soft carry)");
            return Task.FromResult(
                new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true));
        }
    }
}
```

Create `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/BroaderStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: terminology pass 4 (broader additions). Seeds
/// <c>skos:broader</c> triples from <c>rdfs:subClassOf</c> relations among
/// mapped classes. A thrown exception (cancellation aside) becomes an
/// <c>Error</c>+<c>Skipped</c> carry so every downstream step
/// short-circuits (spec §5 D5).
/// </summary>
public sealed class BroaderStep : IPipelineSegment<TerminologyInput, TermSyncCarry, TermSyncCarry>
{
    private readonly TerminologyService _terminology;
    private readonly ILogger<BroaderStep> _logger;

    public BroaderStep(TerminologyService terminology, ILogger<BroaderStep> logger)
    {
        _terminology = terminology;
        _logger = logger;
    }

    public Task<TermSyncCarry> ExecuteAsync(
        TerminologyInput input,
        TermSyncCarry carry,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(
                _terminology.PassBroaderAdditions(input.Ks, carry, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BroaderStep: pass 4 failed (fail-soft carry)");
            return Task.FromResult(
                new TermSyncCarry(null, null, null, 0, Error: ex.Message, Skipped: true));
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Dovetail.Terminology.Steps" --nologo`
Expected: `Passed: 8, Failed: 0`

- [ ] **Step 5: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 961, Failed: 0, Skipped: 1, Total: 962`(953 + 8)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Terminology/Steps/ \
        src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/
git commit -m "feat(extraction): add Terminology Dovetail 4 pass step classes (8 tests)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: ProposalStep + 3 tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/ProposalStep.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/ProposalStepTests.cs`

**Interfaces:**
- Consumes: `TerminologyInput` + `TermSyncCarry` (Task 1), `TerminologyService.FoldCarry` (Task 2), `TerminologyAgent.SuggestAsync` (P3-1, unchanged)
- Produces: `ProposalStep` implementing `IPipelineSegment<TerminologyInput, TermSyncCarry, TerminologyResult>`(Task 5 + Task 6 使用)

**行为契约(spec §5 D6 + D7)**:gating(`input.SuggestEnabled && carry.Error is null && carry.SchemeIri 非空`)在 step 内判;agent 异常**不吞**,原样传播(P1-4 行为一致 — orchestrator 外层 catch → `QuadChangeCapture.MarkError()`);`_agent` null(hand-built)时 fail-soft 折叠。

- [ ] **Step 1: Write 3 failing tests**

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/ProposalStepTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Unit tests for the P3-1 proposal segment. The happy-path / throw tests
/// run a real <see cref="TerminologyAgent"/> against the shared fake chat
/// factory, so the class joins <see cref="ExtractionTestCollection"/>.
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ProposalStepTests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/term-step5";
    private const string BaseIri = GraphIri + "/onto#";

    private readonly SqliteContextFactory _contexts = new();
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly FakeChat _chat = new();

    public ProposalStepTests()
    {
        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(_chat);
    }

    [Fact]
    public async Task ExecuteAsync_GatingNotMet_FoldsWithoutProposals()
    {
        using var db = _contexts.CreateDbContext();
        var step = new ProposalStep(
            null,
            db,
            Options.Create(new ISEStudioOptions()),
            NullLogger<ProposalStep>.Instance);
        var ks = new KsContext(GraphIri, BaseIri);

        // Sub-case 1: SuggestEnabled=false — the operator switch is off.
        var r1 = await step.ExecuteAsync(
            new TerminologyInput(ks, _ksId, "fake-model", SuggestEnabled: false),
            new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0, TermsAdded: 2),
            CancellationToken.None);
        Assert.Equal(0, r1.ProposalsQueued);
        Assert.Equal(2, r1.TermsAdded);

        // Sub-case 2: Error carry — the deterministic sync errored.
        var r2 = await step.ExecuteAsync(
            new TerminologyInput(ks, _ksId, "fake-model", SuggestEnabled: true),
            new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0, Error: "boom"),
            CancellationToken.None);
        Assert.Equal("boom", r2.Error);
        Assert.Equal(0, r2.ProposalsQueued);

        // Sub-case 3: no SchemeIri — the deterministic sync short-circuited.
        var r3 = await step.ExecuteAsync(
            new TerminologyInput(ks, _ksId, "fake-model", SuggestEnabled: true),
            new TermSyncCarry(null, null, null, 0, TermsAdded: 2),
            CancellationToken.None);
        Assert.Equal(0, r3.ProposalsQueued);
        Assert.Equal(2, r3.TermsAdded);

        // Gating never reached the chat layer.
        Assert.Equal(0, _chat.CallCount);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task ExecuteAsync_QueuesProposalsFromAgent()
    {
        var chunkId = SeedChunk();
        _chat.EnqueueTerminologyProposal(1, new[] { chunkId });

        using var db = _contexts.CreateDbContext();
        var agent = new TerminologyAgent(
            FakeChatClientFactory.Default,
            db,
            Options.Create(new ISEStudioOptions()),
            TimeProvider.System);
        var step = new ProposalStep(
            agent,
            db,
            Options.Create(new ISEStudioOptions { TerminologySuggestionMaxChunks = 10 }),
            NullLogger<ProposalStep>.Instance);

        var result = await step.ExecuteAsync(
            new TerminologyInput(new KsContext(GraphIri, BaseIri), _ksId, "fake-model", SuggestEnabled: true),
            new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0, TermsAdded: 2),
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.ProposalsQueued);
        Assert.Equal(2, result.TermsAdded);

        await using var check = _contexts.CreateDbContext();
        var row = Assert.Single(await check.TermProposals.ToListAsync());
        Assert.Equal("Term 0", row.Term);
        Assert.Equal("pending", row.Status);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task ExecuteAsync_AgentThrows_Propagates()
    {
        // No client installed in the shared factory → the agent's chat
        // resolution throws. The step must NOT swallow it (P1-4 parity —
        // the orchestrator's outer catch marks the capture).
        SeedChunk();
        FakeChatClientFactory.Default.Reset();

        using var db = _contexts.CreateDbContext();
        var agent = new TerminologyAgent(
            FakeChatClientFactory.Default,
            db,
            Options.Create(new ISEStudioOptions()),
            TimeProvider.System);
        var step = new ProposalStep(
            agent,
            db,
            Options.Create(new ISEStudioOptions()),
            NullLogger<ProposalStep>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            step.ExecuteAsync(
                new TerminologyInput(new KsContext(GraphIri, BaseIri), _ksId, "fake-model", SuggestEnabled: true),
                new TermSyncCarry($"{GraphIri}/vocabulary#scheme-extracted", null, null, 0),
                CancellationToken.None));
    }

    /// <summary>
    /// Seed the knowledge system + provider + one parsed chunk, so the
    /// step's chunk-id query finds exactly one row (the agent grounds its
    /// proposal against it). Returns the chunk's Guid PK.
    /// </summary>
    private Guid SeedChunk()
    {
        using var db = _contexts.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            Name = "term-step-llm",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "fake-model",
            Kind = "llm",
            ConcurrencyLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Term step fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _ksId,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "pump.txt",
            Folder = "/",
            ParseStatus = "parsed",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        var text = "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        var chunk = new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.SaveChanges();
        return chunk.Id;
    }

    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
        _contexts.Dispose();
    }
}
```

NOTE: `FakeChat.EnqueueTerminologyProposal(1, new[] { chunkId })` produces a proposal whose `preferred_label` is `"Term 0"` and whose `source_chunk_ids` cite the seeded chunk's Guid PK — the agent's grounding filter accepts it. `FakeChatClientFactory` with no client installed throws `InvalidOperationException` from `Create(...)` — that is the deterministic throw for the third test. Both helpers are verified against `src/ISEStudio.Tests/Extraction/FakeChat.cs` + `FakeChatClientFactory.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ProposalStepTests" --nologo`
Expected: FAIL with `CS0246: 未能找到类型或命名空间名"ProposalStep"`

- [ ] **Step 3: Write ProposalStep**

Create `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/ProposalStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ISEStudio.Extraction.Dovetail.Terminology.Steps;

/// <summary>
/// Dovetail pipeline segment: P3-1 terminology proposals. The gating
/// (<c>SuggestEnabled</c> / carry error / SchemeIri) runs inside the step
/// (spec §5 D6); the chunk-id query mirrors
/// <c>ExtractionOrchestrator.RunTerminologyAgentAsync</c>; the accepted-row
/// count folds into the final <see cref="TerminologyResult"/> via
/// <see cref="TerminologyService.FoldCarry"/>. Agent exceptions propagate
/// (P1-4 parity — the orchestrator's outer catch marks the capture), and a
/// null agent (hand-built step tests) folds fail-soft.
/// </summary>
public sealed class ProposalStep : IPipelineSegment<TerminologyInput, TermSyncCarry, TerminologyResult>
{
    private readonly TerminologyAgent? _agent;
    private readonly ISEStudioDbContext _db;
    private readonly ILogger<ProposalStep> _logger;
    private readonly int _maxChunks;

    public ProposalStep(
        TerminologyAgent? agent,
        ISEStudioDbContext db,
        IOptions<ISEStudioOptions> options,
        ILogger<ProposalStep> logger)
    {
        _agent = agent;
        _db = db;
        _logger = logger;
        _maxChunks = options.Value.TerminologySuggestionMaxChunks;
    }

    public async Task<TerminologyResult> ExecuteAsync(
        TerminologyInput input,
        TermSyncCarry carry,
        CancellationToken cancellationToken)
    {
        var folded = TerminologyService.FoldCarry(carry);

        if (!input.SuggestEnabled || carry.Error is not null || string.IsNullOrEmpty(carry.SchemeIri))
        {
            return folded;
        }

        if (_agent is null)
        {
            _logger.LogWarning("ProposalStep: agent is null, folding carry without proposals");
            return folded;
        }

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == input.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null)
        {
            return folded;
        }

        // job.ChunkIds stores ChunkSpan.Idx (an in-memory 0-based index,
        // not ChunkEntity.Id), so we cannot feed it to the agent directly.
        // Query the parsed-document chunks belonging to this knowledge
        // system, ordered for stable propose prompts (Python
        // _terminology_rows orders by document then chunk order too).
        // ChunkEntity has no `Document` navigation property — the join is
        // explicit, mirroring TerminologyAgent.LoadChunksAsync. Phase 3:
        // legacy_id 列已退役; we hand the agent Guid PKs.
        var chunkIds = await _db.Chunks.AsNoTracking()
            .Join(_db.Documents,
                c => c.DocumentId,
                d => d.Id,
                (c, d) => new { Chunk = c, Document = d })
            .Where(join => join.Document.KnowledgeSystemId == ks.Id
                && join.Document.ParseStatus == "parsed")
            .OrderBy(join => join.Chunk.DocumentId).ThenBy(join => join.Chunk.Idx)
            .Take(_maxChunks)
            .Select(join => join.Chunk.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (chunkIds.Count == 0)
        {
            return folded;
        }

        var proposals = await _agent.SuggestAsync(
            ks, carry.SchemeIri!, chunkIds, input.Model, cancellationToken)
            .ConfigureAwait(false);
        return folded with { ProposalsQueued = proposals.Count };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ProposalStepTests" --nologo`
Expected: `Passed: 3, Failed: 0`

- [ ] **Step 5: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 964, Failed: 0, Skipped: 1, Total: 965`(961 + 3)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Terminology/Steps/ProposalStep.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/ProposalStepTests.cs
git commit -m "feat(extraction): add Dovetail Terminology ProposalStep (P3-1 agent folding)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: TerminologyPipeline partial class + 1 emit test

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyPipeline.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyPipelineTests.cs`

**Interfaces:**
- Consumes: 5 step classes (Tasks 3+4), `TerminologyInput` + `TermSyncCarry` (Task 1), `TerminologyResult` (ISEStudio.Extraction)
- Produces: `TerminologyPipeline` partial class with 5 `[Segment]` ctor params; source-gen emits `TerminologyPipeline.g.cs` with `ExecuteAsync` + Mermaid `flowchart TD`

- [ ] **Step 1: Write the failing test**

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyPipelineTests.cs`:

```csharp
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Terminology;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology;

public class TerminologyPipelineTests
{
    [Fact]
    public void TerminologyPipeline_DovetailEmitsExecuteAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // StoreWrapper is nullable on TerminologyService's ctor, so a null
        // store is enough to make the 4 pass steps resolvable; the
        // ProposalStep factory yields null! (no agent registered) and the
        // pipeline still constructs (latent — production always wires it).
        services.AddSingleton(new TerminologyService(null));
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TerminologyPipeline>();
        Assert.NotNull(pipeline);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TerminologyPipelineTests" --nologo`
Expected: FAIL with `CS0246: 未能找到类型或命名空间名"TerminologyPipeline"`

- [ ] **Step 3: Write the pipeline partial class**

Create `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyPipeline.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;

namespace ISEStudio.Extraction.Dovetail.Terminology;

/// <summary>
/// Dovetail pipeline that runs the extraction terminology sync as five
/// typed segments: StaleMapping → EntitySync → Alias → Broader → Proposal.
/// Constructed via <see cref="Dovetail.DovetailPipelineBuilderExtensions.AddPipelines"/>;
/// the source generator emits <c>TerminologyPipeline.g.cs</c> with the
/// <see cref="ExecuteAsync"/> method and Mermaid diagram. The orchestrator
/// resolves it from the per-job scope (Slice 3 R2 lifecycle) and falls back
/// to the P1-4 chain (<see cref="TerminologyService.SyncAsync"/> + scoped
/// agent) when it cannot.
/// </summary>
public partial class TerminologyPipeline : IPipeline<TerminologyInput, TerminologyResult>
{
    public TerminologyPipeline(
        [Segment] StaleMappingStep staleMappingStep,
        [Segment] EntitySyncStep entitySyncStep,
        [Segment] AliasStep aliasStep,
        [Segment] BroaderStep broaderStep,
        [Segment] ProposalStep proposalStep)
    {
        StaleMappingStep = staleMappingStep;
        EntitySyncStep = entitySyncStep;
        AliasStep = aliasStep;
        BroaderStep = broaderStep;
        ProposalStep = proposalStep;
    }

    public StaleMappingStep StaleMappingStep { get; }
    public EntitySyncStep EntitySyncStep { get; }
    public AliasStep AliasStep { get; }
    public BroaderStep BroaderStep { get; }
    public ProposalStep ProposalStep { get; }
}
```

- [ ] **Step 4: Verify source-gen emits ExecuteAsync + 5-stage DAG**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --nologo`
Expected: 0 errors. Inspect the emitted `TerminologyPipeline.g.cs` (typically under `src/ISEStudio/obj/Debug/net10.0/generated/Dovetail/`) for:
- A `Task<TerminologyResult> ExecuteAsync(TerminologyInput, CancellationToken)` method
- A `Mermaid` property returning a `flowchart TD` string naming the 5 segment nodes
- Segment wrappers invoking each step in dependency order (carry relay between steps)

If `EmitCompilerGeneratedFiles=true` is not already set in the project, add to a `Directory.Build.props` temporarily, build, inspect, then revert. (Slice 2 Task 3 precedent.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~TerminologyPipelineTests" --nologo`
Expected: `Passed: 1, Failed: 0`

- [ ] **Step 6: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 965, Failed: 0, Skipped: 1, Total: 966`(964 + 1)

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyPipeline.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyPipelineTests.cs
git commit -m "feat(extraction): add Dovetail TerminologyPipeline (5-stage DAG)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: DI registration for 5 steps + 4 tests

**Files:**
- Modify: `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` (append §8 block)
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/DovetailPipelineRegistrationsTerminologyTests.cs`

**Interfaces:**
- Consumes: 5 step classes (Tasks 3+4), 1 pipeline class (Task 5)
- Produces: 5 step DI registrations — 4 pass steps `AddScoped` plain(dep 只有 singleton TerminologyService),ProposalStep 用 Slice 3 `null!` factory 口径(agent 缺失 → step null → 负向测试断言)

- [ ] **Step 1: Write the failing tests**

Create `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/DovetailPipelineRegistrationsTerminologyTests.cs`:

```csharp
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Llm;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology;

public class DovetailPipelineRegistrationsTerminologyTests
{
    private static void AddDbContexts(IServiceCollection services, SqliteContextFactory contexts)
    {
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
    }

    [Fact]
    public void PassSteps_AreResolvable_WhenTerminologyServiceRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new TerminologyService(null));
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<StaleMappingStep>());
        Assert.NotNull(sp.GetService<EntitySyncStep>());
        Assert.NotNull(sp.GetService<AliasStep>());
        Assert.NotNull(sp.GetService<BroaderStep>());
    }

    [Fact]
    public void ProposalStep_ResolvesNull_WhenTerminologyAgentMissing()
    {
        // The §8 factory returns null! when the agent is absent (Slice 3
        // null! 口径) — the registration tests pin that shape.
        using var contexts = new SqliteContextFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton(new TerminologyService(null));
        AddDbContexts(services, contexts);
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.Null(sp.GetService<ProposalStep>());
    }

    [Fact]
    public void TerminologyPipeline_IsResolvable_WhenAllStepsResolve()
    {
        using var contexts = new SqliteContextFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton(new TerminologyService(null));
        AddDbContexts(services, contexts);
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddScoped<TerminologyAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<TerminologyPipeline>());
    }

    [Fact]
    public void TerminologyPipeline_ResolveFails_WhenTerminologyServiceMissing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.Null(sp.GetService<TerminologyPipeline>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~DovetailPipelineRegistrationsTerminologyTests" --nologo`
Expected: FAIL — pass steps / pipeline not registered.

- [ ] **Step 3: Append the §8 registration block to DovetailPipelineRegistrations**

In `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`, **append** after the §7 AgentChain block (do NOT modify existing registrations):

```csharp
        // 8. Terminology slice 4 step classes (per spec §6.2 + §5 D9).
        // SCOPED for the same per-job lifecycle reason as §7: the
        // orchestrator resolves TerminologyPipeline from the per-job scope,
        // so the steps live per job. The four pass steps depend only on the
        // singleton TerminologyService (registered plainly); ProposalStep
        // holds the scoped TerminologyAgent + DbContext and reuses the §7
        // null! factory pattern so a missing agent surfaces as a null step.
        services.AddScoped<StaleMappingStep>();
        services.AddScoped<EntitySyncStep>();
        services.AddScoped<AliasStep>();
        services.AddScoped<BroaderStep>();
        services.AddScoped<ProposalStep>(sp =>
        {
            var agent = sp.GetService<TerminologyAgent>();
            return agent is null
                ? null!
                : new ProposalStep(
                    agent: agent,
                    db: sp.GetRequiredService<ISEStudioDbContext>(),
                    options: sp.GetRequiredService<IOptions<ISEStudioOptions>>(),
                    logger: sp.GetRequiredService<ILogger<ProposalStep>>());
        });
```

Import statements to add at the top of the file:

```csharp
using ISEStudio.Configuration;                                   // ISEStudioOptions (if not present)
using ISEStudio.Extraction;                                      // TerminologyService / TerminologyAgent
using ISEStudio.Extraction.Dovetail.Terminology.Steps;           // the 5 step classes
using ISEStudio.Infrastructure.Persistence;                      // ISEStudioDbContext
```

(`Microsoft.Extensions.Options` for `IOptions<T>` is already imported; `Microsoft.Extensions.Logging` + `Microsoft.Extensions.DependencyInjection` are already imported.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~DovetailPipelineRegistrationsTerminologyTests" --nologo`
Expected: `Passed: 4, Failed: 0`

- [ ] **Step 5: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 969, Failed: 0, Skipped: 1, Total: 970`(965 + 4)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/Terminology/DovetailPipelineRegistrationsTerminologyTests.cs
git commit -m "feat(extraction): wire TerminologyPipeline into DI

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: ExtractionOrchestrator wire-up + 2 DI tests + 1 e2e

**Files:**
- Modify: `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` (add `_terminologyPipeline` field + ctor tail param + `RunTerminologyAsync` body 替换 + 新增 `RunTerminologyPipelineIfAvailableAsync` helper;**`RunTerminologyAgentAsync` 保留** — fallback 路径复用)
- Create: `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTerminologyPipelineTests.cs` (2 DI tests)
- Create: `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTerminologyPipelineE2ETests.cs` (1 e2e)

**Interfaces:**
- Consumes: `TerminologyPipeline` + `TerminologyInput` (Tasks 1+5)
- Produces: orchestrator field + ctor tail-seam param + new `RunTerminologyAsync` body + helper + 3 new tests

**CRITICAL — read these files in full first:**

1. **`src/ISEStudio/Extraction/ExtractionOrchestrator.cs`** — verify the field block (~lines 128-135, `_agentChainPipeline`)、ctor tail param (line 182 `AgentChainPipeline? agentChainPipeline = null`)、ctor assignment (line 219)、`RunTerminologyAsync` (lines 538-580)、`RunTerminologyAgentAsync` (lines 593-639)。All line numbers are from the pre-task state — if they shifted, find by symbol.
2. **`src/ISEStudio.Tests/Extraction/TerminologyAgentOrchestrationTests.cs`** — the fixture template for the e2e test (constants, ProposeReply, SeedTBox, SeedKnowledgeSystem, PutDocument, BuildServices, BuildOrchestrator, Dispose)。Copy everything verbatim except `BuildServices` (which gains the Dovetail registrations).

- [ ] **Step 1: Add `_terminologyPipeline` field + ctor tail param to ExtractionOrchestrator**

(a) Add field after `_agentChainPipeline` (after line 135):

```csharp
    /// <summary>
    /// Dovetail-generated terminology pipeline (StaleMapping → EntitySync →
    /// Alias → Broader → Proposal). Preferred over the P1-4 chain when the
    /// per-job scope resolves one (production); the P1-4 chain (SyncAsync +
    /// scoped TerminologyAgent) is the fallback for hand-built test
    /// orchestrators and DI failures.
    /// </summary>
    private readonly TerminologyPipeline? _terminologyPipeline;
```

(b) Add ctor tail parameter after `agentChainPipeline`:

```csharp
        TerminologyPipeline? terminologyPipeline = null)
```

(c) Assign in ctor body after `_agentChainPipeline = agentChainPipeline;`:

```csharp
        _terminologyPipeline = terminologyPipeline;
```

(d) Add import:

```csharp
using ISEStudio.Extraction.Dovetail.Terminology;
```

- [ ] **Step 2: Replace `RunTerminologyAsync` body + add the helper**

Replace the body of `RunTerminologyAsync` (keep the XML doc) with:

```csharp
    private async Task RunTerminologyAsync(JobRunContext ctx, int totalProcessed)
    {
        await using var termCapture = await _store.CaptureAsync(
            ctx.KsContext.VocabularyGraph, revertOnError: false, waitTimeout: TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        try
        {
            // Dovetail pipeline preferred when the per-job scope resolves
            // one (production — resolved per job so the scoped steps +
            // agent + DbContext live per job, the Slice 3 R2 lifecycle).
            // The P1-4 chain (SyncAsync + scoped TerminologyAgent) is the
            // fallback for hand-built test orchestrators and DI failures.
            var dagResult = await RunTerminologyPipelineIfAvailableAsync(ctx).ConfigureAwait(false);
            var term = dagResult ?? _terminology.SyncAsync(ctx.KsContext, CancellationToken.None);

            await _jobs.UpdateProgressAsync(ctx.JobId,
                processedChunks: totalProcessed,
                phase: ExtractionPhase.Terminology.ToWire(),
                appendPhaseToLog: ExtractionPhase.Terminology.ToWire(),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            // P3-1 (terminology proposals): deterministic sync was advisory
            // and never queued any. Now that the deterministic pass has
            // stamped the concept scheme, ask the scoped LLM-driven
            // TerminologyAgent to suggest pending TermProposal rows. The
            // agent is Scoped (own DbContext), so we resolve it from a
            // fresh scope the same way the post-TBox agent chain
            // (RunAgentChainAsync) does. When the DAG ran, the proposal
            // pass already happened inside the pipeline — this block only
            // serves the fallback path.
            //
            // Skipped when:
            //   * the DAG path already ran the proposal pass
            //   * the operator opted out via ISEStudioOptions
            //     (terminology_suggest_during_extraction)
            //   * no scope factory is wired (hand-built test orchestrators)
            //   * the deterministic sync short-circuited (no SchemeIri) or
            //     errored (term.Error is set)
            if (dagResult is null
                && _options.TerminologySuggestDuringExtraction
                && _scopes is not null
                && term.Error is null
                && !string.IsNullOrEmpty(term.SchemeIri))
            {
                term = await RunTerminologyAgentAsync(ctx, term).ConfigureAwait(false);
            }

            await _jobs.RecordTerminologyAsync(ctx.JobId, term, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            termCapture.MarkError();
        }
    }

    /// <summary>
    /// Resolve the Dovetail <see cref="TerminologyPipeline"/> from a fresh
    /// per-job scope (scope resolution first — the ctor seam only serves
    /// hand-built orchestrators whose scope cannot resolve one). Returns
    /// <c>null</c> when no scope factory is wired or the pipeline cannot be
    /// resolved, so the caller falls back to the P1-4 chain.
    /// </summary>
    private async Task<TerminologyResult?> RunTerminologyPipelineIfAvailableAsync(JobRunContext ctx)
    {
        if (_scopes is null) return null;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var pipeline = services.GetService<TerminologyPipeline>() ?? _terminologyPipeline;
        if (pipeline is null) return null;

        return await pipeline.ExecuteAsync(new TerminologyInput(
            Ks: ctx.KsContext,
            KnowledgeSystemId: ctx.Request.KnowledgeSystemId,
            Model: ctx.Request.Model,
            SuggestEnabled: _options.TerminologySuggestDuringExtraction),
            CancellationToken.None).ConfigureAwait(false);
    }
```

NOTE: `RunTerminologyAgentAsync` stays EXACTLY as it is — it is the fallback path's proposal stage. The three runner call sites (TBoxOnlyRunnerAsync line 417, ABoxOnlyRunnerAsync line 442, CombinedRunnerAsync line 517) are untouched.

- [ ] **Step 3: Write the 2 DI tests**

Create `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTerminologyPipelineTests.cs`:

```csharp
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Llm;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// DI-level tests for the Dovetail terminology pipeline resolution through
/// the same registration surface the orchestrator uses
/// (<see cref="DovetailPipelineRegistrations.AddDovetailPipelines"/>).
/// </summary>
public class ExtractionOrchestratorTerminologyPipelineTests
{
    [Fact]
    public void TerminologyPipeline_IsResolvable_FromOrchestratorServices()
    {
        using var contexts = new SqliteContextFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton(new TerminologyService(null));
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddScoped<TerminologyAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TerminologyPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void TerminologyPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        // Intentionally NOT calling AddDovetailPipelines().
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TerminologyPipeline>();
        Assert.Null(pipeline);
    }
}
```

- [ ] **Step 4: Write the e2e test**

Create `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTerminologyPipelineE2ETests.cs` — a fixture clone of `TerminologyAgentOrchestrationTests` whose `BuildServices` registers the Dovetail pipelines + the terminology service + the agent-chain interface forwarders, and whose orchestrator passes NO ctor pipeline (the scope must resolve it):

```csharp
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Knowledge;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Persistence;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// End-to-end test for the Dovetail terminology pipeline wired through
/// <see cref="ExtractionOrchestrator.RunTerminologyAsync"/>: the per-job
/// scope resolves <see cref="Dovetail.Terminology.TerminologyPipeline"/>
/// (the ctor seam stays null), the 5-segment DAG runs the deterministic
/// sync + the P3-1 proposal agent, and the job row records the folded
/// result. The fixture mirrors TerminologyAgentOrchestrationTests — which
/// pins the P1-4 fallback chain — except BuildServices additionally
/// registers AddDovetailPipelines + the terminology service + the
/// agent-chain interface forwarders (RunAgentChainAsync scope-resolves
/// AgentChainPipeline since Slice 3 R2, so the forwarders must exist for
/// the job to survive the agent chain on the DAG path).
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class ExtractionOrchestratorTerminologyPipelineE2ETests : IDisposable
{
    private const string GraphIri = "http://goodcrew.local/ks/term-dag";
    private const string BaseIri = GraphIri + "/onto#";

    private const string TBoxDelta = """
        {
          "classes": [
            {"label": "Pump", "comment": "A device that moves fluid"},
            {"label": "Centrifugal Pump", "comment": "A pump that uses rotational energy"}
          ],
          "object_properties": [],
          "data_properties": [],
          "subclass_of": [{"sub": "Centrifugal Pump", "super": "Pump"}],
          "disjoint_with": [],
          "equivalent_class": []
        }
        """;

    private static string ProposeReply(Guid chunkId) => $$"""
        {
          "proposals": [{
            "action": "create",
            "preferred_label": "Impeller",
            "language": "en",
            "alternate_labels": [],
            "description": "Rotating component of a centrifugal pump",
            "broader_concept_iri": null,
            "mapped_entity_iri": null,
            "confidence": 0.9,
            "reason": "explicit component in source",
            "source_chunk_ids": ["{{chunkId}}"]
          }]
        }
        """;

    private readonly string _root;
    private readonly SqliteContextFactory _contexts;
    private readonly Guid _ksId = Guid.NewGuid();
    private readonly IBlobStore _blobs;

    private StoreWrapper Store { get; }

    private KsContext Ks { get; } = new(GraphIri, BaseIri);

    private ExtractionJobStore Jobs { get; }

    private FakeChat FakeChat { get; } = new();

    private ServiceProvider Services { get; }

    private ExtractionOrchestrator Orchestrator { get; }

    private ExtractionRequest Request { get; }

    /// <summary>Guid PK of the fixture chunk the agent's prompt will quote.</summary>
    private Guid ChunkId { get; }

    public ExtractionOrchestratorTerminologyPipelineE2ETests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "isestudio-term-dag-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        Store = new StoreWrapper(Path.Combine(_root, "store"));
        SeedTBox();

        _contexts = new SqliteContextFactory();
        ChunkId = SeedKnowledgeSystem();

        _blobs = new LocalCasBlobStore(Path.Combine(_root, "blobs"));
        var sha = PutDocument(_blobs);

        Jobs = new ExtractionJobStore(_contexts, TimeProvider.System);

        FakeChatClientFactory.Default.Reset();
        FakeChatClientFactory.Default.UseClient(FakeChat);

        Services = BuildServices();
        Orchestrator = BuildOrchestrator(Services.GetRequiredService<IServiceScopeFactory>());

        Request = new ExtractionRequest(
            KnowledgeSystemId: _ksId,
            BlobSha: sha,
            FileName: "term-dag.txt",
            Provider: "openai",
            Model: "fake-model",
            Endpoint: "https://fake.test/v1",
            ApiKey: null,
            ConcurrencyLimit: 1);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task TerminologyPipeline_RunsViaScopeResolution_AndQueuesProposals()
    {
        // Layer 1: TBox extraction produces two classes; layer 2: the
        // deterministic sync seals the scheme (now via the Dovetail
        // StaleMapping → EntitySync → Alias → Broader segments); layer 3:
        // the ProposalStep folds the terminology agent's accepted row.
        FakeChat.Enqueue(TBoxDelta);
        FakeChat.Enqueue(ProposeReply(ChunkId));

        var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
        var finished = await Jobs.WaitAsync(job.Id);

        Assert.True(finished.Status == "completed",
            $"Expected completed but got {finished.Status}: {finished.Error} {finished.Log}");
        Assert.Equal(
            new[] { "tbox", "conflicts", "structure", "terminology", "finalizing" },
            ExtractionJobLog.Phases(finished.Log));

        // The ProposalStep folded one accepted proposal into the job row.
        Assert.Equal(1, finished.TerminologyProposals);

        // The proposal itself landed on the database (proves the DAG's
        // agent pass actually ran, not just the count was synthesised).
        await using var db = _contexts.CreateDbContext();
        var rows = await db.TermProposals
            .Where(p => p.KnowledgeSystemId == _ksId)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("create", row.Action);
        Assert.Equal("Impeller", row.Term);
        Assert.Equal("pending", row.Status);
    }

    // ------------------------------------------------------------------
    // Helpers — copied verbatim from TerminologyAgentOrchestrationTests
    // (same seeding, same build, plus the Dovetail registrations).
    // ------------------------------------------------------------------

    private void SeedTBox()
    {
        var quads = SchemaBuilder.BuildMutation(
            BaseIri,
            new OntologyMutation(
                Classes: new[] { new ClassMutation("Pump", "Seeded fixture class") },
                ObjectProperties: Array.Empty<PropertyMutation>(),
                DataProperties: Array.Empty<PropertyMutation>(),
                Axioms: Array.Empty<AxiomMutation>()),
            Ks.TBoxGraph);
        Store.AddQuads(new OntoNamedNode(Ks.TBoxGraph), quads);
    }

    private Guid SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        var provider = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            Name = "term-dag-llm",
            BaseUrl = "http://localhost/v1",
            ApiKey = "test-key",
            Model = "fake-model",
            Kind = "llm",
            ConcurrencyLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Providers.Add(provider);

        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Term DAG fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            LlmProviderId = provider.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        const string text =
            "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = _ksId,
            Sha256 = Guid.NewGuid().ToString("N"),
            OriginalFilename = "pump.txt",
            Folder = "/",
            ParseStatus = "parsed",
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        var chunk = new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            Idx = 0,
            Text = text,
            CharStart = 0,
            CharEnd = text.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Chunks.Add(chunk);
        db.SaveChanges();
        return chunk.Id;
    }

    private static string PutDocument(IBlobStore blobs)
    {
        var text =
            "A centrifugal pump uses an impeller to move fluid outward by rotational energy.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return blobs.PutAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Sha256;
    }

    /// <summary>
    /// Build the orchestrator's scope factory — the TerminologyAgentOrchestrationTests
    /// BuildServices plus:
    /// <list type="bullet">
    /// <item><c>AddLogging()</c> — the §7/§8 step factories resolve
    /// <c>ILogger&lt;T&gt;</c>;</item>
    /// <item><c>AddDovetailPipelines()</c> — makes the scope resolve the
    /// TerminologyPipeline (and, since Slice 3 R2, the AgentChainPipeline);</item>
    /// <item><c>TerminologyService</c> — the pass steps' only dependency
    /// (a second instance over the same store; stateless wrapper, so
    /// behavior-equivalent to the orchestrator's own);</item>
    /// <item>the agent-chain interface forwarders — with AddDovetailPipelines
    /// in the container, RunAgentChainAsync resolves AgentChainPipeline from
    /// the scope, and its §7 factories need IConflictAgent / IStructureAgent /
    /// IKnowledgeStatsService to construct real steps (missing interfaces
    /// would yield null steps and a mid-job NRE).</item>
    /// </list>
    /// </summary>
    private ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddSingleton(Store);
        services.AddSingleton(Jobs);
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new ISEStudioOptions
        {
            TerminologySuggestionMaxChunks = 10,
            TerminologySuggestDuringExtraction = true,
        }));
        services.AddScoped<ConflictService>();
        services.AddScoped<ConflictAgent>();
        services.AddScoped<IConflictAgent, ConflictAgent>();
        services.AddScoped<StructureAgent>();
        services.AddScoped<IStructureAgent, StructureAgent>();
        services.AddSingleton<OntologyViewBuilder>();
        services.AddScoped<KnowledgeStatsService>();
        services.AddScoped<IKnowledgeStatsService, KnowledgeStatsService>();
        services.AddScoped<TerminologyAgent>();
        services.AddSingleton(new TerminologyService(Store));
        services.AddDovetailPipelines();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private ExtractionOrchestrator BuildOrchestrator(IServiceScopeFactory? scopes) =>
        new(
            Jobs,
            _blobs,
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            FakeChatClientFactory.Default,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(Store),
            new PromptSnapshotService(),
            new ExtractionMerger(Store),
            Store,
            TimeProvider.System,
            Options.Create(new ISEStudioOptions
            {
                TerminologySuggestDuringExtraction = true,
            }),
            verify: null,
            scopes: scopes);

    public void Dispose()
    {
        FakeChatClientFactory.Default.Reset();
        FakeChat.Release();
        Services.Dispose();
        Store.Dispose();
        _contexts.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The Oxigraph handle can linger briefly on Windows; a stale
            // temp directory must never fail a test run.
        }
    }
}
```

NOTE: The orchestrator is built with NO ctor pipeline (all pipeline params default to null) — the scope-resolution path is the ONLY DAG path in this test, exactly what the test must prove.

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ExtractionOrchestratorTerminologyPipeline" --nologo`
Expected: `Passed: 3, Failed: 0`

- [ ] **Step 6: Run full suite to verify no regression (THE GATE — 现有测试零改动)**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 972, Failed: 0, Skipped: 1, Total: 973`(969 + 3)

CRITICAL: `TerminologyAgentOrchestrationTests`(5 tests, P1-4 fallback)与 `ExtractionAgentChainTests` 的 DAG e2e 必须**零改动全绿** — 前者不注册 AddDovetailPipelines(scope 解析不出 pipeline → fallback),后者不注册 TerminologyService(pass steps 解析不出 → pipeline null → fallback SyncAsync + SuggestDuringExtraction 默认 false → 行为不变)。

- [ ] **Step 7: Run integration tests to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --nologo`
Expected: same as baseline (Docker unavailable pre-existing pattern — 4/0/0/4 或当前基线;集成测试不新增)

- [ ] **Step 8: Commit**

```bash
git add src/ISEStudio/Extraction/ExtractionOrchestrator.cs \
        src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTerminologyPipelineTests.cs \
        src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTerminologyPipelineE2ETests.cs
git commit -m "feat(extraction): wire TerminologyPipeline into RunTerminologyAsync

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 8: dovetail-report HTML for Terminology pipeline

**Files:**
- Create: `docs/superpowers/diagrams/extraction-terminology-dag/index.html`
- Create: `docs/superpowers/diagrams/extraction-terminology-dag/ISEStudio.Extraction.Dovetail.Terminology.TerminologyPipeline.html`
- Create: `docs/superpowers/diagrams/extraction-terminology-dag/vendor/mermaid.min.js`
- Create: `docs/superpowers/diagrams/extraction-terminology-dag/vendor/pico.indigo.min.css`

**Interfaces:**
- Consumes: `TerminologyPipeline` partial class (Task 5) discoverable by `dovetail-report`
- Produces: HTML DAG visualization report

- [ ] **Step 1: Verify dovetail-report 1.0.0 is installed**

```bash
dovetail-report --version
```

Expected: `1.0.0` (installed during Slice 1-3). If missing, install:

```bash
dotnet tool install --global Dovetail.Report --version 1.0.0
```

If nuget.org unreachable, fallback to local pack:

```bash
dotnet pack E:\GitHub\Dovetail\Dovetail.Report\Dovetail.Report.csproj -c Release -o ./local-nuget
dotnet tool install --global Dovetail.Report --version 1.0.0 --add-source ./local-nuget
```

If both fail: write `DONE_WITH_CONCERNS` in the report noting that the tool could not be installed, and STOP.

- [ ] **Step 2: Generate the Terminology sub-DAG report**

```bash
dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams/extraction-terminology-dag
```

Expected: command exits 0; `docs/superpowers/diagrams/extraction-terminology-dag/` now has at least `index.html` + `ISEStudio.Extraction.Dovetail.Terminology.TerminologyPipeline.html` + `vendor/`.

If `dovetail-report` complains `ISEStudio.csproj` does not compile, STOP and write `BLOCKED`.

- [ ] **Step 3: Verify the report contains the Terminology pipeline page**

```bash
ls docs/superpowers/diagrams/extraction-terminology-dag/index.html
ls docs/superpowers/diagrams/extraction-terminology-dag/ISEStudio.Extraction.Dovetail.Terminology.TerminologyPipeline.html
ls docs/superpowers/diagrams/extraction-terminology-dag/vendor/
```

Expected: all HTML files exist; `vendor/` has at least `mermaid.min.js` + `pico.indigo.min.css`.

- [ ] **Step 4: Spot-check the rendered DAG content**

```bash
grep -c "mermaid" docs/superpowers/diagrams/extraction-terminology-dag/ISEStudio.Extraction.Dovetail.Terminology.TerminologyPipeline.html
```

Expected: at least 1 occurrence. The 5-stage DAG (`staleMapping → entitySync → alias → broader → proposal`) should render.

- [ ] **Step 5: Verify ISEStudio.csproj still compiles clean after generation**

```bash
dotnet build src/ISEStudio/ISEStudio.csproj --nologo
```

Expected: 0 errors / 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/diagrams/extraction-terminology-dag/
git commit -m "docs(extraction): add Dovetail Terminology sub-DAG HTML report

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Self-Review

### Spec coverage

| Spec section | Covered by |
|--------------|------------|
| §2 设计目标(行为零变化) | Task 2 (拆分 gate) + Task 7 step 6 (零改动 gate) |
| §3 DAG 形状 | Tasks 3+4 (steps) + Task 5 (pipeline) |
| §4 Records (verbatim) | Task 1 |
| §5 D1 四遍拆 4 段 | Tasks 2+3 |
| §5 D2 carry record 接力 | Tasks 1+2 |
| §5 D3 init 段内化 (PrepareCarry) | Task 2 (PrepareCarry) + Task 3 (StaleMappingStep 先调 PrepareCarry) |
| §5 D4 外科拆分 public 零变化 | Task 2 |
| §5 D5 step catch → Error carry | Task 3 (4 steps 的 catch 形状) + Task 3 tests (synthetic throw pins) |
| §5 D6 ProposalStep gating 在 step 内 | Task 4 |
| §5 D7 FoldCarry 双路径 | Task 2 (FoldCarry) + Task 4 (ProposalStep 使用) |
| §5 D8 scope 解析优先 | Task 7 (helper `?? _terminologyPipeline`) |
| §5 D9 steps AddScoped | Task 6 |
| §6.1 新增文件 | Tasks 1, 3, 4, 5, 7 |
| §6.2 修改文件 | Tasks 2, 6, 7 (RunTerminologyAgentAsync 保留 — spec 已修正) |
| §6.3 不动文件 | 全 plan 无涉及 TerminologyAgent.cs / SkosManager / SchemaBuilder / ExtractionServiceCollectionExtensions |
| §7.1 新增 21 tests | Task 1 (2) + Task 3 (8) + Task 4 (3) + Task 5 (1) + Task 6 (4) + Task 7 (3) = 21 |
| §7.2 现有测试零改动 | Task 2 gate + Task 7 step 6 显式 CRITICAL 注释 |
| §7.3 Gate 972/0/1/973 | Task 7 step 6 |
| §8.1 风险 1 (拆分最重) | Task 2 单独成任务 + 现有测试全绿 gate |
| §8.1 风险 2 (null! 口径) | Task 6 (ProposalStep null! factory) |
| §8.1 风险 3 (Pass 3/4 BuildView 重建) | Task 2 (pass 内 `new SkosManager(_store)` 逐行保留) |
| §8.2 已接受口径 | Global Constraints 逐条列入 |
| §9 任务分解 | 本 plan 的 8 tasks |
| §10 LOCKED | 四遍顺序执行 + 无新 LOCKED option(plan 未引入) |
| §13 验收 | Tasks + 最终审查 |

**Gaps**: None identified。

### Placeholder scan

- No "TBD", "TODO", "implement later", "fill in details" in any step.
- No "add appropriate error handling" without specific code.
- Every code block is complete (Task 2 ships the entire replacement region; Task 7 ships the entire e2e fixture).
- The only adapt-to-codebase notes are the two NOTE lines in Task 2/7 pointing at pre-verified symbols (all signatures were read from source while writing this plan).

### Type consistency

- `TerminologyInput(Ks, KnowledgeSystemId, Model, SuggestEnabled)` defined Task 1, used Tasks 3, 4, 7.
- `TermSyncCarry(SchemeIri, View?, PreView?, PropertyCount, …counters, Error, Skipped)` defined Task 1, used Tasks 2, 3, 4, 7.
- `TerminologyService.PrepareCarry/PassStaleMappings/PassEntitySync/PassAliasAdditions/PassBroaderAdditions/FoldCarry` defined Task 2, used Tasks 3, 4.
- Step ctors: `(TerminologyService, ILogger<T>)` defined Task 3, used Tasks 5, 6; `(TerminologyAgent?, ISEStudioDbContext, IOptions<ISEStudioOptions>, ILogger<ProposalStep>)` defined Task 4, used Tasks 5, 6.
- `TerminologyPipeline` ctor: 5 `[Segment]` params defined Task 5, used Tasks 6, 7.
- `RunTerminologyPipelineIfAvailableAsync` defined Task 7 step 2, called Task 7 step 2 (same task).

No type mismatches。

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-29-vocabulary-dovetail-pipeline-slice-4.md`。

**Execution: Subagent-Driven (per slice 1-3 precedent)。** Proceeding with superpowers:subagent-driven-development。
