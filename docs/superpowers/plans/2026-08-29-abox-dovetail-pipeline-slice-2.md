# Slice 2: ABox Dovetail Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**: Refactor ISEStudio's ABox duplicate-class detection from a 3-stage monolithic service (`DuplicateJudge`) into a 5-stage Dovetail DAG (`CandidateGather` → `EmbeddingMatch` → `LLMJudge` → `MergeApply` → `CascadeRetype`) with auto-apply high-confidence + emit-conflict low-confidence paths; `ConflictService.DetectAsync` becomes a thin forwarder to `ExtractionOrchestrator.RunABoxLayerAsync`.

**Architecture**: Each of the 5 pipeline stages is a thin-shell `[Segment]` class wrapping an existing service (`DuplicateJudge` for candidate generation, `OntologyEditor` for class merge + cascade retype, `AuditLogService` for audit). Dovetail source generator wires the DAG; `ExtractionOrchestrator.RunABoxLayerAsync` prefers the pipeline when DI-registered, falls back to the existing `DuplicateJudge.DetectAsync` service in hand-built test orchestrators.

**Tech Stack**: Dovetail 1.0.0 (NuGet, source generator) · `Microsoft.Extensions.AI 10.9.0` (IChatClient) · `Microsoft.Extensions.DependencyInjection` (IServiceCollection) · ISEStudio existing services (`DuplicateJudge`, `OntologyEditor`, `AuditLogService`, `QuadChangeCapture`)

**Spec**: [docs/superpowers/specs/2026-08-29-abox-dovetail-pipeline-slice-2-design.md](../specs/2026-08-29-abox-dovetail-pipeline-slice-2-design.md)

**Base commit**: `a97bfbe` (slice 1 final + spec locked). Tests baseline: **902 unit / 0 failed / 1 skipped / 903 total**.

---

## Global Constraints

These are the spec's project-wide requirements; every task's requirements implicitly include this section.

1. **Dovetail 1.0.0** is the pinned source-generator package; use `IPipelineSegment<T1, ..., TOut>` multi-input form (no bundle records — DOVE006 forbids them; slice 1 commit `8053735` already established this pattern).
2. **Dovetail pipeline classes** are `public partial class` with `[Segment]` on every ctor parameter; declared `: IPipeline<TIn, TOut>`. The source generator emits `ExecuteAsync` + Mermaid doc comment.
3. **Step classes** are `public sealed` with primary constructors; nullable service dependencies are the rule (e.g. `EmbeddingGeneratorFactory?` for optional stages); service-null path returns the `Enabled: false` no-op wrapper.
4. **DI registration** lives in `DovetailPipelineRegistrations.AddDovetailPipelines()` (slice 1 commit `211bb88`). Extend it for ABox; do not create a separate `AddABoxPipelines()` method.
5. **Orchestrator wire-up** is nullable-seam (`TBoxChunkPipeline? chunkPipeline = null` slice 1 commit `1869199` precedent); `RunABoxLayerAsync` follows the same pattern with `_aboxPipeline ? pipeline : _duplicateJudge` ternary.
6. **Tests baseline** must stay green: 902 unit + 46 integration; new tasks add tests, never subtract. Final target: **~931 unit / 0 failed / 1 skipped** (902 + ~29 new).
7. **Commit trailer** required on every commit: `Co-Authored-By: Claude <noreply@anthropic.com>`. Commit messages in English with `feat(extraction):` / `fix(extraction):` / `docs(extraction):` prefix.
8. **`DuplicateJudge` is NOT deleted** — it stays as the fallback path for hand-built test orchestrators and as the production-callable API for `ConflictService.DetectAsync` when DI doesn't register the pipeline. Slice 1's D1 thin-shell rule.
9. **`DuplicateAutoApplyFloor = 0.90`** (LOCKED in spec §4 D3) is a new `ISEStudioOptions` field added in Task 4. Default value must match.
10. **MergeApply capture per-merge** (LOCKED in spec §4 D5): each kept pair gets its own `QuadChangeCapture` with `revertOnError: false`; per-merge failure does not roll back other successful merges.

---

## Task 1: ABoxJobInputs.cs (8 records) + 6 shape tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/ABox/ABoxJobInputs.cs`
- Test: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxJobInputsTests.cs`

**Interfaces:**
- Consumes: nothing (no prior slice 2 dependencies)
- Produces: 8 sealed records used by every step class (Tasks 2) and the pipeline (Task 3). Records follow slice 1 Task 3's `TBoxChunkInputs.cs` precedent (sealed record + immutable positional + XML doc on each record).

**Type shapes (verbatim from spec §3 + §6.3):**

```csharp
public sealed record CandidatePair(string IriA, string IriB, double? Cosine);

public sealed record CandidateList(IReadOnlyList<CandidatePair> Pairs);

public sealed record JudgeResult(IReadOnlyList<int> KeptIndices, string? Reason);

public sealed record MergedClassPair(string Source, string Target, double Confidence);

public sealed record AppliedMerges(IReadOnlyList<MergedClassPair> Pairs);

public sealed record RemainingConflicts(IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts);

public sealed record CascadeResult(IReadOnlyList<Guid> UpdatedIndividuals);

public sealed record ABoxJobResult(
    AppliedMerges Applied,
    RemainingConflicts Remaining,
    CascadeResult Cascade);

public sealed record ABoxJobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    string GraphIri,
    StoreWrapper Store,
    IChatClient Chat,
    IEmbeddingGenerator<string, Embedding<float>> Embedder,
    double MinConfidence);
```

**Why MergeApply output is a wrapper record**: `MergeApplyStep` produces BOTH `AppliedMerges` (auto-applied high-conf pairs) AND `RemainingConflicts` (low-conf pairs to triage). These are distinct types. Spec §3 lists them as a tuple `(AppliedMerges, RemainingConflicts)`, but Dovetail multi-output segments need a single record type — wrap them in `MergeApplyOutput(AppliedMerges Applied, RemainingConflicts Remaining)`. **Add this record** alongside the 8 above (total: 9 records in this file).

- [ ] **Step 1: Write the failing test**

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxJobInputsTests.cs` with 6 tests (one per "anchor" record — `CandidatePair`, `CandidateList`, `JudgeResult`, `MergedClassPair`, `AppliedMerges`, `RemainingConflicts`, `CascadeResult`, `ABoxJobResult`, `ABoxJobInput`, `MergeApplyOutput` — actually 10 records so 10 tests, but spec said 6; we'll add 6 covering the records that have non-trivial shape — i.e., `CandidateList`, `JudgeResult`, `AppliedMerges`, `RemainingConflicts`, `CascadeResult`, `ABoxJobResult` — the simple 2-field records get tested implicitly via Task 2 step tests):

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox;

public class ABoxJobInputsTests
{
    [Fact]
    public void CandidateList_EmptyConstruction_HasEmptyPairs()
    {
        var list = new CandidateList(Array.Empty<CandidatePair>());
        Assert.Empty(list.Pairs);
    }

    [Fact]
    public void JudgeResult_EmptyKeptIndices_HasNullReason()
    {
        var result = new JudgeResult(Array.Empty<int>(), null);
        Assert.Empty(result.KeptIndices);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void AppliedMerges_EmptyConstruction_HasEmptyPairs()
    {
        var merges = new AppliedMerges(Array.Empty<MergedClassPair>());
        Assert.Empty(merges.Pairs);
    }

    [Fact]
    public void RemainingConflicts_EmptyConstruction_HasEmptyConflicts()
    {
        var conflicts = new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>());
        Assert.Empty(conflicts.Conflicts);
    }

    [Fact]
    public void CascadeResult_EmptyConstruction_HasEmptyIndividuals()
    {
        var cascade = new CascadeResult(Array.Empty<Guid>());
        Assert.Empty(cascade.UpdatedIndividuals);
    }

    [Fact]
    public void ABoxJobResult_AllSubresultsRoundTrip()
    {
        var applied = new AppliedMerges(new[] { new MergedClassPair("a", "b", 0.95) });
        var remaining = new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>());
        var cascade = new CascadeResult(new[] { Guid.NewGuid() });
        var result = new ABoxJobResult(applied, remaining, cascade);
        Assert.Same(applied, result.Applied);
        Assert.Same(remaining, result.Remaining);
        Assert.Same(cascade, result.Cascade);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ABoxJobInputsTests" --nologo`
Expected: FAIL with `The type or namespace name 'ABoxJobInputs' could not be found`.

- [ ] **Step 3: Create `ABoxJobInputs.cs` with 9 records**

Create `src/ISEStudio/Extraction/Dovetail/ABox/ABoxJobInputs.cs`:

```csharp
using ISEStudio.Ontology;
using Microsoft.Extensions.AI;

namespace ISEStudio.Extraction.Dovetail.ABox;

/// <summary>
/// One candidate pair of class IRIs flagged by the candidate-gathering
/// stages (Jaccard + Embedding cosine). <c>Cosine</c> is null when the
/// pair came from the Jaccard stage only and never reached the embedding
/// stage (or the embedding stage was disabled).
/// </summary>
public sealed record CandidatePair(string IriA, string IriB, double? Cosine);

/// <summary>
/// Output of <see cref="Steps.CandidateGatherStep"/> /
/// <see cref="Steps.EmbeddingMatchStep"/>: the deduplicated candidate set
/// handed to <see cref="Steps.LLMJudgeStep"/>.
/// </summary>
public sealed record CandidateList(IReadOnlyList<CandidatePair> Pairs);

/// <summary>
/// Output of <see cref="Steps.LLMJudgeStep"/>: which candidate indices
/// the LLM judged as "same concept" (<c>KeptIndices</c>) plus an
/// optional diagnostic <c>Reason</c> string (e.g. "judge_unavailable"
/// when the LLM call failed and the fail-soft path kept all candidates).
/// </summary>
public sealed record JudgeResult(IReadOnlyList<int> KeptIndices, string? Reason);

/// <summary>
/// One successfully auto-applied class merge with the confidence that
/// passed the <c>DuplicateAutoApplyFloor</c> threshold.
/// </summary>
public sealed record MergedClassPair(string Source, string Target, double Confidence);

/// <summary>
/// Output of <see cref="Steps.MergeApplyStep"/>: pairs the pipeline
/// auto-applied (high-confidence triple-AND through LLM, cosine, jaccard).
/// </summary>
public sealed record AppliedMerges(IReadOnlyList<MergedClassPair> Pairs);

/// <summary>
/// Output of <see cref="Steps.MergeApplyStep"/>: pairs that fell below
/// the <c>DuplicateAutoApplyFloor</c> threshold and are now emitted as
/// <see cref="ConflictDetection.DetectedConflict"/> rows for the
/// triage queue (preserves the existing DuplicateJudge behaviour for
/// low-confidence cases).
/// </summary>
public sealed record RemainingConflicts(IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts);

/// <summary>
/// Output of <see cref="Steps.CascadeRetypeStep"/>: ABox individual IRIs
/// whose <c>rdf:type</c> triple was rewritten from source to target
/// during cascade retype.
/// </summary>
public sealed record CascadeResult(IReadOnlyList<Guid> UpdatedIndividuals);

/// <summary>
/// Output of <c>ABoxJobPipeline</c>: applied merges + triage conflicts
/// + cascade updates, aggregated.
/// </summary>
public sealed record ABoxJobResult(
    AppliedMerges Applied,
    RemainingConflicts Remaining,
    CascadeResult Cascade);

/// <summary>
/// Internal wrapper record for <see cref="Steps.MergeApplyStep"/>'s dual
/// output. Dovetail segments emit a single output type, so the
/// applied-vs-remaining split lives here.
/// </summary>
public sealed record MergeApplyOutput(
    AppliedMerges Applied,
    RemainingConflicts Remaining);

/// <summary>
/// Input to <c>ABoxJobPipeline</c>. Mirrors slice 1's
/// <c>TBoxJobInput</c> shape: explicit dependencies (no hidden service
/// locator) plus <c>KnowledgeSystemId</c> for audit scoping and
/// <c>MinConfidence</c> as the per-run threshold (defaults to
/// <see cref="Configuration.ISEStudioOptions.DuplicateAutoApplyFloor"/>).
/// </summary>
public sealed record ABoxJobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    string GraphIri,
    StoreWrapper Store,
    IChatClient Chat,
    IEmbeddingGenerator<string, Embedding<float>> Embedder,
    double MinConfidence);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ABoxJobInputsTests" --nologo`
Expected: 6 tests pass.

- [ ] **Step 5: Run full unit baseline (sanity check)**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: 908 passed / 0 failed / 1 skipped / 909 total (902 baseline + 6 new).

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/ABox/ABoxJobInputs.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxJobInputsTests.cs
git commit -m "feat(extraction): add ABox Dovetail job input/output records (9 records, 6 tests)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 2: 5 step classes (CandidateGather, EmbeddingMatch, LLMJudge, MergeApply, CascadeRetype) + per-step tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/ABox/Steps/CandidateGatherStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/ABox/Steps/EmbeddingMatchStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/ABox/Steps/LLMJudgeStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/ABox/Steps/MergeApplyStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/ABox/Steps/CascadeRetypeStep.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/CandidateGatherStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/EmbeddingMatchStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/LLMJudgeStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/MergeApplyStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/CascadeRetypeStepTests.cs`

**Interfaces:**
- Consumes: `ABoxJobInput` + the record types from Task 1
- Produces: 5 `IPipelineSegment<...>` step classes, each wired to call an existing service (`DuplicateJudge` / `OntologyEditor` / `AuditLogService`)

**Multi-input contracts** (per spec §4 D2, DOVE006 forbids bundle records):
- `CandidateGatherStep : IPipelineSegment<ABoxJobInput, CandidateList>`
- `EmbeddingMatchStep : IPipelineSegment<ABoxJobInput, CandidateList, CandidateList>` (2 inputs)
- `LLMJudgeStep : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult>` (2 inputs)
- `MergeApplyStep : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult, MergeApplyOutput>` (3 inputs)
- `CascadeRetypeStep : IPipelineSegment<ABoxJobInput, MergeApplyOutput, CascadeResult>` (2 inputs)

**Service wiring** (per spec §4 D1 + D4):
- `CandidateGatherStep(DuplicateJudge? judge)` → calls `DuplicateJudge.StringCandidates(labels)` where `labels = ConflictDetection.ReadClassLabels(input.Store, input.GraphIri)`
- `EmbeddingMatchStep(DuplicateJudge? judge)` → calls `DuplicateJudge.EmbeddingCandidatesAsync(labels, threshold, ct)`. Threshold from `input.MinConfidence` (call it `SemanticCandidateThreshold` reuse)
- `LLMJudgeStep(DuplicateJudge? judge)` → calls `DuplicateJudge.JudgeDuplicatesAsync(pairs, ct)`. Returns `JudgeResult(keptIndices, reason)`. **Fail-soft** on exception: returns `JudgeResult(All, "judge_unavailable")` — see spec §5.1 row "LLMJudge"
- `MergeApplyStep(OntologyEditor? editor, AuditLogService? audit)` → for each kept index, calls `OntologyEditor.ApplyClassMergeAsync(source, target)`. Confidence gate: only apply if `input.MinConfidence >= 0.90` (or pass `DuplicateAutoApplyFloor` directly — Task 4 will inject `ISEStudioOptions` and read `DuplicateAutoApplyFloor`). Below threshold → emit `DetectedConflict`. Per-merge `QuadChangeCapture` with `revertOnError: false` (LOCKED in spec §4 D5). Audit on success + on failure (spec §5.3).
- `CascadeRetypeStep(OntologyEditor? editor, AuditLogService? audit)` → for each applied merge, calls `OntologyEditor.CascadeClassMergeAsync(source, target)`. Audit on success/failure.

- [ ] **Step 1: Write the 5 failing test files**

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/CandidateGatherStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.AI;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class CandidateGatherStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullJudge_ReturnsEmptyCandidateList()
    {
        var step = new CandidateGatherStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,  // not used when judge is null
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);

        var result = await step.ExecuteAsync(input, CancellationToken.None);

        Assert.Empty(result.Pairs);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateJudge_NullStoreReturnsEmpty()
    {
        // DuplicateJudge with null Store should throw ArgumentNullException,
        // so the step propagates — verify that's the contract.
        var judge = new DuplicateJudge(
            new ISEStudio.Llm.EmbeddingGeneratorFactory(NullLogger<ISEStudio.Llm.EmbeddingGeneratorFactory>.Instance),
            chats: null);
        var step = new CandidateGatherStep(judge);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => step.ExecuteAsync(input, CancellationToken.None));
    }
}
```

Note: the second test asserts the existing DuplicateJudge contract — null Store throws `ArgumentNullException` (verified at `src/ISEStudio/Ontology/DuplicateJudge.cs:110`). This locks the failure mode for the step.

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/EmbeddingMatchStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class EmbeddingMatchStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullJudge_ReturnsSameCandidateList()
    {
        var step = new EmbeddingMatchStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(new[]
        {
            new CandidatePair("http://a", "http://b", null),
        });

        var result = await step.ExecuteAsync(input, candidates, CancellationToken.None);

        // null judge = pass-through (caller decided to disable semantic)
        Assert.Same(candidates, result);
    }

    [Fact]
    public async Task ExecuteAsync_NullJudge_EmptyInputReturnsEmpty()
    {
        var step = new EmbeddingMatchStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(Array.Empty<CandidatePair>());

        var result = await step.ExecuteAsync(input, candidates, CancellationToken.None);

        Assert.Empty(result.Pairs);
    }
}
```

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/LLMJudgeStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class LLMJudgeStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullJudge_KeepsAllCandidates()
    {
        var step = new LLMJudgeStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(new[]
        {
            new CandidatePair("http://a", "http://b", 0.9),
            new CandidatePair("http://c", "http://d", 0.7),
        });

        var result = await step.ExecuteAsync(input, candidates, CancellationToken.None);

        // null judge = keep all (caller has no LLM factory wired)
        Assert.Equal(2, result.KeptIndices.Count);
        Assert.Equal("judge_unavailable", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCandidates_ReturnsEmptyKeptIndices()
    {
        var step = new LLMJudgeStep(null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(Array.Empty<CandidatePair>());

        var result = await step.ExecuteAsync(input, candidates, CancellationToken.None);

        Assert.Empty(result.KeptIndices);
        Assert.Null(result.Reason);
    }
}
```

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/MergeApplyStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class MergeApplyStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullEditor_ReturnsEmptyAppliedAndAllRemaining()
    {
        var step = new MergeApplyStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(new[]
        {
            new CandidatePair("http://a", "http://b", 0.95),
        });
        var judge = new JudgeResult(new[] { 0 }, null);

        var result = await step.ExecuteAsync(input, candidates, judge, CancellationToken.None);

        Assert.Empty(result.Applied.Pairs);
        Assert.Single(result.Remaining.Conflicts);
    }

    [Fact]
    public async Task ExecuteAsync_NullJudge_EmptyKeptIndices_ReturnsEmpty()
    {
        var step = new MergeApplyStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var candidates = new CandidateList(Array.Empty<CandidatePair>());
        var judge = new JudgeResult(Array.Empty<int>(), null);

        var result = await step.ExecuteAsync(input, candidates, judge, CancellationToken.None);

        Assert.Empty(result.Applied.Pairs);
        Assert.Empty(result.Remaining.Conflicts);
    }
}
```

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/CascadeRetypeStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class CascadeRetypeStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullEditor_ReturnsEmptyCascade()
    {
        var step = new CascadeRetypeStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var mergeOutput = new MergeApplyOutput(
            Applied: new AppliedMerges(Array.Empty<MergedClassPair>()),
            Remaining: new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>()));

        var result = await step.ExecuteAsync(input, mergeOutput, CancellationToken.None);

        Assert.Empty(result.UpdatedIndividuals);
    }

    [Fact]
    public async Task ExecuteAsync_NoAppliedMerges_ReturnsEmptyCascade()
    {
        var step = new CascadeRetypeStep(null, audit: null);
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var mergeOutput = new MergeApplyOutput(
            Applied: new AppliedMerges(new[] { new MergedClassPair("a", "b", 0.95) }),
            Remaining: new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>()));

        // null editor means cascade is a no-op — return empty without touching the merge list
        var result = await step.ExecuteAsync(input, mergeOutput, CancellationToken.None);

        Assert.Empty(result.UpdatedIndividuals);
    }
}
```

- [ ] **Step 2: Run all step tests to verify they fail**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Steps.CandidateGatherStepTests|FullyQualifiedName~Steps.EmbeddingMatchStepTests|FullyQualifiedName~Steps.LLMJudgeStepTests|FullyQualifiedName~Steps.MergeApplyStepTests|FullyQualifiedName~Steps.CascadeRetypeStepTests" --nologo`
Expected: 8 tests fail with `The type or namespace name '*Step' could not be found`.

- [ ] **Step 3: Create `CandidateGatherStep.cs`**

Create `src/ISEStudio/Extraction/Dovetail/ABox/Steps/CandidateGatherStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 1 of ABoxJobPipeline: read class labels from the graph and
/// produce the candidate pair set via Jaccard string similarity at or
/// above the Python <c>DUP_THRESHOLD = 0.86</c> floor
/// (<see cref="DuplicateJudge.StringCandidates"/>).
/// <para>
/// When <see cref="DuplicateJudge"/> is null (DI did not register the
/// optional service), this step returns an empty <see cref="CandidateList"/>
/// — same fail-soft contract as the other ABox steps.
/// </para>
/// </summary>
public sealed class CandidateGatherStep(DuplicateJudge? judge)
    : IPipelineSegment<ABoxJobInput, CandidateList>
{
    private readonly DuplicateJudge? _judge = judge;

    public async Task<CandidateList> ExecuteAsync(ABoxJobInput input, CancellationToken cancellationToken)
    {
        if (_judge is null)
        {
            return new CandidateList(Array.Empty<CandidatePair>());
        }

        var labels = ConflictDetection.ReadClassLabels(input.Store, input.GraphIri);
        var pairs = DuplicateJudge.StringCandidates(labels);
        return await Task.FromResult(new CandidateList(
            pairs.Select(p => new CandidatePair(p.IriA, p.IriB, Cosine: null)).ToList()));
    }
}
```

- [ ] **Step 4: Create `EmbeddingMatchStep.cs`**

Create `src/ISEStudio/Extraction/Dovetail/ABox/Steps/EmbeddingMatchStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 2 of ABoxJobPipeline: enrich the candidate set with embedding
/// cosine scores from <see cref="DuplicateJudge.EmbeddingCandidatesAsync"/>.
/// Pass-through when <see cref="DuplicateJudge"/> is null
/// (semantic-conflicts disabled).
/// </summary>
public sealed class EmbeddingMatchStep(DuplicateJudge? judge)
    : IPipelineSegment<ABoxJobInput, CandidateList, CandidateList>
{
    private readonly DuplicateJudge? _judge = judge;

    public async Task<CandidateList> ExecuteAsync(
        ABoxJobInput input,
        CandidateList candidates,
        CancellationToken cancellationToken)
    {
        if (_judge is null || candidates.Pairs.Count == 0)
        {
            return candidates;
        }

        // Reconstruct the (IriA, IriB) tuple list for the legacy service.
        var pairs = candidates.Pairs.Select(p => (p.IriA, p.IriB)).ToList();
        var cosineResults = await _judge.EmbeddingCandidatesAsync(
            ConflictDetection.ReadClassLabels(input.Store, input.GraphIri),
            input.MinConfidence,
            cancellationToken).ConfigureAwait(false);

        var cosineMap = cosineResults.ToDictionary(r => r.Pair, r => r.Cosine);
        var merged = candidates.Pairs.Select(p =>
        {
            var key = (p.IriA, p.IriB);
            var reverse = (p.IriB, p.IriA);
            var cos = cosineMap.TryGetValue(key, out var c)
                ? c
                : cosineMap.TryGetValue(reverse, out c) ? c : null;
            return new CandidatePair(p.IriA, p.IriB, cos);
        }).ToList();

        return new CandidateList(merged);
    }
}
```

- [ ] **Step 5: Create `LLMJudgeStep.cs`**

Create `src/ISEStudio/Extraction/Dovetail/ABox/Steps/LLMJudgeStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 3 of ABoxJobPipeline: LLM-judge which candidate pairs are true
/// synonyms via <see cref="DuplicateJudge.JudgeDuplicatesAsync"/>.
/// Fail-soft on judge unavailability: when <see cref="DuplicateJudge"/>
/// is null OR the LLM call fails, all candidates are kept with reason
/// <c>judge_unavailable</c> so the cosine + jaccard layers act as
/// fallback filters.
/// </summary>
public sealed class LLMJudgeStep(DuplicateJudge? judge)
    : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult>
{
    private readonly DuplicateJudge? _judge = judge;

    public async Task<JudgeResult> ExecuteAsync(
        ABoxJobInput input,
        CandidateList candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Pairs.Count == 0)
        {
            return new JudgeResult(Array.Empty<int>(), Reason: null);
        }

        if (_judge is null)
        {
            var allIndices = Enumerable.Range(0, candidates.Pairs.Count).ToList();
            return new JudgeResult(allIndices, Reason: "judge_unavailable");
        }

        var pairLabels = candidates.Pairs
            .Select(p => (LabelFromIri(p.IriA), LabelFromIri(p.IriB)))
            .ToList();

        try
        {
            var kept = await _judge.JudgeDuplicatesAsync(pairLabels, cancellationToken)
                .ConfigureAwait(false);
            return new JudgeResult(kept.ToList(), Reason: null);
        }
        catch (Exception)
        {
            // Fail-open: keep all candidates with reason marker. The cosine
            // + jaccard upstream layers still filter out unrelated pairs.
            var allIndices = Enumerable.Range(0, candidates.Pairs.Count).ToList();
            return new JudgeResult(allIndices, Reason: "judge_unavailable");
        }
    }

    private static string LabelFromIri(string iri) => iri;
}
```

- [ ] **Step 6: Create `MergeApplyStep.cs`**

Create `src/ISEStudio/Extraction/Dovetail/ABox/Steps/MergeApplyStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Audit;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 4 of ABoxJobPipeline: for each judge-kept pair, decide between
/// auto-applying the merge (high-confidence) or emitting a
/// <see cref="ConflictDetection.DetectedConflict"/> for triage (low-confidence).
/// Threshold comes from <see cref="ABoxJobInput.MinConfidence"/>, which the
/// orchestrator wires from <c>ISEStudioOptions.DuplicateAutoApplyFloor</c>.
///
/// Per-merge <c>QuadChangeCapture</c> with <c>revertOnError:false</c> per
/// spec §4 D5 (LOCKED): one failed merge does not roll back successful ones.
/// </summary>
public sealed class MergeApplyStep(
    OntologyEditor? editor,
    AuditLogService? audit) : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult, MergeApplyOutput>
{
    private readonly OntologyEditor? _editor = editor;
    private readonly AuditLogService? _audit = audit;

    public async Task<MergeApplyOutput> ExecuteAsync(
        ABoxJobInput input,
        CandidateList candidates,
        JudgeResult judge,
        CancellationToken cancellationToken)
    {
        if (_editor is null || judge.KeptIndices.Count == 0)
        {
            return new MergeApplyOutput(
                Applied: new AppliedMerges(Array.Empty<MergedClassPair>()),
                Remaining: new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>()));
        }

        var applied = new List<MergedClassPair>();
        var remaining = new List<ConflictDetection.DetectedConflict>();

        foreach (var idx in judge.KeptIndices)
        {
            if (idx < 0 || idx >= candidates.Pairs.Count) continue;
            var pair = candidates.Pairs[idx];

            // Confidence gate: cosine present + >= floor → auto-apply.
            var cosine = pair.Cosine ?? 0.0;
            var passesConfidence = cosine >= input.MinConfidence;

            if (passesConfidence)
            {
                try
                {
                    await _editor.ApplyClassMergeAsync(
                        input.Store, input.GraphIri, pair.IriA, pair.IriB, cancellationToken)
                        .ConfigureAwait(false);
                    applied.Add(new MergedClassPair(pair.IriA, pair.IriB, cosine));

                    if (_audit is not null)
                    {
                        await _audit.LogAsync(new AuditEventEntity
                        {
                            ActorName = "abox-dovetail-pipeline",
                            Action = "duplicate.merge",
                            Detail = $"{{\"source\":\"{pair.IriA}\",\"target\":\"{pair.IriB}\",\"confidence\":{cosine:F2}}}",
                            Graph = input.GraphIri,
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // Per-merge isolation: log + continue.
                    if (_audit is not null)
                    {
                        await _audit.LogAsync(new AuditEventEntity
                        {
                            ActorName = "abox-dovetail-pipeline",
                            Action = "duplicate.merge.failed",
                            Detail = $"{{\"source\":\"{pair.IriA}\",\"target\":\"{pair.IriB}\",\"error\":\"{ex.GetType().Name}\"}}",
                            Graph = input.GraphIri,
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // Below threshold: emit conflict for triage (preserve DuplicateJudge behaviour).
                var conflict = new ConflictDetection.DetectedConflict(
                    Signature: "duplicate|" + string.Join("|", new[] { pair.IriA, pair.IriB }.OrderBy(s => s, StringComparer.Ordinal)),
                    Ctype: "duplicate",
                    Severity: "warning",
                    Title: "Possible duplicate classes (low confidence)",
                    Detail: $"\"{pair.IriA}\" and \"{pair.IriB}\" look similar but cosine {cosine:F2} below floor {input.MinConfidence:F2}.",
                    Entities: new[]
                    {
                        new ConflictDetection.EntityRef(pair.IriA, pair.IriA),
                        new ConflictDetection.EntityRef(pair.IriB, pair.IriB),
                    },
                    Resolutions: Array.Empty<ConflictDetection.Resolution>());
                remaining.Add(conflict);
            }
        }

        return new MergeApplyOutput(
            new AppliedMerges(applied),
            new RemainingConflicts(remaining));
    }
}
```

- [ ] **Step 7: Create `CascadeRetypeStep.cs`**

Create `src/ISEStudio/Extraction/Dovetail/ABox/Steps/CascadeRetypeStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Audit;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Stage 5 of ABoxJobPipeline: for each auto-applied merge, run
/// <see cref="OntologyEditor.CascadeClassMergeAsync"/> to retype dependent
/// ABox individuals. Pass-through when <see cref="OntologyEditor"/> is null.
/// Per-merge isolation: one cascade failure does not roll back prior merges.
/// </summary>
public sealed class CascadeRetypeStep(
    OntologyEditor? editor,
    AuditLogService? audit) : IPipelineSegment<ABoxJobInput, MergeApplyOutput, CascadeResult>
{
    private readonly OntologyEditor? _editor = editor;
    private readonly AuditLogService? _audit = audit;

    public async Task<CascadeResult> ExecuteAsync(
        ABoxJobInput input,
        MergeApplyOutput mergeOutput,
        CancellationToken cancellationToken)
    {
        if (_editor is null || mergeOutput.Applied.Pairs.Count == 0)
        {
            return new CascadeResult(Array.Empty<Guid>());
        }

        var updated = new List<Guid>();

        foreach (var merge in mergeOutput.Applied.Pairs)
        {
            try
            {
                var cascadeIndividuals = await _editor.CascadeClassMergeAsync(
                    input.Store, input.GraphIri, merge.Source, merge.Target, cancellationToken)
                    .ConfigureAwait(false);
                updated.AddRange(cascadeIndividuals);

                if (_audit is not null && cascadeIndividuals.Count > 0)
                {
                    await _audit.LogAsync(new AuditEventEntity
                    {
                        ActorName = "abox-dovetail-pipeline",
                        Action = "duplicate.cascade",
                        Detail = $"{{\"source\":\"{merge.Source}\",\"target\":\"{merge.Target}\",\"retype_count\":{cascadeIndividuals.Count}}}",
                        Graph = input.GraphIri,
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (_audit is not null)
                {
                    await _audit.LogAsync(new AuditEventEntity
                    {
                        ActorName = "abox-dovetail-pipeline",
                        Action = "duplicate.cascade.failed",
                        Detail = $"{{\"source\":\"{merge.Source}\",\"target\":\"{merge.Target}\",\"error\":\"{ex.GetType().Name}\"}}",
                        Graph = input.GraphIri,
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new CascadeResult(updated);
    }
}
```

- [ ] **Step 8: Run all step tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Steps.CandidateGatherStepTests|FullyQualifiedName~Steps.EmbeddingMatchStepTests|FullyQualifiedName~Steps.LLMJudgeStepTests|FullyQualifiedName~Steps.MergeApplyStepTests|FullyQualifiedName~Steps.CascadeRetypeStepTests" --nologo`
Expected: 8 step tests pass.

Note: tests reference `ISEStudio.Llm.EmbeddingGeneratorFactory` and `NullLogger<T>`. If `NullLogger<T>` requires `using Microsoft.Extensions.Logging.Abstractions;`, add the using to the test file.

- [ ] **Step 9: Run full unit baseline (sanity check)**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: 916 passed / 0 failed / 1 skipped / 917 total (908 baseline after Task 1 + 8 new step tests).

- [ ] **Step 10: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/ABox/Steps/ \
        src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/
git commit -m "feat(extraction): add ABox Dovetail 5 step classes (CandidateGather/EmbeddingMatch/LLMJudge/MergeApply/CascadeRetype)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 3: ABoxJobPipeline partial class + happy-path test

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/ABox/ABoxJobPipeline.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxJobPipelineTests.cs`

**Interfaces:**
- Consumes: 5 step classes from Task 2 + ABoxJobInput/Output records from Task 1
- Produces: `ABoxJobPipeline : IPipeline<ABoxJobInput, ABoxJobResult>` partial class with 5 `[Segment]` parameters; Dovetail source generator emits `ExecuteAsync`

**Note on the output type**: Task 2's `MergeApplyStep` produces `MergeApplyOutput` (applied + remaining). The pipeline needs to produce `ABoxJobResult` (applied + remaining + cascade). A 6th step (or the `MergeApplyOutput` cascades through `CascadeRetypeStep` which then merges both into `ABoxJobResult`) handles this.

Solution: add a 6th implicit `[Segment]` step `FinalMergeStep` that reads `MergeApplyOutput` + `CascadeResult` and emits `ABoxJobResult`. This is the slice 1 TBox `JobMergeStep` precedent (Task 7 brief step (d)).

Update Task 2's pipeline shape: 6 segments total (5 step classes + 1 final merge). Step contracts:

- `CandidateGatherStep : IPipelineSegment<ABoxJobInput, CandidateList>` (1 input)
- `EmbeddingMatchStep : IPipelineSegment<ABoxJobInput, CandidateList, CandidateList>` (2 inputs)
- `LLMJudgeStep : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult>` (2 inputs)
- `MergeApplyStep : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult, MergeApplyOutput>` (3 inputs)
- `CascadeRetypeStep : IPipelineSegment<ABoxJobInput, MergeApplyOutput, CascadeResult>` (2 inputs)
- `FinalMergeStep : IPipelineSegment<ABoxJobInput, MergeApplyOutput, CascadeResult, ABoxJobResult>` (3 inputs, no service dependency, pure function)

Add `FinalMergeStep` to Task 2's file list. Add 1 test for FinalMergeStep in Task 2's per-step tests.

- [ ] **Step 1: Add `FinalMergeStep.cs` (6th step)**

Create `src/ISEStudio/Extraction/Dovetail/ABox/Steps/FinalMergeStep.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;

namespace ISEStudio.Extraction.Dovetail.ABox.Steps;

/// <summary>
/// Final step of ABoxJobPipeline: bundle MergeApplyOutput + CascadeResult
/// into the ABoxJobResult. Pure function, no LLM, no service dependencies.
/// Multi-input form (DOVE006 forbids bundle records).
/// </summary>
public sealed class FinalMergeStep
    : IPipelineSegment<ABoxJobInput, MergeApplyOutput, CascadeResult, ABoxJobResult>
{
    public Task<ABoxJobResult> ExecuteAsync(
        ABoxJobInput input,
        MergeApplyOutput mergeOutput,
        CascadeResult cascade,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ABoxJobResult(
            Applied: mergeOutput.Applied,
            Remaining: mergeOutput.Remaining,
            Cascade: cascade));
}
```

- [ ] **Step 2: Add `FinalMergeStepTests.cs`**

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/FinalMergeStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox.Steps;

public class FinalMergeStepTests
{
    [Fact]
    public async Task ExecuteAsync_RoundTripsAllThreeSubresults()
    {
        var step = new FinalMergeStep();
        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: null!,
            Embedder: null!,
            MinConfidence: 0.90);
        var mergeOutput = new MergeApplyOutput(
            Applied: new AppliedMerges(new[] { new MergedClassPair("a", "b", 0.95) }),
            Remaining: new RemainingConflicts(Array.Empty<ConflictDetection.DetectedConflict>()));
        var cascade = new CascadeResult(new[] { Guid.NewGuid() });

        var result = await step.ExecuteAsync(input, mergeOutput, cascade, CancellationToken.None);

        Assert.Same(mergeOutput.Applied, result.Applied);
        Assert.Same(mergeOutput.Remaining, result.Remaining);
        Assert.Same(cascade, result.Cascade);
    }
}
```

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Steps.FinalMergeStepTests" --nologo`
Expected: 1 test passes.

- [ ] **Step 3: Write the pipeline failing test**

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxJobPipelineTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.AI;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox;

public class ABoxJobPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyInputs_ReturnsEmptyResult()
    {
        var pipeline = new ABoxJobPipeline(
            gather: new CandidateGatherStep(null),
            embed: new EmbeddingMatchStep(null),
            judge: new LLMJudgeStep(null),
            merge: new MergeApplyStep(null, audit: null),
            cascade: new CascadeRetypeStep(null, audit: null),
            final: new FinalMergeStep());

        var input = new ABoxJobInput(
            JobId: Guid.NewGuid(),
            KnowledgeSystemId: Guid.NewGuid(),
            GraphIri: "http://example.org/g",
            Store: null!,
            Chat: new NullChat(),
            Embedder: null!,
            MinConfidence: 0.90);

        var output = await pipeline.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(output);
        Assert.Empty(output.Applied.Pairs);
        Assert.Empty(output.Remaining.Conflicts);
        Assert.Empty(output.Cascade.UpdatedIndividuals);
    }

    private sealed class NullChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
```

- [ ] **Step 4: Run pipeline test to verify it fails**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ABoxJobPipelineTests" --nologo`
Expected: FAIL with `The type or namespace name 'ABoxJobPipeline' could not be found`.

- [ ] **Step 5: Create `ABoxJobPipeline.cs`**

Create `src/ISEStudio/Extraction/Dovetail/ABox/ABoxJobPipeline.cs`:

```csharp
using Dovetail;
using ISEStudio.Extraction.Dovetail.ABox.Steps;

namespace ISEStudio.Extraction.Dovetail.ABox;

/// <summary>
/// Job-level ABox pipeline: candidate gathering → embedding match →
/// LLM judge → merge apply → cascade retype → final merge.
/// <![CDATA[
/// graph TD
///   gather --> embed
///   embed --> judge
///   judge --> merge
///   merge --> cascade
///   cascade --> final
/// ]]>
/// </summary>
public partial class ABoxJobPipeline(
    [Segment] CandidateGatherStep gather,
    [Segment] EmbeddingMatchStep embed,
    [Segment] LLMJudgeStep judge,
    [Segment] MergeApplyStep merge,
    [Segment] CascadeRetypeStep cascade,
    [Segment] FinalMergeStep final) : IPipeline<ABoxJobInput, ABoxJobResult>;
```

- [ ] **Step 6: Run pipeline test to verify it passes**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ABoxJobPipelineTests" --nologo`
Expected: 1 test passes.

- [ ] **Step 7: Run full unit baseline**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: 918 passed / 0 failed / 1 skipped / 919 total (916 after Task 2 + 2 new: 1 FinalMergeStep + 1 ABoxJobPipeline).

- [ ] **Step 8: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/ABox/Steps/FinalMergeStep.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/ABox/Steps/FinalMergeStepTests.cs \
        src/ISEStudio/Extraction/Dovetail/ABox/ABoxJobPipeline.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxJobPipelineTests.cs
git commit -m "feat(extraction): add Dovetail ABoxJobPipeline (6-stage partial-order DAG)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 4: DI registration extension + `DuplicateAutoApplyFloor` option + 5 new tests

**Files:**
- Modify: `src/ISEStudio/Configuration/ISEStudioOptions.cs`
- Modify: `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxPipelineRegistrationsTests.cs`

**Interfaces:**
- Consumes: `ISEStudioOptions.DuplicateAutoApplyFloor` (new field, default `0.90`)
- Produces: `DovetailPipelineRegistrations.AddDovetailPipelines()` extended to register all 6 ABox step classes

**Registration order matters** (slice 1 Task 8 lesson): `services.AddPipelines()` first so Dovetail can scan the assembly; concrete `services.AddSingleton<T>()` calls then follow.

- [ ] **Step 1: Write the failing tests**

Create `src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxPipelineRegistrationsTests.cs`:

```csharp
using Dovetail;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox;

public class ABoxPipelineRegistrationsTests
{
    [Fact]
    public void AddDovetailPipelines_RegistersABoxJobPipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<ABoxJobPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AddDovetailPipelines_RegistersAllABoxStepClasses()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<CandidateGatherStep>());
        Assert.NotNull(sp.GetService<EmbeddingMatchStep>());
        Assert.NotNull(sp.GetService<LLMJudgeStep>());
        Assert.NotNull(sp.GetService<MergeApplyStep>());
        Assert.NotNull(sp.GetService<CascadeRetypeStep>());
        Assert.NotNull(sp.GetService<FinalMergeStep>());
    }

    [Fact]
    public void AddDovetailPipelines_RegistersABoxSteps_WithNullableServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        // Steps accept nullable service deps; if DuplicateJudge / OntologyEditor
        // are not registered, the step is still resolvable with null services.
        var gather = sp.GetRequiredService<CandidateGatherStep>();
        var merge = sp.GetRequiredService<MergeApplyStep>();
        Assert.NotNull(gather);
        Assert.NotNull(merge);
    }

    [Fact]
    public void DuplicateAutoApplyFloor_Default_Is090()
    {
        var options = new ISEStudioOptions();
        Assert.Equal(0.90, options.DuplicateAutoApplyFloor);
    }

    [Fact]
    public void DuplicateAutoApplyFloor_CanBeOverridden()
    {
        var options = new ISEStudioOptions { DuplicateAutoApplyFloor = 0.95 };
        Assert.Equal(0.95, options.DuplicateAutoApplyFloor);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ABoxPipelineRegistrationsTests" --nologo`
Expected: 5 tests fail (DuplicateAutoApplyFloor doesn't exist + ABoxJobPipeline not registered).

- [ ] **Step 3: Add `DuplicateAutoApplyFloor` to `ISEStudioOptions`**

Open `src/ISEStudio/Configuration/ISEStudioOptions.cs`. Add a new property after the existing `AutoApplyFloor` field:

```csharp
/// <summary>
/// Confidence floor for auto-applying duplicate-class merges in the
/// ABox Dovetail pipeline (Slice 2). Below this threshold the pipeline
/// emits a <c>DetectedConflict</c> for triage instead of auto-applying.
/// Default 0.90 (LOCKED in slice 2 spec §4 D3) — stricter than the
/// P3-11 conflict-agent's <see cref="AutoApplyFloor"/> (0.85) because
/// duplicate-class merges cascade into ABox individual retype.
/// </summary>
public double DuplicateAutoApplyFloor { get; set; } = 0.90;
```

- [ ] **Step 4: Extend `DovetailPipelineRegistrations.AddDovetailPipelines()`**

Open `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`. Add ABox step registrations inside `AddDovetailPipelines()` AFTER the existing job-level step registrations but BEFORE the return statement:

```csharp
        // 6. ABox-level step classes (Slice 2). All nullable service
        // dependencies — DI registers them with whatever services are
        // available; missing services yield steps with null service refs
        // (fail-soft path; see spec §4 D4).
        services.AddSingleton<CandidateGatherStep>(sp =>
            new CandidateGatherStep(sp.GetService<DuplicateJudge>()));
        services.AddSingleton<EmbeddingMatchStep>(sp =>
            new EmbeddingMatchStep(sp.GetService<DuplicateJudge>()));
        services.AddSingleton<LLMJudgeStep>(sp =>
            new LLMJudgeStep(sp.GetService<DuplicateJudge>()));
        services.AddSingleton<MergeApplyStep>(sp =>
            new MergeApplyStep(sp.GetService<OntologyEditor>(), sp.GetService<AuditLogService>()));
        services.AddSingleton<CascadeRetypeStep>(sp =>
            new CascadeRetypeStep(sp.GetService<OntologyEditor>(), sp.GetService<AuditLogService>()));
        services.AddSingleton<FinalMergeStep>();
```

Verify imports at the top of the file include:
- `using ISEStudio.Extraction.Dovetail.ABox;`
- `using ISEStudio.Extraction.Dovetail.ABox.Steps;`
- `using ISEStudio.Ontology;` (for `DuplicateJudge`)

If `AuditLogService` namespace differs, adjust. Check existing code for the AuditLogService namespace location; likely `ISEStudio.Extraction.Audit`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ABoxPipelineRegistrationsTests" --nologo`
Expected: 5 tests pass.

- [ ] **Step 6: Run full unit baseline**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: 923 passed / 0 failed / 1 skipped / 924 total (918 baseline + 5 new).

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Configuration/ISEStudioOptions.cs \
        src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/ABox/ABoxPipelineRegistrationsTests.cs
git commit -m "feat(extraction): wire ABoxJobPipeline into DI + add DuplicateAutoApplyFloor=0.90

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 5: ExtractionOrchestrator.RunABoxLayerAsync wire-up + ConflictService forwarder

**Files:**
- Modify: `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`
- Modify: `src/ISEStudio/Conflicts/ConflictService.cs`
- Modify: `src/ISEStudio/Conflicts/ConflictServiceCollectionExtensions.cs`
- Create: `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorABoxPipelineTests.cs`
- Modify (tests): `src/ISEStudio.Tests/Conflicts/ConflictServiceTests.cs` (forwarder assertion update if needed)

**Interfaces:**
- Consumes: `ABoxJobPipeline?` (nullable seam), `DuplicateJudge` (fallback), `IOptions<ISEStudioOptions>` (for `DuplicateAutoApplyFloor`)
- Produces: `RunABoxLayerAsync(ksId, ct)` method that prefers the pipeline when DI-registered; `ConflictService.DetectAsync` becomes a single-line forwarder

- [ ] **Step 1: Write the failing tests**

Create `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorABoxPipelineTests.cs`:

```csharp
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction;

public class ExtractionOrchestratorABoxPipelineTests
{
    [Fact]
    public void ABoxJobPipeline_IsResolvable_FromOrchestratorServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddSingleton<DuplicateJudge>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<ABoxJobPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void ABoxJobPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        // Intentionally NOT calling AddDovetailPipelines().
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<ABoxJobPipeline>();
        Assert.Null(pipeline);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (DI resolvability should pass after Task 4)**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ExtractionOrchestratorABoxPipelineTests" --nologo`
Expected: 2 tests pass (Task 4 already wired DI). If negative test passes only, the contract is locked — proceed to Step 3.

- [ ] **Step 3: Add `RunABoxLayerAsync` to `ExtractionOrchestrator`**

Open `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`. Three surgical changes:

**(a)** Add field after `_chunkPipeline` (slice 1 line ~121):

```csharp
/// <summary>
/// Dovetail-generated ABox job-level pipeline (candidate gather → embedding
/// match → LLM judge → merge apply → cascade retype → final merge). Preferred
/// over <see cref="_duplicateJudge"/> when registered in DI; the judge is the
/// legacy fallback. Null in hand-built test orchestrators that bypass DI.
/// </summary>
private readonly ABoxJobPipeline? _aboxPipeline;
```

**(b)** Add ctor parameter AFTER the existing `chunkPipeline` parameter (continue the trailing-optional-seam convention from slice 1):

```csharp
ABoxJobPipeline? aboxPipeline = null,
```

Assign it in the ctor body:

```csharp
_aboxPipeline = aboxPipeline;
```

**(c)** Add the `RunABoxLayerAsync` method (alongside the existing `ExtractAndVerifyAsync`):

```csharp
/// <summary>
/// Run the ABox duplicate-class detection pipeline. When
/// <c>_aboxPipeline</c> is registered (production DI), runs the 6-stage
/// Dovetail DAG and returns the job result (applied merges + remaining
/// conflicts + cascade updates). When null (hand-built test
/// orchestrators), falls back to <see cref="DuplicateJudge.DetectAsync"/>
/// and synthesizes a minimal <see cref="ABoxJobResult"/> from the
/// detected conflicts.
/// </summary>
public async Task<ABoxJobResult> RunABoxLayerAsync(
    Guid knowledgeSystemId,
    string graphIri,
    StoreWrapper store,
    IChatClient chat,
    IEmbeddingGenerator<string, Embedding<float>>? embedder,
    CancellationToken cancellationToken)
{
    var options = _optionsAccessor.Value;
    var input = new ABoxJobInput(
        JobId: Guid.NewGuid(),
        KnowledgeSystemId: knowledgeSystemId,
        GraphIri: graphIri,
        Store: store,
        Chat: chat,
        Embedder: embedder ?? throw new ArgumentNullException(nameof(embedder)),
        MinConfidence: options.DuplicateAutoApplyFloor);

    if (_aboxPipeline is not null)
    {
        return await _aboxPipeline.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
    }

    // Fallback: invoke the legacy DuplicateJudge and synthesize a result.
    var conflicts = await _duplicateJudge.DetectAsync(store, graphIri, cancellationToken)
        .ConfigureAwait(false);
    return new ABoxJobResult(
        Applied: new AppliedMerges(Array.Empty<MergedClassPair>()),
        Remaining: new RemainingConflicts(conflicts),
        Cascade: new CascadeResult(Array.Empty<Guid>()));
}
```

Verify imports at the top of the file include:
- `using ISEStudio.Extraction.Dovetail.ABox;`
- `using Microsoft.Extensions.AI;` (for IEmbeddingGenerator)
- A field `_duplicateJudge` exists or is added (nullable per fallback pattern)

If `_duplicateJudge` does not exist, add it as a nullable field with a ctor parameter:

```csharp
private readonly DuplicateJudge? _duplicateJudge;

DuplicateJudge? duplicateJudge = null,
```

and ctor body:

```csharp
_duplicateJudge = duplicateJudge;
```

Inject `IOptions<ISEStudioOptions> _optionsAccessor` if not already present (slice 1 may have used it for `AutoApplyFloor`).

- [ ] **Step 4: Update `ConflictService.DetectAsync` to be a forwarder**

Open `src/ISEStudio/Conflicts/ConflictService.cs`. Find the existing `DetectAsync` method that calls `_duplicateJudge.DetectAsync(...)` (around line 127 per slice 1 spec pre-flight). Replace the body to forward to `ExtractionOrchestrator.RunABoxLayerAsync`:

```csharp
public async Task<IReadOnlyList<ConflictOut>> DetectAsync(Guid ksId, CancellationToken ct)
{
    // Slice 2: forwarder to ExtractionOrchestrator.RunABoxLayerAsync.
    // The orchestrator prefers the Dovetail ABoxJobPipeline when registered
    // and falls back to the legacy DuplicateJudge otherwise (spec §4 D6).
    var outcome = await _extraction.RunABoxLayerAsync(
        ksId,
        graphIri: _store.GetGraphIri(ksId),  // adjust to existing accessor
        store: _store,
        chat: _chats?.Create(...),  // adjust to existing chat factory
        embedder: _embeddingFactory?.Create(...),  // adjust to existing factory
        ct).ConfigureAwait(false);

    return outcome.Remaining.Conflicts.Select(_conflictMapper.ToConflictOut).ToList();
}
```

**Important**: the exact forwarder body depends on the existing `ConflictService` field signatures (chat factory, embedding factory, store accessor). The implementer must read the current `ConflictService.cs` constructor + fields to match the existing access pattern. The sketch above is illustrative; copy field names + access methods from the existing class.

If `_extraction` is not a field of `ConflictService`, add it as a ctor parameter (last position for binary compat):

```csharp
ExtractionOrchestrator extraction,
```

at the END of the existing ctor parameter list.

- [ ] **Step 5: Update `ConflictServiceCollectionExtensions` DI**

Open `src/ISEStudio/Conflicts/ConflictServiceCollectionExtensions.cs`. Add the orchestrator registration:

```csharp
services.AddScoped<ExtractionOrchestrator>();
```

(or `AddSingleton<ExtractionOrchestrator>()` if the orchestrator is registered as singleton elsewhere — check the existing registration in `ExtractionServiceCollectionExtensions.cs` for the lifetime).

Update `services.AddScoped<DuplicateJudge>();` to remain (still needed as fallback when the pipeline is not registered).

- [ ] **Step 6: Run orchestrator + conflict tests**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ExtractionOrchestratorABoxPipelineTests|FullyQualifiedName~ConflictServiceTests" --nologo`
Expected: all pass.

- [ ] **Step 7: Run full unit baseline**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: 925 passed / 0 failed / 1 skipped / 926 total (923 baseline + 2 new orchestrator tests; existing `ConflictServiceTests` continue to pass with the forwarder change).

- [ ] **Step 8: Run integration baseline**

Run: `dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --nologo`
Expected: 46 passed / 0 failed. Confirm no regression in the conflict detection integration path.

- [ ] **Step 9: Commit**

```bash
git add src/ISEStudio/Extraction/ExtractionOrchestrator.cs \
        src/ISEStudio/Conflicts/ConflictService.cs \
        src/ISEStudio/Conflicts/ConflictServiceCollectionExtensions.cs \
        src/ISEStudio.Tests/Extraction/ExtractionOrchestratorABoxPipelineTests.cs \
        src/ISEStudio.Tests/Conflicts/ConflictServiceTests.cs
git commit -m "feat(extraction): wire ABoxJobPipeline into RunABoxLayerAsync (ABox branch)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 6: dovetail-report HTML for ABox pipeline

**Files:**
- Create: `docs/superpowers/diagrams/extraction-abox-dag/index.html` (auto-generated)
- Create: `docs/superpowers/diagrams/extraction-abox-dag/ISEStudio.Extraction.Dovetail.ABox.ABoxJobPipeline.html` (auto-generated)
- Create: `docs/superpowers/diagrams/extraction-abox-dag/vendor/mermaid.min.js` (vendored)
- Create: `docs/superpowers/diagrams/extraction-abox-dag/vendor/pico.indigo.min.css` (vendored)

**Tool**: `dovetail-report` 1.0.0 from `dotnet tool install --global Dovetail.Report --version 1.0.0` (slice 1 Task 10 precedent, install source: nuget.org first-try success).

- [ ] **Step 1: Install `dovetail-report` 1.0.0**

```bash
dotnet tool install --global Dovetail.Report --version 1.0.0
```

Expected: installation succeeds. If nuget.org unreachable, fallback to local pack (slice 1 Task 10 brief Step 1 alternative):
```bash
dotnet pack E:\GitHub\Dovetail\Dovetail.Report\Dovetail.Report.csproj -c Release -o ./local-nuget
dotnet tool install --global Dovetail.Report --version 1.0.0 --add-source ./local-nuget
```

If both fail: write `DONE_WITH_CONCERNS` in the report and stop.

- [ ] **Step 2: Generate the ABox sub-DAG report**

```bash
mkdir -p docs/superpowers/diagrams
dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams/extraction-abox-dag
```

Expected: command exits 0; `docs/superpowers/diagrams/extraction-abox-dag/` now has at least `index.html` + `ISEStudio.Extraction.Dovetail.ABox.ABoxJobPipeline.html` + `vendor/`.

If `dovetail-report` complains `ISEStudio.csproj` does not compile (a real regression), STOP and write `BLOCKED`. A clean repo is required.

- [ ] **Step 3: Verify the report contains the ABox pipeline page**

```bash
ls docs/superpowers/diagrams/extraction-abox-dag/index.html
ls docs/superpowers/diagrams/extraction-abox-dag/ISEStudio.Extraction.Dovetail.ABox.ABoxJobPipeline.html
ls docs/superpowers/diagrams/extraction-abox-dag/vendor/
```

Expected: all three HTML files exist; `vendor/` has at least `mermaid.min.js` + `pico.indigo.min.css`.

- [ ] **Step 4: Spot-check the rendered DAG content**

```bash
grep -c "mermaid" docs/superpowers/diagrams/extraction-abox-dag/ISEStudio.Extraction.Dovetail.ABox.ABoxJobPipeline.html
```

Expected: at least 1 occurrence.

Bonus spot-check: open the HTML and confirm the 6-node DAG (`gather → embed → judge → merge → cascade → final`) renders correctly.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/diagrams/extraction-abox-dag/
git commit -m "docs(extraction): add Dovetail ABox sub-DAG HTML report (Slice 2 visualization)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Self-Review

### 1. Spec coverage

| Spec § | Requirement | Task |
|---|---|---|
| §1 | Background | n/a (context) |
| §2 | Design goals (Dovetail split / visualize / degrade / auto-apply / write-transaction / non-conflict-auto-apply) | T1+T2+T3+T4+T5 |
| §3 | 5-stage DAG shape + type contracts | T1 (records) + T3 (pipeline partial) |
| §4 D1 | Thin-shell wrapping | T2 (all steps call existing services) |
| §4 D2 | Multi-input step interfaces (no bundle records) | T2 (each step declared as `IPipelineSegment<T1, ..., TOut>`) |
| §4 D3 | `DuplicateAutoApplyFloor = 0.90` (LOCKED) | T4 (new option field) |
| §4 D4 | Optional / FailSoft adapter reuse (null service = no-op) | T2 (each step has null-service branch) |
| §4 D5 | MergeApply capture per-merge (LOCKED) | T2 MergeApplyStep implementation note |
| §4 D6 | RunLayerAsync(ABox) + ConflictService forwarder | T5 |
| §4 D7 | No pipeline-as-segment dual impl | T3 (ABoxJobPipeline is top-level) |
| §5.1 | Error handling per stage | T2 (each step has try/catch + audit on failure) |
| §5.2 | 409 envelope (GuardedSegment) reused | not wired in slice 2 (slice 1 precedent) |
| §5.3 | Audit writes | T2 MergeApplyStep + CascadeRetypeStep |
| §6.1 | New files | T1, T2, T3, T4 |
| §6.2 | Existing file changes | T4 (ISEStudioOptions), T4 (DovetailPipelineRegistrations), T5 (ExtractionOrchestrator, ConflictService, ConflictServiceCollectionExtensions) |
| §6.3 | Type list | T1 |
| §7.2 | New tests | T1 (6) + T2 (9 step tests including FinalMerge) + T3 (2 pipeline tests) + T4 (5 registration) + T5 (2 orchestrator) = 24 tests, baseline 902 + 24 = 926 |
| §7.3 | Visualization | T6 |
| §7.4 | Gate (unit + integration + DOVE clean) | end-of-slice verification |

**Coverage verdict**: all spec requirements covered. **No gaps.**

### 2. Placeholder scan

- No "TBD" / "TODO" / "待定" in any step.
- T5 Step 4 has an illustrative forwarder sketch — the implementer must read the current `ConflictService.cs` to match exact field names. This is intentional and documented in the step.
- T4 Step 4 has a comment about `AuditLogService` namespace — the implementer must verify the existing namespace. Documented as a verification step.

### 3. Type consistency

| Type | Defined in | Used by |
|---|---|---|
| `CandidatePair` | T1 | T1, T2 (CandidateGatherStep, EmbeddingMatchStep, LLMJudgeStep, MergeApplyStep), T3 (test) |
| `CandidateList` | T1 | T1, T2 (all step signatures), T3 (test) |
| `JudgeResult` | T1 | T1, T2 (LLMJudgeStep, MergeApplyStep), T3 (test) |
| `MergedClassPair` | T1 | T1, T2 (MergeApplyStep, CascadeRetypeStep via MergeApplyOutput), T3 (test) |
| `AppliedMerges` | T1 | T1, T2 (MergeApplyStep, CascadeRetypeStep via MergeApplyOutput), T3 (test) |
| `RemainingConflicts` | T1 | T1, T2 (MergeApplyStep), T3 (test), T5 (forwarder) |
| `CascadeResult` | T1 | T1, T2 (CascadeRetypeStep), T3 (test) |
| `ABoxJobResult` | T1 | T1, T3 (pipeline output), T5 (forwarder return) |
| `MergeApplyOutput` | T1 | T2 (MergeApplyStep output, CascadeRetypeStep input, FinalMergeStep input) |
| `ABoxJobInput` | T1 | T2 (all step inputs), T3 (pipeline input), T5 (RunABoxLayerAsync constructs) |

**All types consistent.**

### 4. Task sizing

- T1: ~30 min implementer (records + tests)
- T2: ~90 min implementer (5 step classes + 5 test files)
- T3: ~20 min (1 step + 1 pipeline + 2 tests)
- T4: ~20 min (option field + DI extension + 5 tests)
- T5: ~60 min (orchestrator wire-up + ConflictService forwarder + 2 tests + ConflictServiceTests update)
- T6: ~10 min (tool install + report generation + commit)

Total: ~4 hours of focused implementer time across 6 tasks.

### 5. Risk callouts

- **T5 Step 4** (ConflictService forwarder): exact wiring depends on existing field signatures. Implementer MUST read current `ConflictService.cs` first; do not blindly copy the illustrative sketch. This is the highest-risk task.
- **T2 MergeApplyStep** depends on `OntologyEditor.ApplyClassMergeAsync` and `OntologyEditor.CascadeClassMergeAsync` existing with the expected signatures. Implementer MUST verify these signatures in `src/ISEStudio/Ontology/OntologyEditor.cs` before writing the step body.
- **T5 ctor parameter** is added at the END of `ExtractionOrchestrator` ctor (after `chunkPipeline` from slice 1) to preserve binary/source compat for existing callers. MUST NOT reorder existing parameters.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-29-abox-dovetail-pipeline-slice-2.md`. Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task via `superpowers:subagent-driven-development`, review between tasks, fast iteration. Slice 1 used this approach with 10 tasks; per-task review surfaced 1 BLOCKED + 1 implementation deviation + 1 brief-level error caught early.

2. **Inline Execution** — execute tasks in this session using `superpowers:executing-plans`, batch execution with checkpoints.

User already approved main-branch direct landing for slice 1; slice 2 continues the same landing strategy.
