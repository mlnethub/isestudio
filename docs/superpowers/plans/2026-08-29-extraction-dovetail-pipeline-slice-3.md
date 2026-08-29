# Slice 3: AgentChainPipeline Dovetail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**: 把 `ExtractionOrchestrator.RunAgentChainAsync`(P1-4 手写 4 步链)替换为 Dovetail DAG(`ConflictAgentStep` → `StructureAgentStep` → `StatsRefreshStep`),fallback 保留 P1-4 手写链供 hand-built 测试 orchestrator 使用。

**Architecture**: 3 段 DOVE006 多输入段 + 4 sealed records + 1 partial pipeline class + tail-seam ctor + thin-shell `RunAgentChainAsync`(DAG 路径优先,fallback 退化)。DetectAsync 留在 DAG 外。

**Tech Stack**: Dovetail 1.0.0 (NuGet + local E:\GitHub\Dovetail, source-gen) + .NET 10 + xUnit + Dovetail `AddPipelines()` 自动发现。

**Spec**: [docs/superpowers/specs/2026-08-29-extraction-dovetail-pipeline-slice-3-design.md](../specs/2026-08-29-extraction-dovetail-pipeline-slice-3-design.md)

**Predecessors**: Slice 1 (TBoxJobPipeline commit 57d1753) + Slice 2 (ABoxJobPipeline commit 250af1a) + P1-4 agent chain (commit 5594371)

## Global Constraints

- **Dovetail 1.0.0** (NuGet + local source)
- **DOVE006**: every segment input must be pipeline input or another step's output (no bundle records)
- **Concrete step type DI** (no `IPipelineSegment<...>` factories — slice 1 F-1 lesson)
- **Tail-seam ctor** in `ExtractionOrchestrator` (no parameter reordering)
- **`skipActiveExtractionGate: true`** for both agents (P1-4 LOCKED decision)
- **StatsRefreshStep fail-soft** (P1-4 LOCKED decision, DAG 不 propagate stats 失败)
- **`IChatClient.GetResponseAsync` 10.9.0** requires `IEnumerable<ChatMessage>` (NOT `IList<ChatMessage>`)
- **`DuplicateAutoApplyFloor = 0.90`** LOCKED (Slice 2)
- **C# 14 / .NET 10** / nullable enabled
- **RTK** for git operations (user preference)
- **Co-Authored-By: Claude <noreply@anthropic.com>** trailer on every commit
- **Main branch direct landing** (slice 1 precedent)

---

## File Structure

| Layer | File | Responsibility |
|------|------|----------------|
| Records | `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainInputs.cs` | 4 sealed records |
| Pipeline | `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainPipeline.cs` | partial class with 3 `[Segment]` ctor params |
| Step 1 | `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStep.cs` | 1 input → ConflictTriageResult |
| Step 2 | `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StructureAgentStep.cs` | 2 inputs → StructureAttachResult |
| Step 3 | `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStep.cs` | 3 inputs → AgentChainResult |
| DI | `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` (modify) | append 3 step registrations |
| Orchestrator | `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` (modify) | add `_agentChainPipeline` field + ctor tail param + `RunAgentChainAsync` body replace |
| Tests | 5 new test files + 1 modify (P1-4 6 tests分流) | record/step/pipeline/orchestrator coverage |

---

## Task Decomposition

5 tasks + 1 visualization task (total 6):

| Task | Deliverable | Commit pattern |
|------|-------------|----------------|
| 1 | 4 records + 4 tests | `feat(extraction): add AgentChain Dovetail job I/O records (4 records, 4 tests)` |
| 2 | 3 step classes + 7 tests | `feat(extraction): add AgentChain Dovetail 3 step classes (ConflictAgent/StructureAgent/StatsRefresh)` |
| 3 | `AgentChainPipeline` partial + 1 happy-path | `feat(extraction): add Dovetail AgentChainPipeline (3-stage DAG)` |
| 4 | DI registrations | `feat(extraction): wire AgentChainPipeline into DI` |
| 5 | Orchestrator wire + P1-4 测试分流 | `feat(extraction): wire AgentChainPipeline into RunAgentChainAsync (agent chain branch)` |
| 6 | dovetail-report HTML | `docs(extraction): add Dovetail AgentChain sub-DAG HTML report` |

---

### Task 1: AgentChain Dovetail job I/O records (4 records + 4 tests)

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainInputs.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainInputsTests.cs`

**Interfaces:**
- Consumes: spec §4 verbatim record definitions
- Produces: `AgentChainInput`, `ConflictTriageResult`, `StructureAttachResult`, `AgentChainResult` types

- [ ] **Step 1: Write the failing test**

Create `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainInputsTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Ontology;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

public class AgentChainInputsTests
{
    [Fact]
    public void ConflictTriageResult_EmptyConstruction_HasEmptyTriagedAndZeroAttached()
    {
        var result = new ConflictTriageResult(Array.Empty<ConflictDetection.DetectedConflict>(), 0);
        Assert.Empty(result.TriagedConflicts);
        Assert.Equal(0, result.RecommendationsAttached);
    }

    [Fact]
    public void StructureAttachResult_EmptyConstruction_HasZeroAttachedAndZeroCreated()
    {
        var result = new StructureAttachResult(0, 0);
        Assert.Equal(0, result.IsolatedAttached);
        Assert.Equal(0, result.NewClassesCreated);
    }

    [Fact]
    public void AgentChainInput_EmptyConstruction_HasEmptyConflictsAndNullModel()
    {
        var input = new AgentChainInput(
            JobId: Guid.Empty,
            KnowledgeSystemId: Guid.Empty,
            Conflicts: Array.Empty<ConflictDetection.DetectedConflict>(),
            Model: null);
        Assert.Equal(Guid.Empty, input.JobId);
        Assert.Empty(input.Conflicts);
        Assert.Null(input.Model);
    }

    [Fact]
    public void AgentChainResult_AllSubresultsRoundTrip()
    {
        var triage = new ConflictTriageResult(Array.Empty<ConflictDetection.DetectedConflict>(), 3);
        var structure = new StructureAttachResult(5, 2);
        var result = new AgentChainResult(triage, structure);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
        Assert.Equal(3, result.Triage.RecommendationsAttached);
        Assert.Equal(5, result.Structure.IsolatedAttached);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~AgentChainInputsTests" --nologo`
Expected: FAIL with `CS0234: 命名空间"ISEStudio.Extraction.Dovetail"中不存在类型或命名空间名"AgentChain"`

- [ ] **Step 3: Write minimal implementation**

Create `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainInputs.cs`:

```csharp
using ISEStudio.Ontology;

namespace ISEStudio.Extraction.Dovetail.AgentChain;

/// <summary>
/// Input to the agent chain Dovetail pipeline. Conflicts are detected
/// externally by <c>ConflictService.DetectAsync</c> (per Slice 3 spec §5 D1)
/// and passed in here; the pipeline runs ConflictAgent → StructureAgent →
/// StatsRefresh as three typed segments.
/// </summary>
public sealed record AgentChainInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts,
    string? Model);

/// <summary>
/// Output of <c>ConflictAgentStep</c>. Holds the triaged conflicts plus
/// the count of conflicts to which a recommendation was attached.
/// </summary>
public sealed record ConflictTriageResult(
    IReadOnlyList<ConflictDetection.DetectedConflict> TriagedConflicts,
    int RecommendationsAttached);

/// <summary>
/// Output of <c>StructureAgentStep</c>. Counts of isolated classes that
/// were attached to a parent + new parent classes created.
/// </summary>
public sealed record StructureAttachResult(
    int IsolatedAttached,
    int NewClassesCreated);

/// <summary>
/// Final output of <c>AgentChainPipeline</c>. Bundles the two intermediate
/// results for the orchestrator to log/expose.
/// </summary>
public sealed record AgentChainResult(
    ConflictTriageResult Triage,
    StructureAttachResult Structure);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~AgentChainInputsTests" --nologo`
Expected: `Passed: 4, Failed: 0`

- [ ] **Step 5: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 931, Failed: 0, Skipped: 1, Total: 932` (927 + 4 = 931)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainInputs.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainInputsTests.cs
git commit -m "feat(extraction): add AgentChain Dovetail job I/O records (4 records, 4 tests)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 3 AgentChain step classes + 7 tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StructureAgentStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStep.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/StructureAgentStepTests.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStepTests.cs`

**Interfaces:**
- Consumes: `AgentChainInput`, `ConflictTriageResult`, `StructureAttachResult` (Task 1)
- Produces: 3 step classes implementing `IPipelineSegment<...>`

**CRITICAL — verify these signatures against current code before writing:**

1. **`ConflictAgent.TriageAsync(IReadOnlyList<DetectedConflict>, Guid, string?, bool, CancellationToken)` returns `Task<int>`** — verify at `src/ISEStudio/Conflicts/ConflictAgent.cs` (post-rename from P1-1 commit 1b4a95b). Note: `RecommendationsAttached` count comes from this Task<int> return value.
2. **`StructureAgent.AttachIsolatedAsync(Guid, int, bool, CancellationToken)` returns `Task<(int IsolatedAttached, int NewClassesCreated)>`** — verify at `src/ISEStudio/Ontology/StructureAgent.cs` (post-rename from P1-3 commit 38a6320). Note: tuple return must be unpacked.
3. **`KnowledgeStatsService.RefreshAsync(Guid, CancellationToken)` returns `Task`** — verify at `src/ISEStudio/Knowledge/KnowledgeStatsService.cs`.

If signatures differ, **adapt step code to match actual signatures** — do NOT silently leave broken wiring. Document each adaptation in the task report.

- [ ] **Step 1: Write 7 failing tests**

Create `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStepTests.cs`:

```csharp
using ISEStudio.Conflicts;
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain.Steps;

public class ConflictAgentStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullAgent_ReturnsEmptyTriage()
    {
        var step = new ConflictAgentStep(null, NullLogger<ConflictAgentStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<DetectedConflict>(), null);
        var result = await step.ExecuteAsync(input, CancellationToken.None);
        Assert.Empty(result.TriagedConflicts);
        Assert.Equal(0, result.RecommendationsAttached);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsRecommendationsAttached()
    {
        var fakeAgent = new FakeConflictAgent(recommendationsAttached: 3);
        var step = new ConflictAgentStep(fakeAgent, NullLogger<ConflictAgentStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<DetectedConflict>(), "test-model");
        var result = await step.ExecuteAsync(input, CancellationToken.None);
        Assert.Equal(3, result.RecommendationsAttached);
        Assert.Same(input.Conflicts, result.TriagedConflicts);
    }
}

internal sealed class FakeConflictAgent : IConflictAgent
{
    private readonly int _recommendationsAttached;

    public FakeConflictAgent(int recommendationsAttached) => _recommendationsAttached = recommendationsAttached;

    public Task<int> TriageAsync(
        IReadOnlyList<ConflictDetection.DetectedConflict> conflicts,
        Guid knowledgeSystemId,
        string? model,
        bool skipActiveExtractionGate,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(_recommendationsAttached);
    }
}
```

NOTE: The implementer must check whether `IConflictAgent` interface exists in the codebase or whether `ConflictAgent` is a concrete class. If concrete class, the step ctor takes `ConflictAgent?` (nullable) directly. Adapt to actual type structure.

Create `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/StructureAgentStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain.Steps;

public class StructureAgentStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullAgent_ReturnsZeroAttached()
    {
        var step = new StructureAgentStep(null, NullLogger<StructureAgentStep>.Instance, maxSameParent: 5);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<DetectedConflict>(), null);
        var triage = new ConflictTriageResult(Array.Empty<DetectedConflict>(), 0);
        var result = await step.ExecuteAsync(input, triage, CancellationToken.None);
        Assert.Equal(0, result.IsolatedAttached);
        Assert.Equal(0, result.NewClassesCreated);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsIsolatedCount()
    {
        var fakeAgent = new FakeStructureAgent(isolatedAttached: 4, newClassesCreated: 1);
        var step = new StructureAgentStep(fakeAgent, NullLogger<StructureAgentStep>.Instance, maxSameParent: 5);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<DetectedConflict>(), null);
        var triage = new ConflictTriageResult(Array.Empty<DetectedConflict>(), 2);
        var result = await step.ExecuteAsync(input, triage, CancellationToken.None);
        Assert.Equal(4, result.IsolatedAttached);
        Assert.Equal(1, result.NewClassesCreated);
    }
}

internal sealed class FakeStructureAgent : IStructureAgent
{
    private readonly int _isolated;
    private readonly int _newClasses;

    public FakeStructureAgent(int isolatedAttached, int newClassesCreated)
    {
        _isolated = isolatedAttached;
        _newClasses = newClassesCreated;
    }

    public Task<(int IsolatedAttached, int NewClassesCreated)> AttachIsolatedAsync(
        Guid knowledgeSystemId,
        int maxSameParent,
        bool skipActiveExtractionGate,
        CancellationToken cancellationToken)
    {
        return Task.FromResult((_isolated, _newClasses));
    }
}
```

Create `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStepTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Knowledge;
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain.Steps;

public class StatsRefreshStepTests
{
    [Fact]
    public async Task ExecuteAsync_NullStats_ReturnsAgentChainResult()
    {
        var step = new StatsRefreshStep(null, NullLogger<StatsRefreshStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<DetectedConflict>(), null);
        var triage = new ConflictTriageResult(Array.Empty<DetectedConflict>(), 0);
        var structure = new StructureAttachResult(0, 0);
        var result = await step.ExecuteAsync(input, triage, structure, CancellationToken.None);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_RefreshesAndBundles()
    {
        var fakeStats = new FakeKnowledgeStatsService();
        var step = new StatsRefreshStep(fakeStats, NullLogger<StatsRefreshStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<DetectedConflict>(), null);
        var triage = new ConflictTriageResult(Array.Empty<DetectedConflict>(), 1);
        var structure = new StructureAttachResult(2, 1);
        var result = await step.ExecuteAsync(input, triage, structure, CancellationToken.None);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
        Assert.Equal(1, fakeStats.RefreshCallCount);
        Assert.Equal(input.KnowledgeSystemId, fakeStats.LastKnowledgeSystemId);
    }

    [Fact]
    public async Task ExecuteAsync_StatsThrows_FailsSoft_StillReturnsResult()
    {
        var fakeStats = new FakeKnowledgeStatsService { ThrowOnRefresh = true };
        var step = new StatsRefreshStep(fakeStats, NullLogger<StatsRefreshStep>.Instance);
        var input = new AgentChainInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<DetectedConflict>(), null);
        var triage = new ConflictTriageResult(Array.Empty<DetectedConflict>(), 1);
        var structure = new StructureAttachResult(2, 1);
        var result = await step.ExecuteAsync(input, triage, structure, CancellationToken.None);
        Assert.Same(triage, result.Triage);
        Assert.Same(structure, result.Structure);
    }
}

internal sealed class FakeKnowledgeStatsService : IKnowledgeStatsService
{
    public int RefreshCallCount { get; private set; }
    public Guid LastKnowledgeSystemId { get; private set; }
    public bool ThrowOnRefresh { get; init; }

    public Task RefreshAsync(Guid knowledgeSystemId, CancellationToken cancellationToken)
    {
        RefreshCallCount++;
        LastKnowledgeSystemId = knowledgeSystemId;
        if (ThrowOnRefresh) throw new InvalidOperationException("test-induced stats failure");
        return Task.CompletedTask;
    }
}
```

NOTE: Implementer must check whether `IKnowledgeStatsService` interface exists; if not, step ctor takes `KnowledgeStatsService?` directly.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~AgentChain.Steps" --nologo`
Expected: FAIL with `CS0246: 未能找到类型或命名空间名"ConflictAgentStep"` (and similar for StructureAgentStep, StatsRefreshStep)

- [ ] **Step 3: Write ConflictAgentStep**

Create `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStep.cs`:

```csharp
using ISEStudio.Conflicts;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.AgentChain.Steps;

/// <summary>
/// Dovetail pipeline segment: runs <see cref="ConflictAgent.TriageAsync"/>
/// over the conflicts produced upstream by
/// <c>ConflictService.DetectAsync</c> (which is invoked outside the DAG per
/// Slice 3 spec §5 D1). Fail-soft on null agent (returns empty result so the
/// downstream segments can still complete in tests that omit the agent).
/// </summary>
public sealed class ConflictAgentStep : IPipelineSegment<AgentChainInput, ConflictTriageResult>
{
    private readonly ConflictAgent? _agent;
    private readonly ILogger<ConflictAgentStep> _logger;

    public ConflictAgentStep(ConflictAgent? agent, ILogger<ConflictAgentStep> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public async Task<ConflictTriageResult> ExecuteAsync(AgentChainInput input, CancellationToken cancellationToken)
    {
        if (_agent is null)
        {
            _logger.LogWarning("ConflictAgentStep: agent is null, returning empty triage");
            return new ConflictTriageResult(input.Conflicts, 0);
        }

        var attached = await _agent.TriageAsync(
            input.Conflicts,
            input.KnowledgeSystemId,
            input.Model,
            skipActiveExtractionGate: true,
            cancellationToken).ConfigureAwait(false);

        return new ConflictTriageResult(input.Conflicts, attached);
    }
}
```

NOTE: If `ConflictAgent` is actually a concrete class, the nullable ctor parameter type is `ConflictAgent?`. If it's behind an interface (e.g., `IConflictAgent`), change to `IConflictAgent?`. Verify against current code.

- [ ] **Step 4: Write StructureAgentStep**

Create `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StructureAgentStep.cs`:

```csharp
using ISEStudio.Ontology;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.AgentChain.Steps;

/// <summary>
/// Dovetail pipeline segment: runs
/// <see cref="StructureAgent.AttachIsolatedAsync"/> to attach isolated
/// classes to broader parents. Fail-soft on null agent. <c>maxSameParent</c>
/// is read from <c>ISEStudioOptions.StructureMaxSameParent</c> at DI registration
/// time (the orchestrator passes it via constructor).
/// </summary>
public sealed class StructureAgentStep : IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult>
{
    private readonly StructureAgent? _agent;
    private readonly ILogger<StructureAgentStep> _logger;
    private readonly int _maxSameParent;

    public StructureAgentStep(StructureAgent? agent, ILogger<StructureAgentStep> logger, int maxSameParent)
    {
        _agent = agent;
        _logger = logger;
        _maxSameParent = maxSameParent;
    }

    public async Task<StructureAttachResult> ExecuteAsync(
        AgentChainInput input,
        ConflictTriageResult triage,
        CancellationToken cancellationToken)
    {
        if (_agent is null)
        {
            _logger.LogWarning("StructureAgentStep: agent is null, returning zero attached");
            return new StructureAttachResult(0, 0);
        }

        var (isolated, newClasses) = await _agent.AttachIsolatedAsync(
            input.KnowledgeSystemId,
            _maxSameParent,
            skipActiveExtractionGate: true,
            cancellationToken).ConfigureAwait(false);

        return new StructureAttachResult(isolated, newClasses);
    }
}
```

NOTE: Verify StructureAgent ctor / DI lifetime (scoped per P1-3). If scoped, the Dovetail pipeline will need to construct a scope per execution — this is handled by Dovetail 1.0.0's pipeline resolution (verify with the source-gen emit in Task 3). If StructureAgent is registered as scoped and the pipeline is singleton, this is a known limitation — document in report and consider Task 4 DI fix.

- [ ] **Step 5: Write StatsRefreshStep**

Create `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStep.cs`:

```csharp
using ISEStudio.Knowledge;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail.AgentChain.Steps;

/// <summary>
/// Dovetail pipeline segment: best-effort
/// <see cref="KnowledgeStatsService.RefreshAsync"/>. Fail-soft: stats refresh
/// exceptions are swallowed and logged, never propagated (Slice 3 spec §5 D4,
/// matching P1-4 LOCKED decision).
/// </summary>
public sealed class StatsRefreshStep : IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult, AgentChainResult>
{
    private readonly KnowledgeStatsService? _stats;
    private readonly ILogger<StatsRefreshStep> _logger;

    public StatsRefreshStep(KnowledgeStatsService? stats, ILogger<StatsRefreshStep> logger)
    {
        _stats = stats;
        _logger = logger;
    }

    public async Task<AgentChainResult> ExecuteAsync(
        AgentChainInput input,
        ConflictTriageResult triage,
        StructureAttachResult structure,
        CancellationToken cancellationToken)
    {
        if (_stats is not null)
        {
            try
            {
                await _stats.RefreshAsync(input.KnowledgeSystemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StatsRefreshStep: stats refresh failed (fail-soft, continuing)");
            }
        }

        return new AgentChainResult(triage, structure);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~AgentChain.Steps" --nologo`
Expected: `Passed: 7, Failed: 0`

- [ ] **Step 7: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 938, Failed: 0, Skipped: 1, Total: 939` (931 + 7 = 938)

- [ ] **Step 8: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/ \
        src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/
git commit -m "feat(extraction): add AgentChain Dovetail 3 step classes (ConflictAgent/StructureAgent/StatsRefresh)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: AgentChainPipeline partial class + 1 happy-path test

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainPipeline.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainPipelineTests.cs`

**Interfaces:**
- Consumes: 3 step classes (Task 2), 4 record types (Task 1)
- Produces: `AgentChainPipeline` partial class with 3 `[Segment]` ctor params; source-gen emits `AgentChainPipeline.g.cs` with `ExecuteAsync` + Mermaid `flowchart TD`

- [ ] **Step 1: Write the failing test**

Create `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainPipelineTests.cs`:

```csharp
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

public class AgentChainPipelineTests
{
    [Fact]
    public void AgentChainPipeline_DovetailEmitsExecuteAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<AgentChainPipeline>();
        Assert.NotNull(pipeline);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~AgentChainPipelineTests" --nologo`
Expected: FAIL with `CS0246: 未能找到类型或命名空间名"AgentChainPipeline"`

- [ ] **Step 3: Write the pipeline partial class**

Create `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainPipeline.cs`:

```csharp
using Dovetail;

namespace ISEStudio.Extraction.Dovetail.AgentChain;

/// <summary>
/// Dovetail pipeline that runs the extraction agent chain as three typed
/// segments: ConflictAgent → StructureAgent → StatsRefresh. Constructed via
/// <see cref="Dovetail.DovetailPipelineBuilderExtensions.AddPipelines"/>;
/// the source generator emits <c>AgentChainPipeline.g.cs</c> with the
/// <see cref="ExecuteAsync"/> method and Mermaid diagram.
/// </summary>
public partial class AgentChainPipeline
{
    public AgentChainPipeline(
        [Segment] ConflictAgentStep conflictAgentStep,
        [Segment] StructureAgentStep structureAgentStep,
        [Segment] StatsRefreshStep statsRefreshStep)
    {
        ConflictAgentStep = conflictAgentStep;
        StructureAgentStep = structureAgentStep;
        StatsRefreshStep = statsRefreshStep;
    }

    public ConflictAgentStep ConflictAgentStep { get; }
    public StructureAgentStep StructureAgentStep { get; }
    public StatsRefreshStep StatsRefreshStep { get; }
}
```

- [ ] **Step 4: Verify source-gen emits ExecuteAsync**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --nologo`
Expected: 0 errors. Inspect the emitted `AgentChainPipeline.g.cs` (typically under `obj/Debug/net10.0/generated/Dovetail/`) for:
- A `Task<AgentChainResult> ExecuteAsync(AgentChainInput, CancellationToken)` method
- A `Mermaid` property returning the DAG as `flowchart TD` string
- 3 segment wrappers invoking each step in dependency order

If `EmitCompilerGeneratedFiles=true` is not already set in the project, add to a `Directory.Build.props` temporarily, build, inspect, then revert. (Slice 2 Task 3 precedent.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~AgentChainPipelineTests" --nologo`
Expected: `Passed: 1, Failed: 0`

- [ ] **Step 6: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 939, Failed: 0, Skipped: 1, Total: 940`

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainPipeline.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainPipelineTests.cs
git commit -m "feat(extraction): add Dovetail AgentChainPipeline (3-stage DAG)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: DI registration for 3 steps + pipeline integration tests

**Files:**
- Modify: `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` (append 3 step registrations)
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/DovetailPipelineRegistrationsAgentChainTests.cs` (or extend existing)

**Interfaces:**
- Consumes: 3 step classes (Task 2), 1 pipeline class (Task 3)
- Produces: 3 step DI registrations using concrete type factory pattern

- [ ] **Step 1: Read current DovetailPipelineRegistrations**

Read `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` to confirm:
- Where ABox step registrations live (around lines 70-84 per Slice 2 spec)
- The factory pattern used: `services.AddSingleton<XStep>(sp => new XStep(sp.GetService<...>(), ...))`

- [ ] **Step 2: Write the failing tests**

Create `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/DovetailPipelineRegistrationsAgentChainTests.cs`:

```csharp
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction.Dovetail.AgentChain;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;
using ISEStudio.Knowledge;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.AgentChain;

public class DovetailPipelineRegistrationsAgentChainTests
{
    [Fact]
    public void ConflictAgentStep_IsResolvable_WhenAgentsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<ConflictAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var step = sp.GetService<ConflictAgentStep>();
        Assert.NotNull(step);
    }

    [Fact]
    public void StructureAgentStep_IsResolvable_WhenAgentsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions { StructureMaxSameParent = 5 }));
        services.AddSingleton<StructureAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var step = sp.GetService<StructureAgentStep>();
        Assert.NotNull(step);
    }

    [Fact]
    public void StatsRefreshStep_IsResolvable_WhenStatsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<KnowledgeStatsService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var step = sp.GetService<StatsRefreshStep>();
        Assert.NotNull(step);
    }

    [Fact]
    public void AllAgentChainSteps_ResolveNull_WhenAgentsNotRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.Null(sp.GetService<ConflictAgentStep>());
        Assert.Null(sp.GetService<StructureAgentStep>());
        Assert.Null(sp.GetService<StatsRefreshStep>());
    }

    [Fact]
    public void AgentChainPipeline_IsResolvable_WhenStepsResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<ConflictAgent>();
        services.AddSingleton<StructureAgent>();
        services.AddSingleton<KnowledgeStatsService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<AgentChainPipeline>();
        Assert.NotNull(pipeline);
    }
}
```

NOTE: `ISEStudioOptions.StructureMaxSameParent` exists per P1-3 memory. `ConflictAgent` / `StructureAgent` / `KnowledgeStatsService` are concrete classes registered as scoped/singleton — verify with current code.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~DovetailPipelineRegistrationsAgentChainTests" --nologo`
Expected: FAIL — at least one step DI registration missing.

- [ ] **Step 4: Append 3 step registrations to DovetailPipelineRegistrations**

In `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`, **append** (do NOT replace existing ABox registrations):

```csharp
// AgentChain slice 3 (per spec §6.2 + §5 D6)
services.AddSingleton<ConflictAgentStep>(sp => new ConflictAgentStep(
    agent: sp.GetService<ConflictAgent>(),
    logger: sp.GetRequiredService<ILogger<ConflictAgentStep>>()));

services.AddSingleton<StructureAgentStep>(sp => new StructureAgentStep(
    agent: sp.GetService<StructureAgent>(),
    logger: sp.GetRequiredService<ILogger<StructureAgentStep>>(),
    maxSameParent: sp.GetRequiredService<IOptions<ISEStudioOptions>>().Value.StructureMaxSameParent));

services.AddSingleton<StatsRefreshStep>(sp => new StatsRefreshStep(
    stats: sp.GetService<KnowledgeStatsService>(),
    logger: sp.GetRequiredService<ILogger<StatsRefreshStep>>()));
```

NOTE: Import statements at top of file must include:
- `using ISEStudio.Conflicts;` (for `ConflictAgent`)
- `using ISEStudio.Extraction.Dovetail.AgentChain.Steps;` (for the 3 step classes)
- `using ISEStudio.Knowledge;` (for `KnowledgeStatsService`)
- `using ISEStudio.Ontology;` (for `StructureAgent`)
- `using ISEStudio.Configuration;` (for `ISEStudioOptions`)
- `using Microsoft.Extensions.Options;` (for `IOptions<ISEStudioOptions>`)

If `using Microsoft.Extensions.Logging;` not already present, add it.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~DovetailPipelineRegistrationsAgentChainTests" --nologo`
Expected: `Passed: 5, Failed: 0`

- [ ] **Step 6: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 944, Failed: 0, Skipped: 1, Total: 945` (939 + 5 = 944)

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/DovetailPipelineRegistrationsAgentChainTests.cs
git commit -m "feat(extraction): wire AgentChainPipeline into DI

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: ExtractionOrchestrator wire-up + P1-4 test分流

**Files:**
- Modify: `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` (add `_agentChainPipeline` field + ctor tail param + replace `RunAgentChainAsync` body)
- Modify: `src/ISEStudio.Tests/Extraction/ExtractionAgentChainTests.cs` (分流 P1-4 6 tests)
- Create: `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorAgentChainPipelineTests.cs`

**Interfaces:**
- Consumes: `AgentChainPipeline` (Task 3), 4 records (Task 1)
- Produces: orchestrator field + ctor tail-seam param + new `RunAgentChainAsync` body + 2 new orchestrator DI tests

**CRITICAL — read these files in full first:**

1. **`src/ISEStudio/Extraction/ExtractionOrchestrator.cs`** (read the whole file)
   - Find the ctor — confirm parameter ordering, existing `_chunkPipeline` / `_aboxPipeline` fields, existing `_conflictService` / `_conflictAgent` / `_structureAgent` / `_stats` fields.
   - Find `RunAgentChainAsync` — see exact existing body (P1-4 implementation).
   - Find `RunLayerAsync(TBox)` / `RunABoxLayerAsync` — see how Slice 1/2 nullable-seam ternary is written.

2. **`src/ISEStudio/Conflicts/ConflictService.cs`** (read the whole file)
   - Find `DetectAsync` (Slice 2 made it a forwarder to `RunABoxLayerAsync`).
   - **VERIFY**: `RunAgentChainAsync` calls `_conflictService.DetectAsync` directly (P1-4 precedent), NOT through `RunABoxLayerAsync` — confirm.

3. **`src/ISEStudio.Tests/Extraction/ExtractionAgentChainTests.cs`** (read the test file)
   - Find the 6 tests that call `RunAgentChainAsync` — note which assertions need分流.
   - Existing tests may assert specific call order (Detect → Triage → Attach → Refresh).

- [ ] **Step 1: Add `_agentChainPipeline` field + ctor tail param to ExtractionOrchestrator**

In `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`:

(a) Add new field after `_aboxPipeline`:

```csharp
/// <summary>
/// Dovetail-generated agent chain pipeline (ConflictAgent → StructureAgent
/// → StatsRefresh). Preferred over manual chain when registered in DI; the
/// manual chain is the fallback for hand-built test orchestrators.
/// </summary>
private readonly AgentChainPipeline? _agentChainPipeline;
```

(b) Add ctor tail parameter (after `duplicateJudge` from Slice 2):

```csharp
AgentChainPipeline? agentChainPipeline = null
```

Assign in ctor body:

```csharp
_agentChainPipeline = agentChainPipeline;
```

(c) Verify imports at top include:

```csharp
using ISEStudio.Extraction.Dovetail.AgentChain;
```

If not present, add it.

- [ ] **Step 2: Replace `RunAgentChainAsync` body**

Find the existing `RunAgentChainAsync` method. Replace its body with:

```csharp
public async Task RunAgentChainAsync(CancellationToken cancellationToken)
{
    if (_scopes is null)
    {
        // P1-4 seam: hand-built test orchestrators skip the chain entirely.
        return;
    }

    using var scope = _scopes.CreateScope();
    var conflictService = scope.ServiceProvider.GetRequiredService<ConflictService>();
    var conflicts = await conflictService.DetectAsync(_currentKsId, cancellationToken).ConfigureAwait(false);

    var input = new AgentChainInput(
        JobId: _jobId,
        KnowledgeSystemId: _currentKsId,
        Conflicts: conflicts,
        Model: null);

    if (_agentChainPipeline is not null)
    {
        await _agentChainPipeline.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        return;
    }

    // Fallback: P1-4 hand-written chain (hand-built test orchestrators or DI failure).
    var conflictAgent = scope.ServiceProvider.GetService<ConflictAgent>();
    var structureAgent = scope.ServiceProvider.GetService<StructureAgent>();
    var stats = scope.ServiceProvider.GetService<KnowledgeStatsService>();
    var options = _optionsAccessor.Value;

    if (conflictAgent is not null)
    {
        await conflictAgent.TriageAsync(conflicts, _currentKsId, null, skipActiveExtractionGate: true, cancellationToken).ConfigureAwait(false);
    }

    if (structureAgent is not null)
    {
        await structureAgent.AttachIsolatedAsync(_currentKsId, options.StructureMaxSameParent, skipActiveExtractionGate: true, cancellationToken).ConfigureAwait(false);
    }

    if (stats is not null)
    {
        try
        {
            await stats.RefreshAsync(_currentKsId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // P1-4 fail-soft: stats refresh failure must not fail the job.
        }
    }
}
```

NOTE: `_jobId`, `_currentKsId`, `_scopes`, `_optionsAccessor` field names must match the existing ExtractionOrchestrator field names. Verify with current code. If fields are named differently (e.g., `_currentJobId`), adapt.

NOTE: If `_optionsAccessor` is not unwrapped `ISEStudioOptions` (i.e., it's `IOptions<ISEStudioOptions>`), keep as `IOptions<ISEStudioOptions>` and call `.Value.StructureMaxSameParent`.

- [ ] **Step 3: Write the failing tests**

Create `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorAgentChainPipelineTests.cs`:

```csharp
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.AgentChain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction;

public class ExtractionOrchestratorAgentChainPipelineTests
{
    [Fact]
    public void AgentChainPipeline_IsResolvable_FromOrchestratorServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<ISEStudio.Conflicts.ConflictAgent>();
        services.AddSingleton<ISEStudio.Ontology.StructureAgent>();
        services.AddSingleton<ISEStudio.Knowledge.KnowledgeStatsService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<AgentChainPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AgentChainPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        // Intentionally NOT calling AddDovetailPipelines().
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<AgentChainPipeline>();
        Assert.Null(pipeline);
    }
}
```

- [ ] **Step 4: Run new tests to verify they fail (or pass for the negative case)**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~ExtractionOrchestratorAgentChainPipelineTests" --nologo`
Expected: PASS (positive test verifies DI wiring from Task 4; negative test verifies omission).

If both pass without Task 4 changes (because Dovetail auto-registers), Task 4 may already cover this. Adapt tests if needed.

- [ ] **Step 5:分流 P1-4 6 tests in `ExtractionAgentChainTests.cs`**

Read the existing 6 tests in `src/ISEStudio.Tests/Extraction/ExtractionAgentChainTests.cs` carefully. For each test:

- If the test uses DI scope path (calls `RunAgentChainAsync` via DI-injected orchestrator): assertions should verify the DAG path is invoked. Either:
  - Replace `_conflictAgent.TriageAsync` direct assertion with `_agentChainPipeline.ExecuteAsync` indirect assertion (e.g., verify through side effects / mocks).
  - Or: assert that the existing tests still pass because the DAG internally calls `ConflictAgent.TriageAsync` (which the test mocks).

- If the test uses hand-built path (constructs `ExtractionOrchestrator` manually with null scope seam): assertions should verify the fallback path.

**Pre-existing tests should NOT be deleted — only modified to track the new dispatch point.**

If pre-existing tests are too tightly coupled to specific call sequences (e.g., exact log messages, exact mock invocation order), allow rewrites that preserve the **intent** of each test:

- `RunAgentChainAsync_NormalFlow_RunsAllThreeAgents` (or similar) — verify all 3 agents are invoked regardless of DAG vs fallback path
- `RunAgentChainAsync_NullScope_SkipsChain` — verify null scope seam behavior
- `RunAgentChainAsync_StatsThrows_StillCompletes` — verify stats fail-soft
- etc.

Document each test modification in the task report.

- [ ] **Step 6: Run full suite to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: `Passed: 946, Failed: 0, Skipped: 1, Total: 947` (944 + 2 new orchestrator tests = 946)

If the 6 P1-4 tests break and need分流, the count may shift. Aim for `940+ / 0 / 1 / 940+` (no regression in total count, but allow ±2 for test分流).

- [ ] **Step 7: Run integration tests to verify no regression**

Run: `dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --nologo`
Expected: same as baseline (Docker unavailable, 4/42 pre-existing pattern).

- [ ] **Step 8: Commit**

```bash
git add src/ISEStudio/Extraction/ExtractionOrchestrator.cs \
        src/ISEStudio.Tests/Extraction/ExtractionAgentChainTests.cs \
        src/ISEStudio.Tests/Extraction/ExtractionOrchestratorAgentChainPipelineTests.cs
git commit -m "feat(extraction): wire AgentChainPipeline into RunAgentChainAsync (agent chain branch)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: dovetail-report HTML for AgentChain pipeline

**Files:**
- Create: `docs/superpowers/diagrams/extraction-agentchain-dag/index.html`
- Create: `docs/superpowers/diagrams/extraction-agentchain-dag/ISEStudio.Extraction.Dovetail.AgentChain.AgentChainPipeline.html`
- Create: `docs/superpowers/diagrams/extraction-agentchain-dag/vendor/mermaid.min.js`
- Create: `docs/superpowers/diagrams/extraction-agentchain-dag/vendor/pico.indigo.min.css`

**Interfaces:**
- Consumes: `AgentChainPipeline` partial class (Task 3) discoverable by `dovetail-report`
- Produces: HTML DAG visualization report

- [ ] **Step 1: Install dovetail-report 1.0.0**

```bash
dotnet tool install --global Dovetail.Report --version 1.0.0
```

Expected: installation succeeds. If nuget.org unreachable, fallback to local pack:

```bash
dotnet pack E:\GitHub\Dovetail\Dovetail.Report\Dovetail.Report.csproj -c Release -o ./local-nuget
dotnet tool install --global Dovetail.Report --version 1.0.0 --add-source ./local-nuget
```

If both fail: write `DONE_WITH_CONCERNS` in the report noting that the tool could not be installed, and STOP.

- [ ] **Step 2: Generate the AgentChain sub-DAG report**

```bash
mkdir -p docs/superpowers/diagrams
dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams/extraction-agentchain-dag
```

Expected: command exits 0; `docs/superpowers/diagrams/extraction-agentchain-dag/` now has at least `index.html` + `ISEStudio.Extraction.Dovetail.AgentChain.AgentChainPipeline.html` + `vendor/`.

If `dovetail-report` complains `ISEStudio.csproj` does not compile, STOP and write `BLOCKED`.

- [ ] **Step 3: Verify the report contains the AgentChain pipeline page**

```bash
ls docs/superpowers/diagrams/extraction-agentchain-dag/index.html
ls docs/superpowers/diagrams/extraction-agentchain-dag/ISEStudio.Extraction.Dovetail.AgentChain.AgentChainPipeline.html
ls docs/superpowers/diagrams/extraction-agentchain-dag/vendor/
```

Expected: all HTML files exist; `vendor/` has at least `mermaid.min.js` + `pico.indigo.min.css`.

- [ ] **Step 4: Spot-check the rendered DAG content**

```bash
grep -c "mermaid" docs/superpowers/diagrams/extraction-agentchain-dag/ISEStudio.Extraction.Dovetail.AgentChain.AgentChainPipeline.html
```

Expected: at least 1 occurrence. The 3-stage DAG (`conflictAgent → structureAgent → statsRefresh`) should render.

- [ ] **Step 5: Verify ISEStudio.csproj still compiles clean after generation**

```bash
dotnet build src/ISEStudio/ISEStudio.csproj --nologo
```

Expected: 0 errors / 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/diagrams/extraction-agentchain-dag/
git commit -m "docs(extraction): add Dovetail AgentChain sub-DAG HTML report (Slice 3 visualization)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Self-Review

### Spec coverage

| Spec section | Covered by |
|--------------|------------|
| §1 Background | (plan preamble) |
| §2 Design goals | All tasks |
| §3 DAG shape | Task 2 (steps) + Task 3 (pipeline) |
| §4 Records | Task 1 |
| §5 D1 DetectAsync outside DAG | Task 5 step 2 |
| §5 D2 DAG path or fallback | Task 5 step 2 |
| §5 D3 skipActiveExtractionGate | Task 2 steps 3 + 4 |
| §5 D4 StatsRefresh fail-soft | Task 2 step 5 |
| §5 D5 IServiceScopeFactory seam | Task 5 step 2 (preserved) |
| §5 D6 concrete step type DI | Task 4 step 4 |
| §5 D7 no new options | (no task needed) |
| §6.1 new files (8) | Tasks 1, 2, 3 |
| §6.2 modified files (3) | Tasks 4, 5 |
| §7.1 new tests (~14) | Tasks 1, 2, 3, 4, 5 |
| §7.2 existing P1-4 tests | Task 5 step 5 |
| §7.3 Gate | Each task ends with full-suite run |
| §8 risk & preflight | (covered by adapt notes in steps) |
| §9 task decomposition | This plan |
| §10 LOCKED defaults | (no new LOCKED) |
| §11 ADR gap | (no impact) |
| §12 slice 1/2 comparison | (table in spec) |
| §13 acceptance | All tasks + final review |

**Gaps**: None identified.

### Placeholder scan

- No "TBD", "TODO", "implement later", "fill in details" in any step.
- No "add appropriate error handling" without specific code.
- No "similar to Task N" — each step is self-contained.
- Every code block is complete.
- No references to undefined types — all types defined in earlier tasks or in the codebase.

### Type consistency

- `AgentChainInput(JobId, KnowledgeSystemId, Conflicts, Model)` defined Task 1, used Task 2 + Task 5.
- `ConflictTriageResult(TriagedConflicts, RecommendationsAttached)` defined Task 1, used Task 2 + Task 5.
- `StructureAttachResult(IsolatedAttached, NewClassesCreated)` defined Task 1, used Task 2 + Task 5.
- `AgentChainResult(Triage, Structure)` defined Task 1, used Task 2 + Task 5.
- `ConflictAgentStep` ctor: `(ConflictAgent?, ILogger<ConflictAgentStep>)` defined Task 2, used Task 4.
- `StructureAgentStep` ctor: `(StructureAgent?, ILogger<StructureAgentStep>, int maxSameParent)` defined Task 2, used Task 4.
- `StatsRefreshStep` ctor: `(KnowledgeStatsService?, ILogger<StatsRefreshStep>)` defined Task 2, used Task 4.
- `AgentChainPipeline` ctor: 3 `[Segment]` params defined Task 3, used Task 5.

No type mismatches.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-29-extraction-dovetail-pipeline-slice-3.md`.

**Execution: Subagent-Driven (per slice 1/2 precedent).** Proceeding with superpowers:subagent-driven-development.
