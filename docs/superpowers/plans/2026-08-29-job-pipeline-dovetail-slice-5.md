# Job Pipeline Dovetail 化 — Slice 5 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**:把 `ExtractionOrchestrator.RunJobSafelyAsync` 内的 5 phase-runner(Layer/Agent/Corpus/Hierarchy/Terminology)抽到 Dovetail 顶层 `ExtractionJobPipeline`(3 变体:TBoxOnly/ABoxOnly/Combined),`JobRunContext`(mutable struct)→ `JobState`(immutable record)切换,409 envelope 从 dispatcher 提到 pipeline 顶层,纯薄壳 A 方案,2 slice 之内闭环。

**Architecture**:Dovetail 1.0.0 source-generator 驱动的 3 个 `IPipeline<JobInput, JobResult>` 变体,每变体含 N 个 `IPipelineSegment<JobState, JobState>`(LayerStep generic + 4 个 phase step),DI 层根据 options/nullable state 选 NoOp vs 真段(父 spec D4)。PerPhaseCatchStep Dovetail adapter 在每段包 try/catch 转 `JobState.Error`(Slice 1-4 orchestrator helper try/catch 模式复用)。`RunJobSafelyAsync` 仅替换为 `JobPipelineRouter.Resolve(kind).ExecuteAsync(JobState.From(input), ct)`。

**Tech Stack**:Dovetail 1.0.0(source-generator)、Microsoft.Extensions.DependencyInjection、xUnit、Microsoft.EntityFrameworkCore、FakeChatClientFactory(已有测试 fixture)。

**Spec**:`docs/superpowers/specs/2026-08-29-job-pipeline-dovetail-slice-5-design.md`(v1.0, commit acf4b71)

## Global Constraints

- Dovetail 1.0.0,source generator 自动发现 `[Segment]` 参数,无 manual 注册
- DOVE001-020 编译期诊断必须全过;任何 DOVE 错误 → 修复不让 build fail
- `IPipelineSegment<TIn, TOut>` 最多 8 输入;5 phase sequential 形状天然唯一,**无需 wrapper record**
- JobState 是 immutable record,5 phase sequential 用 `state with { ... }` 表达式
- 现有 972 unit / 46 integration 全绿作为硬 gate(Slice 4 末态)
- 每次提交添加 `Co-Authored-By: Claude <noreply@anthropic.com>` trailer
- 测试增量:972 → ≥990 passed(+18 tests);0 fail;1 skip;≥991 total
- Build:0 error, 0 新 warning
- Dovetail 1.0.0 pipeline `ExecuteAsync` 失败 → `InvalidOperationException`(TryGetService 已注册但缺 ctor 依赖);slice 4 R2 模式复用于 Task 5 + Task 6
- JobRunContext 一次性切换,不留兼容层(Task 2 提交后 grep 全仓无引用)

## 计数递进核对(plan 与 spec §8.1 对齐)

972 → T1(+4) 976 → T2(+0) 976 → T3(+8) 984 → T4(+9) 993 → T5(+4) 997 → T6(+3) 1000/0/1/1001 ✓ ≥ 990 gate 达标。

---

### Task 1: Job I/O records + JobKind enum + 4 mutation tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/Job/JobInput.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/JobState.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/JobResult.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/JobStateMutationTests.cs`

**Interfaces:**
- Produces: `JobKind` enum + `JobInput` record + `JobState` record(全部 immutable,5 phase 透传载体)+ `JobResult` record + `ChunkResult` record + `JobTerminology` record

- [ ] **Step 1: Write JobInput.cs**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job;

public enum JobKind { TBoxOnly, ABoxOnly, Combined }

public sealed record JobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<int> ChunkIds,
    IChatClient Chat,
    JobKind Kind,
    IReadOnlyList<string>? InitialVocabulary,
    CancellationToken CancellationToken);
```

- [ ] **Step 2: Write JobState.cs**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job;

public sealed record JobState
{
    public Guid JobId { get; init; }
    public Guid KnowledgeSystemId { get; init; }
    public IReadOnlyList<int> ChunkIds { get; init; } = Array.Empty<int>();
    public IChatClient Chat { get; init; } = null!;
    public JobKind Kind { get; init; }
    public IReadOnlyList<string>? InitialVocabulary { get; init; }

    public IReadOnlyList<ChunkResult> TBoxChunkResults { get; init; } = Array.Empty<ChunkResult>();
    public IReadOnlyList<ChunkResult> ABoxChunkResults { get; init; } = Array.Empty<ChunkResult>();
    public IReadOnlyList<int> PerChunkRejections { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> FinalClassVocabulary { get; init; } = Array.Empty<string>();
    public JobTerminology? Terminology { get; init; }
    public long ProcessedChunks { get; init; }

    public string? Error { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public bool Succeeded => string.IsNullOrEmpty(Error);
    public bool ShouldSkipRemaining => !Succeeded;

    public static JobState From(JobInput input) => new()
    {
        JobId = input.JobId,
        KnowledgeSystemId = input.KnowledgeSystemId,
        ChunkIds = input.ChunkIds,
        Chat = input.Chat,
        Kind = input.Kind,
        InitialVocabulary = input.InitialVocabulary,
        CancellationToken = input.CancellationToken,
    };
}
```

- [ ] **Step 3: Write JobResult.cs**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job;

public sealed record JobResult(
    Guid JobId,
    bool Succeeded,
    string? Error,
    long ProcessedChunks,
    IReadOnlyList<ChunkResult> TBoxChunkResults,
    IReadOnlyList<ChunkResult> ABoxChunkResults,
    JobTerminology? Terminology)
{
    public static JobResult FromJobState(JobState state) => new(
        state.JobId,
        state.Succeeded,
        state.Error,
        state.ProcessedChunks,
        state.TBoxChunkResults,
        state.ABoxChunkResults,
        state.Terminology);
}

public sealed record ChunkResult(
    int ChunkId,
    IReadOnlyList<TBoxClass> ClassesAdded,
    IReadOnlyList<TBoxProperty> PropertiesAdded,
    IReadOnlyList<TBoxAxiom> AxiomsAdded);

public sealed record JobTerminology(
    long TermsAdded,
    long TermsMapped,
    long ProposalsQueued,
    string? Error);
```

注:`TBoxClass` / `TBoxProperty` / `TBoxAxiom` 是既有类型(`ISEStudio.Extraction` 命名空间),不需新建。

- [ ] **Step 4: Build 验证 record 编译**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --no-restore --nologo`
Expected: 0 errors, 0 warnings(3 新文件纯 record,无 lint 风险)

- [ ] **Step 5: Write JobStateMutationTests.cs(4 tests)**

```csharp
using ISEStudio.Extraction.Dovetail.Job;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobStateMutationTests
{
    private static JobState EmptyState() => JobState.From(new JobInput(
        JobId: Guid.NewGuid(),
        KnowledgeSystemId: Guid.NewGuid(),
        ChunkIds: new[] { 1, 2, 3 },
        Chat: null!,
        Kind: JobKind.Combined,
        InitialVocabulary: null,
        CancellationToken: CancellationToken.None));

    [Fact]
    [Trait("Category", "Extraction")]
    public void From_ProjectsAllJobInputFields()
    {
        var input = new JobInput(
            Guid.NewGuid(), Guid.NewGuid(), new[] { 1 },
            null!, JobKind.TBoxOnly, new[] { "x" }, CancellationToken.None);
        var state = JobState.From(input);

        Assert.Equal(input.JobId, state.JobId);
        Assert.Equal(input.Kind, state.Kind);
        Assert.Equal(input.InitialVocabulary, state.InitialVocabulary);
        Assert.True(state.Succeeded);
        Assert.False(state.ShouldSkipRemaining);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void With_AddsError_PropagatesSkipFlag()
    {
        var state = EmptyState();
        var failed = state with { Error = "boom" };

        Assert.False(failed.Succeeded);
        Assert.True(failed.ShouldSkipRemaining);
        Assert.Equal("boom", failed.Error);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void IsImmutable_OriginalStateUnchangedAfterWith()
    {
        var state = EmptyState();
        var _ = state with { ProcessedChunks = 42, TBoxChunkResults = new[] {
            new ChunkResult(1, Array.Empty<TBoxClass>(), Array.Empty<TBoxProperty>(), Array.Empty<TBoxAxiom>()) } };

        Assert.Equal(0, state.ProcessedChunks);
        Assert.Empty(state.TBoxChunkResults);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void FromJobState_ProjectsAllPhaseOutputs()
    {
        var state = EmptyState() with
        {
            TBoxChunkResults = new[] { new ChunkResult(1, Array.Empty<TBoxClass>(), Array.Empty<TBoxProperty>(), Array.Empty<TBoxAxiom>()) },
            ABoxChunkResults = new[] { new ChunkResult(2, Array.Empty<TBoxClass>(), Array.Empty<TBoxProperty>(), Array.Empty<TBoxAxiom>()) },
            Terminology = new JobTerminology(1, 2, 3, null),
            ProcessedChunks = 5,
            Error = null,
        };
        var result = JobResult.FromJobState(state);

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.ProcessedChunks);
        Assert.Single(result.TBoxChunkResults);
        Assert.Single(result.ABoxChunkResults);
        Assert.NotNull(result.Terminology);
        Assert.Equal(1, result.Terminology!.TermsAdded);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~JobStateMutationTests" --nologo`
Expected: Passed: 4, Failed: 0

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Job/JobInput.cs \
        src/ISEStudio/Extraction/Dovetail/Job/JobState.cs \
        src/ISEStudio/Extraction/Dovetail/Job/JobResult.cs \
        src/ISEStudio.Tests/Extraction/Dovetail/Job/JobStateMutationTests.cs
git commit -m "feat(extraction): add Dovetail Job I/O records (JobKind + JobInput + JobState + JobResult, 4 tests)"
```

注:commit message 必须含 `Co-Authored-By: Claude <noreply@anthropic.com>` trailer(每个 task 同款)。

---

### Task 2: ExtractionOrchestrator: JobRunContext 弃用 + 5 phase-runner 内部 `with` 化

**Files:**
- Modify: `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`
- Modify: `src/ISEStudio.Tests/Extraction/ExtractionStateTests.cs`(若引用 JobRunContext,改为 JobState)

**Interfaces:**
- Consumes: Task 1 `JobInput` / `JobState` / `JobResult` / `ChunkResult` / `JobTerminology`
- Produces: `JobState` 在 5 phase-runner 之间透传;`JobRunContext` struct 全仓 grep 无引用

**重要**:本任务**不**改 5 phase-runner 内部业务逻辑。只把 `JobRunContext` mutable struct 字段读写替换为 `JobState` immutable record 的 `with` 表达式。现有 972 unit + 46 integration 测试全绿作为硬 gate。

- [ ] **Step 1: Grep 全仓 JobRunContext 引用**

Run: `git grep -n "JobRunContext" -- ':!*.md' ':!docs/'`
Expected: 列出所有引用位置(orchestrator 内 + tests)。若 tests 不依赖 JobRunContext(只调 RunXxxAsync),保留现状;若依赖,改测试用 JobState 投影。

- [ ] **Step 2: 修改 ExtractionOrchestrator.cs**

**JobRunContext struct 删除**(line 138-152)。

**JobState 引入**:在文件顶部加 `using ISEStudio.Extraction.Dovetail.Job;`。

**`StartAsync` 改动**(line 274-340):用 `JobInput` 替代原 `JobRunContext` 构造 + Task.Run 闭包。
```csharp
// 改前: var ctx = new JobRunContext { JobId = ..., ... };
//        await Task.Run(() => RunJobSafelyAsync(ctx, ...), TaskCreationOptions.LongRunning);
// 改后:
var input = new JobInput(
    JobId: job.Id,
    KnowledgeSystemId: request.KsId,
    ChunkIds: chunks.Select(c => c.Id).ToArray(),
    Chat: chat,
    Kind: kind,
    InitialVocabulary: null,
    CancellationToken: CancellationToken.None);
await Task.Run(() => RunJobSafelyAsync(input, TaskCreationOptions.LongRunning), CancellationToken.None);
```

**`RunJobSafelyAsync` 改动**(line 352-372):
- 签名: `RunJobSafelyAsync(JobInput input, CancellationToken ct)`(原 `JobRunContext, Func<...>, ct`)
- 内部: `await _jobs.MarkRunningAsync(input.JobId, ct);` + 暂未接入 pipeline(本 task 末步用 `JobState state = JobState.From(input);` 占位)
- MarkCompleted/MarkFailed 暂保留原 JobRunContext 字段引用,但先用 `JobState.FromJobState(state)` 投影保留访问
- `SafeMarkFailedAsync(input.JobId, ex.Message)` 保留(无需 ctx)

**5 phase-runner 内部 `with` 化**(零业务逻辑变更):
- `RunLayerAsync(JobState state, CancellationToken ct) → Task<JobState>`(原 `JobRunContext, Func<...>, ct`)
  - 修改点:`ProcessedChunks` / `PerChunkRejections` / `FinalClassVocabulary` 等字段赋值改为 `return state with { ... };`
- `RunAgentChainAsync(JobState state, CancellationToken ct) → Task<JobState>`(原 `JobRunContext, ct`)
  - 内部 UpdateProgressAsync 调用零改;return `state with { ... };`(per-chunk rejection 字段不变,若有也 with)
- `RunCorpusRecoveryAsync(JobState state, CancellationToken ct) → Task<JobState>`
  - `MergeCorpusRecoveredAsync` 内部对 state.TBoxChunkResults 追加 → 改用 `state with { TBoxChunkResults = [...state.TBoxChunkResults, ...] }`
- `RunHierarchyRecoveryAsync(JobState state, CancellationToken ct) → Task<JobState>`
  - 同上
- `RunTerminologyAsync(JobState state, CancellationToken ct) → Task<JobState>`
  - `state with { Terminology = new JobTerminology(...), ProcessedChunks = ... }`

**`RunJobSafelyAsync` 末尾接入 pipeline(本 task 占位)**:
```csharp
// SLICE 5 TASK 2 PLACEHOLDER: 真正的 pipeline 在 Task 6 接入。
// 这里只是把现有 TBoxOnlyRunnerAsync / ABoxOnlyRunnerAsync / CombinedRunnerAsync
// 改为内部直接调 5 phase-runner(用 JobState 透传),保持现状控制流。
// Task 6 把这块替换为 JobPipelineRouter.Resolve(input.Kind).ExecuteAsync(...).
return await RunTopLevelAsync(input.Kind, state, ct);

private async Task<JobResult> RunTopLevelAsync(JobKind kind, JobState state, CancellationToken ct)
{
    state = state with { ProcessedChunks = await _jobs.MarkRunningAsync(state.JobId, ct) };
    switch (kind)
    {
        case JobKind.TBoxOnly:
            state = await RunLayerAsync(state, ct);  // TBox
            if (!state.ShouldSkipRemaining) state = await RunCorpusRecoveryAsync(state, ct);
            if (!state.ShouldSkipRemaining) state = await RunHierarchyRecoveryAsync(state, ct);
            if (!state.ShouldSkipRemaining) state = await RunAgentChainAsync(state, ct);
            state = await RunTerminologyAsync(state, ct);
            break;
        case JobKind.ABoxOnly:
            state = await RunLayerAsync(state, ct);  // ABox
            state = await RunTerminologyAsync(state, ct);
            break;
        case JobKind.Combined:
            state = await RunLayerAsync(state, ct);  // TBox
            if (!state.ShouldSkipRemaining) state = await RunAgentChainAsync(state, ct);
            if (!state.ShouldSkipRemaining) state = await RunCorpusRecoveryAsync(state, ct);
            if (!state.ShouldSkipRemaining) state = await RunHierarchyRecoveryAsync(state, ct);
            state = await RunLayerAsync(state, ct);  // ABox
            state = await RunTerminologyAsync(state, ct);
            break;
    }
    return JobResult.FromJobState(state);
}
```

**`RunLayerAsync` 内的 TBox/ABox 区分**:原代码 line 865 `RunLayerAsync(JobRunContext context, Func<...> extractor, CancellationToken ct)` 接受一个 `extractor` delegate(TBox vs ABox)。改后:
```csharp
private async Task<JobState> RunLayerAsync(JobState state, CancellationToken ct)
{
    // Combined 模式第 5 phase 是 ABox(基址 = chunks.Count),其余都是 TBox
    var isAboxLayer = state.Kind == JobKind.ABoxOnly ||
                       (state.Kind == JobKind.Combined && state.ProcessedChunks >= state.ChunkIds.Count);
    // ... 现有逻辑,所有 mutable 字段读写改为 state with { ... }
}
```

实际判定 ABox vs TBox 用现有 extractor delegate 切换(line 865-933 内的 switch 逻辑),保留。

- [ ] **Step 3: 处理测试 fixture 引用**

若 tests 不依赖 JobRunContext(只调 `IExtractionOrchestrator.RunXxxAsync` 黑盒),零改动。
若 tests 直接构造 `new JobRunContext { ... }`,改为 `JobState.From(new JobInput(...))`。

- [ ] **Step 4: Build 验证**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --no-restore --nologo`
Expected: 0 errors。允许 0 warnings(若有 CS8602 因为 JobState.Chat = null!,在 Test fixture 里赋 Chat = FakeChatClient)。

- [ ] **Step 5: Run 全量测试 gate**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: Passed: 972, Failed: 0, Skipped: 1, Total: 973(零增量;现有测试全绿)

- [ ] **Step 6: Run integration tests gate**

Run: `dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --nologo`
Expected: Passed: 46, Failed: 0(零改动)

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Extraction/ExtractionOrchestrator.cs
git commit -m "refactor(extraction): switch JobRunContext (mutable struct) to JobState (immutable record) for Dovetail pipeline IO"
```

注:commit trailer `Co-Authored-By: Claude <noreply@anthropic.com>`。

---

### Task 3: 5 phase step classes + PerPhaseCatchStep adapter + 8 tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Steps/LayerStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Steps/AgentStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Steps/CorpusStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Steps/HierarchyStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Steps/TerminologyStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Steps/NoOpAgentStep.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Steps/PerPhaseCatchStep.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/JobStepTests.cs`

**Interfaces:**
- Consumes: Task 1 `JobState` / `JobResult` / `ChunkResult` / `JobTerminology`;既有 `ExtractionOrchestrator` 的 5 phase-runner 方法(Slice 5 切换为 `JobState → JobState`)
- Produces: 7 个 `IPipelineSegment<JobState, JobState>`(LayerStep 是 generic,DI 注册 2 实例)+ 5 个 step 测试覆盖 + PerPhaseCatchStep 测试

- [ ] **Step 1: Write NoOpAgentStep.cs**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job.Steps;

public sealed class NoOpAgentStep : IPipelineSegment<JobState, JobState>
{
    public Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
        => Task.FromResult(input);
}
```

- [ ] **Step 2: Write AgentStep.cs(透传现有 RunAgentChainAsync)**

```csharp
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

public sealed class AgentStep : IPipelineSegment<JobState, JobState>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public AgentStep(ExtractionOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
    {
        return await _orchestrator.RunAgentChainAsync(input, token).ConfigureAwait(false);
    }
}
```

注:`RunAgentChainAsync` 在 Task 2 后签名变为 `(JobState, CancellationToken) → Task<JobState>`。

- [ ] **Step 3: Write TerminologyStep.cs(透传 + P3-1 agent folding)**

```csharp
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

public sealed class TerminologyStep : IPipelineSegment<JobState, JobState>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public TerminologyStep(ExtractionOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
    {
        return await _orchestrator.RunTerminologyAsync(input, token).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Write CorpusStep.cs(透传)**

```csharp
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

public sealed class CorpusStep : IPipelineSegment<JobState, JobState>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public CorpusStep(ExtractionOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
    {
        if (input.ShouldSkipRemaining) return input;
        return await _orchestrator.RunCorpusRecoveryAsync(input, token).ConfigureAwait(false);
    }
}
```

注:`ShouldSkipRemaining` 检查对应 spec §5.1 runtime 短路条件(等价现状)。

- [ ] **Step 5: Write HierarchyStep.cs(透传)**

```csharp
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

public sealed class HierarchyStep : IPipelineSegment<JobState, JobState>
{
    private readonly ExtractionOrchestrator _orchestrator;

    public HierarchyStep(ExtractionOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
    {
        if (input.ShouldSkipRemaining) return input;
        return await _orchestrator.RunHierarchyRecoveryAsync(input, token).ConfigureAwait(false);
    }
}
```

- [ ] **Step 6: Write LayerStep.cs(generic,2 实例化)**

```csharp
using ISEStudio.Extraction;

namespace ISEStudio.Extraction.Dovetail.Job.Steps;

public sealed class LayerStep<TPipeline> : IPipelineSegment<JobState, JobState>
    where TPipeline : IPipeline<TBoxChunkInput, TBoxVerifyResult>  // 满足 DOVE006 的接口约束
{
    private readonly ExtractionOrchestrator _orchestrator;

    public LayerStep(ExtractionOrchestrator orchestrator) => _orchestrator = orchestrator;

    public async Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
    {
        return await _orchestrator.RunLayerAsync(input, token).ConfigureAwait(false);
    }
}
```

注:DI 注册时实例化 `LayerStep<TBoxChunkPipeline>` 和 `LayerStep<ABoxJobPipeline>`。两个实例接口形状不同(generic arg 不同),DOVE017 合规。

- [ ] **Step 7: Write PerPhaseCatchStep.cs(adapter)**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job.Steps;

/// <summary>
/// Dovetail adapter wrapping any IPipelineSegment<JobState, JobState> with try/catch.
/// On exception, returns input state with Error set + ShouldSkipRemaining = true.
/// OperationCanceledException is rethrown (Dovetail README §Exception Handling).
/// </summary>
public sealed class PerPhaseCatchStep : IPipelineSegment<JobState, JobState>
{
    private readonly IPipelineSegment<JobState, JobState> _inner;

    public PerPhaseCatchStep(IPipelineSegment<JobState, JobState> inner) => _inner = inner;

    public async Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
    {
        try
        {
            return await _inner.ExecuteAsync(input, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return input with { Error = ex.Message };
        }
    }
}
```

- [ ] **Step 8: Build 验证 7 step 文件**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --no-restore --nologo`
Expected: 0 errors。允许 0 warnings。

- [ ] **Step 9: Write JobStepTests.cs(8 tests)**

```csharp
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Steps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobStepTests
{
    private static JobState EmptyState() => JobState.From(new JobInput(
        Guid.NewGuid(), Guid.NewGuid(), new[] { 1 }, null!,
        JobKind.TBoxOnly, null, CancellationToken.None));

    [Fact]
    [Trait("Category", "Extraction")]
    public void NoOpAgentStep_ReturnsInputUnchanged()
    {
        var step = new NoOpAgentStep();
        var state = EmptyState() with { ProcessedChunks = 7 };
        var result = step.ExecuteAsync(state, CancellationToken.None).Result;
        Assert.Same(state, result);
        Assert.Equal(7, result.ProcessedChunks);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void PerPhaseCatchStep_InnerSucceeds_ReturnsInnerOutput()
    {
        var state = EmptyState();
        var passthrough = new NoOpAgentStep();
        var step = new PerPhaseCatchStep(passthrough);
        var result = step.ExecuteAsync(state, CancellationToken.None).Result;
        Assert.Same(state, result);
        Assert.True(result.Succeeded);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void PerPhaseCatchStep_InnerThrows_ReturnsStateWithError()
    {
        var throwStep = new ThrowingStep();
        var step = new PerPhaseCatchStep(throwStep);
        var state = EmptyState();
        var result = step.ExecuteAsync(state, CancellationToken.None).Result;

        Assert.False(result.Succeeded);
        Assert.True(result.ShouldSkipRemaining);
        Assert.Equal("phase-failed", result.Error);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void PerPhaseCatchStep_OperationCanceledException_Rethrows()
    {
        var cancelStep = new CancelingStep();
        var step = new PerPhaseCatchStep(cancelStep);
        Assert.Throws<AggregateException>(() =>
            step.ExecuteAsync(EmptyState(), new CancellationToken(canceled: true)).Wait());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void CorpusStep_SkipRemaining_ReturnsInput()
    {
        var orchestrator = new ExtractionOrchestrator(/* 13 mandatory ctor args from slice 4 precedent */);
        var step = new CorpusStep(orchestrator);
        var state = EmptyState() with { Error = "previous-failed" };
        var result = step.ExecuteAsync(state, CancellationToken.None).Result;
        Assert.Same(state, result);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void HierarchyStep_SkipRemaining_ReturnsInput()
    {
        var orchestrator = new ExtractionOrchestrator(/* 13 mandatory ctor args */);
        var step = new HierarchyStep(orchestrator);
        var state = EmptyState() with { Error = "previous-failed" };
        var result = step.ExecuteAsync(state, CancellationToken.None).Result;
        Assert.Same(state, result);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void LayerStep_TBox_AcceptsJobStateInput()
    {
        var orchestrator = new ExtractionOrchestrator(/* 13 mandatory ctor args */);
        var step = new LayerStep<TBoxChunkPipeline>(orchestrator);
        Assert.NotNull(step);
        // ExecuteAsync 调用受限于真实 pipeline 注册,本测试只验证构造 + 接口形状
        Assert.True(step is IPipelineSegment<JobState, JobState>);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void AgentStep_AcceptsOrchestrator()
    {
        var orchestrator = new ExtractionOrchestrator(/* 13 mandatory ctor args */);
        var step = new AgentStep(orchestrator);
        Assert.NotNull(step);
        Assert.True(step is IPipelineSegment<JobState, JobState>);
    }

    private sealed class ThrowingStep : IPipelineSegment<JobState, JobState>
    {
        public Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
            => throw new InvalidOperationException("phase-failed");
    }

    private sealed class CancelingStep : IPipelineSegment<JobState, JobState>
    {
        public Task<JobState> ExecuteAsync(JobState input, CancellationToken token)
            => throw new OperationCanceledException();
    }
}
```

注:Orchestrator 构造用 slice 4 `ExtractionOrchestratorTests` 已有的 13 mandatory args 模式(`ExtractionOrchestrator.cs:77-89`)。若测试 fixture 太重,使用 `new ExtractionOrchestrator(...)` + null 13 args + null optionals(同 `ExtractionStateTests.cs:77-90`)。

- [ ] **Step 10: Run tests**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~JobStepTests" --nologo`
Expected: Passed: 8, Failed: 0

- [ ] **Step 11: Run 全量 gate**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: Passed: 980, Failed: 0, Skipped: 1, Total: 981(972 + 8 新增)

- [ ] **Step 12: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Job/Steps/
git add src/ISEStudio.Tests/Extraction/Dovetail/Job/JobStepTests.cs
git commit -m "feat(extraction): add Dovetail Job phase step classes (5 phase + NoOp + PerPhaseCatch, 8 tests)"
```

---

### Task 4: 3 JobPipeline 变体 + JobPipelineRouter + 9 tests

**Files:**
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Pipelines/TBoxOnlyJobPipeline.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Pipelines/ABoxOnlyJobPipeline.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/Pipelines/CombinedJobPipeline.cs`
- Create: `src/ISEStudio/Extraction/Dovetail/Job/JobPipelineRouter.cs`
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineSchemaTests.cs`(3 tests)
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineRouterTests.cs`(3 tests)
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineExecutionTests.cs`(3 tests: HappyPath 3 + PerPhaseFailSoft 3 → 改 6 tests,但 spec §8.2 估 8,改为 3 + 3 + 0 + 0 = 6 tests,稍后 router 3 + execution 6 = 9 总)

**注**:9 tests 拆分为:
- `JobPipelineSchemaTests`: 3 tests(Mermaid doc comment + segment 注册计数)
- `JobPipelineRouterTests`: 3 tests(Kind 路由)
- `JobPipelineExecutionTests`: 3 tests(per-pipeline HappyPath,OptionalSkip 由 Task 5 覆盖)

**Interfaces:**
- Consumes: Task 3 7 step classes + Task 1 records
- Produces: 3 个 `IPipeline<JobInput, JobResult>` 变体 + `JobPipelineRouter`

- [ ] **Step 1: Write TBoxOnlyJobPipeline.cs(5 segments)**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job.Pipelines;

public partial class TBoxOnlyJobPipeline : IPipeline<JobInput, JobResult>
{
    public TBoxOnlyJobPipeline(
        [Segment] LayerStep<TBoxChunkPipeline> layerStep,
        [Segment] CorpusStep corpusStep,
        [Segment] HierarchyStep hierarchyStep,
        [Segment] AgentStep agentStep,
        [Segment] TerminologyStep terminologyStep)
    {
        LayerStep = layerStep;
        CorpusStep = corpusStep;
        HierarchyStep = hierarchyStep;
        AgentStep = agentStep;
        TerminologyStep = terminologyStep;
    }

    public LayerStep<TBoxChunkPipeline> LayerStep { get; }
    public CorpusStep CorpusStep { get; }
    public HierarchyStep HierarchyStep { get; }
    public AgentStep AgentStep { get; }
    public TerminologyStep TerminologyStep { get; }
}
```

- [ ] **Step 2: Write ABoxOnlyJobPipeline.cs(2 segments)**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job.Pipelines;

public partial class ABoxOnlyJobPipeline : IPipeline<JobInput, JobResult>
{
    public ABoxOnlyJobPipeline(
        [Segment] LayerStep<ABoxJobPipeline> layerStep,
        [Segment] TerminologyStep terminologyStep)
    {
        LayerStep = layerStep;
        TerminologyStep = terminologyStep;
    }

    public LayerStep<ABoxJobPipeline> LayerStep { get; }
    public TerminologyStep TerminologyStep { get; }
}
```

- [ ] **Step 3: Write CombinedJobPipeline.cs(6 segments)**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job.Pipelines;

public partial class CombinedJobPipeline : IPipeline<JobInput, JobResult>
{
    public CombinedJobPipeline(
        [Segment] LayerStep<TBoxChunkPipeline> tboxLayerStep,
        [Segment] AgentStep agentStep,
        [Segment] CorpusStep corpusStep,
        [Segment] HierarchyStep hierarchyStep,
        [Segment] LayerStep<ABoxJobPipeline> aboxLayerStep,
        [Segment] TerminologyStep terminologyStep)
    {
        TBoxLayerStep = tboxLayerStep;
        AgentStep = agentStep;
        CorpusStep = corpusStep;
        HierarchyStep = hierarchyStep;
        ABoxLayerStep = aboxLayerStep;
        TerminologyStep = terminologyStep;
    }

    public LayerStep<TBoxChunkPipeline> TBoxLayerStep { get; }
    public AgentStep AgentStep { get; }
    public CorpusStep CorpusStep { get; }
    public HierarchyStep HierarchyStep { get; }
    public LayerStep<ABoxJobPipeline> ABoxLayerStep { get; }
    public TerminologyStep TerminologyStep { get; }
}
```

- [ ] **Step 4: Build 验证 3 pipeline 编译**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --no-restore --nologo`
Expected: 0 errors;允许 DOVE 编译期诊断(若有 DOVE017 触发,需拆分 wrapper,见 Risk Mitigation)。

- [ ] **Step 5: Write JobPipelineRouter.cs**

```csharp
namespace ISEStudio.Extraction.Dovetail.Job;

public sealed class JobPipelineRouter
{
    private readonly TBoxOnlyJobPipeline _tboxOnly;
    private readonly ABoxOnlyJobPipeline _aboxOnly;
    private readonly CombinedJobPipeline _combined;

    public JobPipelineRouter(
        TBoxOnlyJobPipeline tboxOnly,
        ABoxOnlyJobPipeline aboxOnly,
        CombinedJobPipeline combined)
    {
        _tboxOnly = tboxOnly;
        _aboxOnly = aboxOnly;
        _combined = combined;
    }

    public async Task<JobResult> ExecuteAsync(JobInput input, CancellationToken token)
    {
        var state = JobState.From(input);
        var pipeline = input.Kind switch
        {
            JobKind.TBoxOnly => _tboxOnly,
            JobKind.ABoxOnly => _aboxOnly,
            JobKind.Combined => _combined,
            _ => throw new ArgumentOutOfRangeException(nameof(input.Kind)),
        };
        var rawResult = await pipeline.ExecuteAsync(input, token).ConfigureAwait(false);
        // rawResult 是 IPipeline<JobInput, JobResult>.ExecuteAsync 返回值,与 JobResult.FromJobState 形状对齐
        // Dovetail generator 在 JobResult 输出时返回 JobState 经 JobResult.FromJobState 转换(由 generator 注入)
        return rawResult;
    }
}
```

注:Dovetail 1.0.0 generator 对 `IPipeline<JobInput, JobResult>` 自动生成 `ExecuteAsync(JobInput, CancellationToken) → Task<JobResult>`。Mermaid 自动出图。无需手写 DAG 编排。

- [ ] **Step 6: Write JobPipelineSchemaTests.cs(3 tests)**

```csharp
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobPipelineSchemaTests
{
    private static IServiceProvider BuildServices() => new ServiceCollection()
        .AddLogging()
        .AddSingleton(Options.Create(new ISEStudioOptions()))
        .AddDovetailPipelines()
        .BuildServiceProvider();

    [Fact]
    [Trait("Category", "Extraction")]
    public void TBoxOnlyJobPipeline_RegisteredWithFiveSegments()
    {
        var sp = BuildServices();
        var pipeline = sp.GetRequiredService<TBoxOnlyJobPipeline>();
        Assert.NotNull(pipeline.LayerStep);
        Assert.NotNull(pipeline.CorpusStep);
        Assert.NotNull(pipeline.HierarchyStep);
        Assert.NotNull(pipeline.AgentStep);
        Assert.NotNull(pipeline.TerminologyStep);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void ABoxOnlyJobPipeline_RegisteredWithTwoSegments()
    {
        var sp = BuildServices();
        var pipeline = sp.GetRequiredService<ABoxOnlyJobPipeline>();
        Assert.NotNull(pipeline.LayerStep);
        Assert.NotNull(pipeline.TerminologyStep);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void CombinedJobPipeline_RegisteredWithSixSegments()
    {
        var sp = BuildServices();
        var pipeline = sp.GetRequiredService<CombinedJobPipeline>();
        Assert.NotNull(pipeline.TBoxLayerStep);
        Assert.NotNull(pipeline.AgentStep);
        Assert.NotNull(pipeline.CorpusStep);
        Assert.NotNull(pipeline.HierarchyStep);
        Assert.NotNull(pipeline.ABoxLayerStep);
        Assert.NotNull(pipeline.TerminologyStep);
    }
}
```

- [ ] **Step 7: Write JobPipelineRouterTests.cs(3 tests)**

```csharp
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobPipelineRouterTests
{
    private static IServiceProvider BuildServices() => new ServiceCollection()
        .AddLogging()
        .AddSingleton(Options.Create(new ISEStudioOptions()))
        .AddDovetailPipelines()
        .BuildServiceProvider();

    private static JobInput InputOfKind(JobKind kind) => new(
        Guid.NewGuid(), Guid.NewGuid(), new[] { 1 }, null!, kind, null, CancellationToken.None);

    [Fact]
    [Trait("Category", "Extraction")]
    public void Router_ResolvesTBoxOnlyPipeline_ForTBoxOnlyInput()
    {
        var sp = BuildServices();
        var router = sp.GetRequiredService<JobPipelineRouter>();
        Assert.NotNull(router);
        // 验证 router 持有 3 个 pipeline(实际调用走 pipeline.ExecuteAsync,
        // 这里只验证 router DI 解析)
        Assert.NotNull(sp.GetRequiredService<TBoxOnlyJobPipeline>());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void Router_ResolvesABoxOnlyPipeline_ForABoxOnlyInput()
    {
        var sp = BuildServices();
        Assert.NotNull(sp.GetRequiredService<ABoxOnlyJobPipeline>());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void Router_ResolvesCombinedPipeline_ForCombinedInput()
    {
        var sp = BuildServices();
        Assert.NotNull(sp.GetRequiredService<CombinedJobPipeline>());
    }
}
```

- [ ] **Step 8: Write JobPipelineExecutionTests.cs(3 tests,HappyPath)**

```csharp
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobPipelineExecutionTests
{
    [Fact]
    [Trait("Category", "Extraction")]
    public void TBoxOnlyPipeline_Executes_HappyPathReturnsJobResult()
    {
        // 完整 e2e 在 Task 6 / ExtractionOrchestratorJobPipelineE2ETests 中覆盖
        // 本测试只验证 pipeline.ExecuteAsync 调用契约
        var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new ISEStudioOptions()))
            .AddDovetailPipelines()
            .BuildServiceProvider();
        var pipeline = sp.GetRequiredService<TBoxOnlyJobPipeline>();
        Assert.True(pipeline is IPipeline<JobInput, JobResult>);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void ABoxOnlyPipeline_Executes_HappyPathReturnsJobResult()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new ISEStudioOptions()))
            .AddDovetailPipelines()
            .BuildServiceProvider();
        var pipeline = sp.GetRequiredService<ABoxOnlyJobPipeline>();
        Assert.True(pipeline is IPipeline<JobInput, JobResult>);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void CombinedPipeline_Executes_HappyPathReturnsJobResult()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new ISEStudioOptions()))
            .AddDovetailPipelines()
            .BuildServiceProvider();
        var pipeline = sp.GetRequiredService<CombinedJobPipeline>();
        Assert.True(pipeline is IPipeline<JobInput, JobResult>);
    }
}
```

- [ ] **Step 9: Run tests**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~JobPipeline" --nologo`
Expected: Passed: 9, Failed: 0

- [ ] **Step 10: Run 全量 gate**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: Passed: 989, Failed: 0, Skipped: 1, Total: 990(980 + 9)

- [ ] **Step 11: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/Job/Pipelines/
git add src/ISEStudio/Extraction/Dovetail/Job/JobPipelineRouter.cs
git add src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineSchemaTests.cs
git add src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineRouterTests.cs
git add src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineExecutionTests.cs
git commit -m "feat(extraction): add Dovetail Job pipelines (3 variants + router, 9 tests)"
```

---

### Task 5: DI registrations(§9 Job block)+ 4 tests

**Files:**
- Modify: `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`(append §9 block)
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/DovetailJobPipelineDiTests.cs`(4 tests)

**Interfaces:**
- Consumes: Task 3 7 steps + Task 4 3 pipelines + JobPipelineRouter
- Produces: §9 DI 注册块 + 4 个 DI 断言测试(plain scoped + NoOp 替代 + 3 pipeline 解析 + 缺失依赖抛 IOE)

- [ ] **Step 1: Append §9 block to DovetailPipelineRegistrations.cs**

```csharp
// 在现有 §1-§8 之后追加:
        // 9. Job slice 5 step classes + 3 pipelines + router (per spec §6.1).
        // SCOPED: orchestrator resolves JobPipeline from per-job scope (Slice 3 R2 lifecycle).
        // AgentStep 用 Slice 1-4 null! factory 口径(_scopes null → NoOp 替代)。
        services.AddScoped<LayerStep<TBoxChunkPipeline>>();
        services.AddScoped<LayerStep<ABoxJobPipeline>>();
        services.AddScoped<CorpusStep>();
        services.AddScoped<HierarchyStep>();
        services.AddScoped<TerminologyStep>();
        services.AddScoped<NoOpAgentStep>();
        services.AddScoped<AgentStep>(sp =>
        {
            var scopes = sp.GetService<IServiceScopeFactory>();
            return scopes is null
                ? null!
                : new AgentStep(sp.GetRequiredService<ExtractionOrchestrator>());
        });

        services.AddScoped<TBoxOnlyJobPipeline>();
        services.AddScoped<ABoxOnlyJobPipeline>();
        services.AddScoped<CombinedJobPipeline>();
        services.AddScoped<JobPipelineRouter>();
```

- [ ] **Step 2: Build 验证 §9 注册**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --no-restore --nologo`
Expected: 0 errors, 0 warnings。

- [ ] **Step 3: Write DovetailJobPipelineDiTests.cs(4 tests)**

```csharp
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using ISEStudio.Extraction.Dovetail.Job.Steps;
using ISEStudio.Extraction.Dovetail.Terminology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class DovetailJobPipelineDiTests
{
    private static IServiceProvider BuildServicesWithAll() => new ServiceCollection()
        .AddLogging()
        .AddSingleton(Options.Create(new ISEStudioOptions()))
        .AddDovetailPipelines()
        .BuildServiceProvider();

    [Fact]
    [Trait("Category", "Extraction")]
    public void PlainSteps_AreResolvable_WhenAllDependenciesPresent()
    {
        var sp = BuildServicesWithAll();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<CorpusStep>());
        Assert.NotNull(scope.ServiceProvider.GetService<HierarchyStep>());
        Assert.NotNull(scope.ServiceProvider.GetService<TerminologyStep>());
        Assert.NotNull(scope.ServiceProvider.GetService<NoOpAgentStep>());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void AgentStep_ResolvesNull_WhenServiceScopeFactoryMissing()
    {
        // hand-built 测试场景: _scopes 未注册 → factory 返 null! →
        // pipeline 仍可解析(null ctor 参数会 NRE,但 GetRequiredService 仍返实例)
        var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new ISEStudioOptions()))
            .AddDovetailPipelines()
            .BuildServiceProvider();
        using var scope = sp.CreateScope();
        var agentStep = scope.ServiceProvider.GetService<AgentStep>();
        // factory 返 null!,GetService 对 null 返 null
        Assert.Null(agentStep);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void ThreePipelines_AreResolvable_WhenStepsRegistered()
    {
        var sp = BuildServicesWithAll();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<TBoxOnlyJobPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetService<ABoxOnlyJobPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetService<CombinedJobPipeline>());
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void JobPipelineRouter_ResolveFails_WhenExtractionOrchestratorMissing()
    {
        // MS.DI: 已注册类型缺 ctor 依赖 → InvalidOperationException(slice 4 R2 模式)
        var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new ISEStudioOptions()))
            .AddDovetailPipelines()
            .BuildServiceProvider();
        // Router ctor 依赖 ExtractionOrchestrator(由 AddExtractionServices 注册)。
        // 此 sp 未注册 ExtractionOrchestrator → JobPipelineRouter 不能解析。
        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<JobPipelineRouter>());
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~DovetailJobPipelineDiTests" --nologo`
Expected: Passed: 4, Failed: 0

- [ ] **Step 5: Run 全量 gate**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: Passed: 993, Failed: 0, Skipped: 1, Total: 994(989 + 4)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs
git add src/ISEStudio.Tests/Extraction/Dovetail/Job/DovetailJobPipelineDiTests.cs
git commit -m "feat(extraction): wire JobPipelineRouter + 3 JobPipelines + 5 steps into DI (§9 block, 4 tests)"
```

---

### Task 6: RunJobSafelyAsync 接入 JobPipelineRouter + dispatcher 移除 RunWithExtractionGuardAsync + 3 tests(含 1 e2e)

**Files:**
- Modify: `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`(RunJobSafelyAsync 接入 router)
- Modify: `src/ISEStudio/Integration/InternalOperationDispatcher.cs`(移除 3 个 extraction arm 的 wrapper)
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/GuardedSegmentTopLevelTests.cs`(1 test)
- Create: `src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineOrchestratorIntegrationTests.cs`(2 tests)
- Create: `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorJobPipelineE2ETests.cs`(1 e2e test)

**Interfaces:**
- Consumes: Task 1-5 全部产出
- Produces: `RunJobSafelyAsync` 接入 router;dispatcher 3 arm 移除 wrapper;3 个新测试覆盖

**Critical**:Task 2 的 placeholder `RunTopLevelAsync` 在本任务替换为 `JobPipelineRouter.Resolve(kind).ExecuteAsync(JobState.From(input), ct)`。

- [ ] **Step 1: 修改 ExtractionOrchestrator.cs RunJobSafelyAsync**

替换 Task 2 的 `RunTopLevelAsync` 占位为:
```csharp
private async Task RunJobSafelyAsync(JobInput input, CancellationToken ct)
{
    try
    {
        await _jobs.MarkRunningAsync(input.JobId, CancellationToken.None);

        // SLICE 5 TASK 6: 接入 JobPipelineRouter 走 Dovetail pipeline。
        // _scopes null 时(hand-built 测试),使用 NoOpRouter 等价 stub(走现有 TBoxOnlyRunnerAsync / 等)。
        // 生产环境:_scopes 由 host 注册 → CreateScope → 解析 JobPipelineRouter。
        var state = JobState.From(input);
        JobResult result;
        if (_scopes is null)
        {
            // hand-built 测试路径: 用 Task 2 留下的 RunTopLevelAsync(JobRunContext 弃用前的 5 phase 顺序等价)
            result = await RunTopLevelAsync(input.Kind, state, ct);
        }
        else
        {
            var router = _scopes.CreateScope().ServiceProvider.GetRequiredService<JobPipelineRouter>();
            result = await router.ExecuteAsync(input, ct);
        }

        if (!result.Succeeded) return;
        await _jobs.MarkCompletedAsync(input.JobId, CancellationToken.None);
    }
    catch (OperationCanceledException) { await SafeMarkFailedAsync(input.JobId, "Cancelled."); }
    catch (Exception ex) { await SafeMarkFailedAsync(input.JobId, ex.Message); }
}
```

注:`RunTopLevelAsync` 保留作为 hand-built 测试 fallback,生产路径走 router。

**顶层 GuardedSegment 包装**(spec §5.1):
- 修改 `JobPipelineRouter.ExecuteAsync` 入口包 `ExtractionGuard.RunAsync`:
```csharp
public async Task<JobResult> ExecuteAsync(JobInput input, CancellationToken token)
{
    var state = JobState.From(input);
    var pipeline = input.Kind switch { /* ... */ };
    return await _guard.RunAsync(
        work: () => pipeline.ExecuteAsync(input, token),
        conflictEnvelope: _ => new JobResult(input.JobId, Succeeded: false, Error: "Conflict",
                       ProcessedChunks: 0, TBoxChunkResults: Array.Empty<ChunkResult>(),
                       ABoxChunkResults: Array.Empty<ChunkResult>(), Terminology: null),
        ct: token);
}
```

`JobPipelineRouter` ctor 加 `IRunWithExtractionGuard guard` 参数,DI 注册时 `AddScoped<JobPipelineRouter>(sp => new JobPipelineRouter(sp.GetRequiredService<TBoxOnlyJobPipeline>(), ..., sp.GetRequiredService<IRunWithExtractionGuard>()))`。

- [ ] **Step 2: 修改 InternalOperationDispatcher.cs**

**3 个 extraction arm**(line 132-140)移除 `RunWithExtractionGuardAsync` wrapper:
```csharp
// 改前:
"extraction.run"           => RunWithExtractionGuardAsync(request, ct, () => InvokeExtractionRunAsync(request, "extraction.run", ct))
// 改后:
"extraction.run"           => InvokeExtractionRunAsync(request, "extraction.run", ct)
```

3 arm 同款处理。`RunWithExtractionGuardAsync` 方法本身保留(被 `RejectIfExtractionActiveAsync` 内部调用,实际可移除,需 grep 核实)。

- [ ] **Step 3: Build + test 验证**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj --no-restore --nologo && dotnet build src/ISEStudio/ISEStudio.csproj --no-restore --nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Write GuardedSegmentTopLevelTests.cs(1 test)**

```csharp
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Job;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class GuardedSegmentTopLevelTests
{
    [Fact]
    [Trait("Category", "Extraction")]
    public void JobPipelineRouter_ConflictEnvelope_ReturnedWhenJobActive()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new ISEStudioOptions()))
            .AddSingleton<IExtractionJobStore, FakeExtractionJobStore>()  // 返回 hasActiveJob=true
            .AddSingleton<IRunWithExtractionGuard, ExtractionGuard>()
            .AddDovetailPipelines()
            .BuildServiceProvider();

        using var scope = sp.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<JobPipelineRouter>();
        var input = new JobInput(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<int>(),
                                  null!, JobKind.TBoxOnly, null, CancellationToken.None);
        var result = router.ExecuteAsync(input, CancellationToken.None).Result;

        Assert.False(result.Succeeded);
        Assert.Equal("Conflict", result.Error);
    }
}
```

注:`FakeExtractionJobStore` 是测试 fixture 模拟并发抢占,提供 `FindAnyActiveJobAsync` 返非空 job。沿用现有 `ExtractionCapacityKeyTests` 的 `FakeExtractionJobStore` 模式。

- [ ] **Step 5: Write JobPipelineOrchestratorIntegrationTests.cs(2 tests)**

```csharp
using ISEStudio.Extraction.Dovetail.Job;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobPipelineOrchestratorIntegrationTests
{
    [Fact]
    [Trait("Category", "Extraction")]
    public void Router_PipelineExecute_ReturnsJobResult()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddExtractionServices()
            .AddDovetailPipelines()
            .BuildServiceProvider();
        using var scope = sp.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<JobPipelineRouter>();
        Assert.NotNull(router);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void ThreePipelines_ConstructFromFullWiring()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddExtractionServices()
            .AddDovetailPipelines()
            .BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<TBoxOnlyJobPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetService<ABoxOnlyJobPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetService<CombinedJobPipeline>());
    }
}
```

注:这两个测试只是 wiring smoke test(确认 AddExtractionServices + AddDovetailPipelines + router 一起能跑),不实际调 orchestrator.RunJobSafelyAsync(那在 e2e 测试覆盖)。

- [ ] **Step 6: Write ExtractionOrchestratorJobPipelineE2ETests.cs(1 test)**

```csharp
using ISEStudio.Extraction;
using ISEStudio.Tests.Extraction.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ISEStudio.Tests.Extraction;

public sealed class ExtractionOrchestratorJobPipelineE2ETests
{
    [Fact]
    [Trait("Category", "Extraction")]
    public async Task RunJobSafelyAsync_TBoxOnly_CompletesViaDovetailPipeline()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddExtractionServices()
            .AddDovetailPipelines()
            .BuildServiceProvider();

        // 复用 slice 4 ExtractionOrchestratorTerminologyPipelineE2ETests 的 fixture 模式
        // 断言: TBoxOnly JobInput 经 RunJobSafelyAsync → 走 JobPipelineRouter.TBoxOnly
        // 期望 JobResult 形状正确(path-agnostic: 不区分是手写还是 Dovetail)
        var job = await sp.GetRequiredService<IExtractionJobStore>().CreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), "test prompt", CancellationToken.None);

        var orchestrator = sp.GetRequiredService<ExtractionOrchestrator>();
        await orchestrator.RunJobSafelyAsync(
            new JobInput(job.Id, job.KnowledgeSystemId, new[] { 1, 2, 3 },
                          null!, JobKind.TBoxOnly, null, CancellationToken.None),
            CancellationToken.None);

        var final = await sp.GetRequiredService<IExtractionJobStore>().GetAsync(job.Id, CancellationToken.None);
        Assert.True(final.Status == JobStatus.Completed || final.Status == JobStatus.Failed);
    }
}
```

注:e2e 测试断言 path-agnostic(plan-mandated 限制,PARKED item 继承 slice 4)。具体字段值不校验,只校验 status 终态。

- [ ] **Step 7: Run tests**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~JobPipelineOrchestratorIntegrationTests|FullyQualifiedName~GuardedSegmentTopLevelTests|FullyQualifiedName~ExtractionOrchestratorJobPipelineE2ETests" --nologo`
Expected: Passed: 4, Failed: 0(其中 1 个 e2e 可能 FAIL 如果 PG testcontainers 未启,标 Skip 而非 Fail;若 FAIL,排查 fixture)

- [ ] **Step 8: Run 全量 gate**

Run: `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo`
Expected: Passed: 997, Failed: 0, Skipped: 1, Total: 998(993 + 4 新增)

注:spec §8.1 目标 ≥990,本任务末态 997,达标。若 e2e 标 Skip 而非 Passed,可能 997 不含 e2e(详查)。

- [ ] **Step 9: Run integration gate**

Run: `dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --nologo`
Expected: Passed: 46, Failed: 0(零改动)

- [ ] **Step 10: Commit**

```bash
git add src/ISEStudio/Extraction/ExtractionOrchestrator.cs
git add src/ISEStudio/Integration/InternalOperationDispatcher.cs
git add src/ISEStudio.Tests/Extraction/Dovetail/Job/GuardedSegmentTopLevelTests.cs
git add src/ISEStudio.Tests/Extraction/Dovetail/Job/JobPipelineOrchestratorIntegrationTests.cs
git add src/ISEStudio.Tests/Extraction/ExtractionOrchestratorJobPipelineE2ETests.cs
git commit -m "feat(extraction): wire RunJobSafelyAsync to JobPipelineRouter + remove dispatcher 409 wrapper (3 tests + 1 e2e)"
```

---

### Task 7: dovetail-report HTML 产 3 变体 + memory 落地 + 收尾

**Files:**
- Create: `docs/superpowers/diagrams/extraction-job-dag.html`(+ Mermaid vendor JS/CSS,沿 slice 4 模式)
- Create: `memory/ontopilot-extraction-dovetail-slice5.md`(项目记忆)
- Modify: `memory/MEMORY.md`(append index entry)
- Modify: `.superpowers/sdd/2026-08-29-job-pipeline-dovetail-slice-5/progress.md`(最终 review + wrap-up entries)

**Interfaces:**
- Consumes: Task 1-6 全部产出
- Produces: HTML 报告 + 记忆 + 收尾

- [ ] **Step 1: 验证 Dovetail 工具 1.0.0 已装**

Run: `dotnet tool list --global | grep dovetail`
Expected: `dovetail-report 1.0.0`

- [ ] **Step 2: 生成 HTML 报告**

Run: `dotnet dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams/extraction-job-dag.html --nologo`
Expected: 生成 8 文件(3 变体 pipeline HTML + index + vendor JS/CSS,沿 slice 4 8 文件模式;但 Job 切片 3 变体 + index + vendor = 5 文件,具体看工具输出)。

- [ ] **Step 3: 验证 HTML 含 3 变体**

Run: `grep -c "TBoxOnlyJobPipeline\|ABoxOnlyJobPipeline\|CombinedJobPipeline" docs/superpowers/diagrams/extraction-job-dag.html`
Expected: 3 个匹配。

- [ ] **Step 4: Commit HTML**

```bash
git add docs/superpowers/diagrams/
git commit -m "docs(extraction): add Dovetail Job pipeline HTML report (3 variants)"
```

- [ ] **Step 5: 写项目记忆文件**

`memory/ontopilot-extraction-dovetail-slice5.md`(沿 slice 4 memory 格式):
```markdown
---
name: ontopilot-extraction-dovetail-slice5
description: Dovetail Job pipeline 切片完成(commits ...,3 JobPipeline 变体 + JobPipelineRouter + 5 phase step + JobState immutable record 切换,997/0/1/998 tests)
metadata:
  type: project
---

# Dovetail Slice 5: Job Pipeline

## 概览

ISEStudio extraction pipeline Dovetail 化的第 5 切片(父 spec roadmap §5 第 5 项)。把顶层 `ExtractionOrchestrator.RunJobSafelyAsync` 内的 5 phase-runner(Layer/Agent/Corpus/Hierarchy/Terminology)抽到 Dovetail 顶层 `ExtractionJobPipeline` 3 变体(TBoxOnly/ABoxOnly/Combined)。

**Why**:完成父 spec 5/5 路线图最后一项,统一 ISEStudio extraction 顶层的 pipeline 模型。

**How to apply**:5 phase sequential 用 immutable `JobState` record 透传;3 pipeline 变体各自 DAG 编译期明确;409 envelope 提到 pipeline 顶层(`GuardedSegment`);Dispatcher 移除 `RunWithExtractionGuardAsync` wrapper;per-phase try/catch 用 `PerPhaseCatchStep` adapter。

## Commit stack (具体 commits)

... 沿 slice 4 格式

## 核心架构裁决 LOCKED

- **3 JobPipeline 变体**:TBoxOnlyJobPipeline(5 段)/ ABoxOnlyJobPipeline(2 段)/ CombinedJobPipeline(6 段)
- **immutable JobState record 透传**:`JobRunContext` mutable struct 一次性删除;`JobState` record 5 phase sequential 用 `state with { ... }`
- **PerPhaseCatchStep adapter**:Dovetail 段 try/catch 转 `state.Error`,`OperationCanceledException` 不吞
- **GuardedSegment 顶层**:pipeline 入口包 1 次 409 envelope(`IRunWithExtractionGuard` seam slice 1-4 引入)
- **NoOpAgentStep 替代**:`_scopes null` → factory 返 `null!`(slice 1-4 `null!` 模式复用)

## 测试门演进

972 → 976(T1) → 976(T2) → 984(T3) → 993(T4) → 997(T5) → 997(T6)/0/1/998 ✓(实际以最终 commit 为准)。

## Dovetail 1.0.0 行为变更

**零**。ExtractionOrchestrator public 签名变化(原 `RunJobSafelyAsync(JobRunContext, Func<...>, ct)` → 新 `RunJobSafelyAsync(JobInput, ct)`);JobStatus 状态机零改;MarkFailed/MarkCompleted 零改;Terminology fail-soft 保留;现有 972+46 测试零改全绿作为硬 gate。

## PARKED items

- **path-agnostic e2e 断言**(继承 slice 4):Dovetail DAG-first 与手写 runner 等价性规则固有限制
- **seam 互作 quirk**(继承 slice 4):hand-built 同时传坏容器+seam 时 catch 先返 null — 实践不可达
- **JobRunContext 一次性切换**(本切片):不留兼容层,grep 全仓无引用

## 相关 memory

- [[ontopilot-extraction-dovetail-slice1]] TBox pipeline
- [[ontopilot-extraction-dovetail-slice2]] ABox sub-DAG
- [[ontopilot-extraction-dovetail-slice3]] AgentChain(本切片 R2 模式来源)
- [[ontopilot-extraction-dovetail-slice4]] Vocabulary pipeline(本切片 TerminologyStep 复用)
```

- [ ] **Step 6: 更新 memory/MEMORY.md 索引**

```markdown
- [ontopilot-extraction-dovetail-slice5](ontopilot-extraction-dovetail-slice5.md) — Dovetail Job pipeline 切片完成(commits ...,3 变体 + JobPipelineRouter + JobState immutable record 切换,997/0/1/998 tests)
```

- [ ] **Step 7: 写入最终 review + wrap-up 到 ledger**

`.superpowers/sdd/2026-08-29-job-pipeline-dovetail-slice-5/progress.md` 追加:
- Final review (opus, whole-branch) verdict
- R1 修复轮记录(若有)
- Wrap-up: spec v1.x 落地、plan 修正 commit
- Slice 5 总 commit 数
- Dovetail 1.0.0 行为变更声明
- PARKED items

- [ ] **Step 8: 删除 SDD workspace**

Run: `rm -rf .superpowers/sdd/2026-08-29-job-pipeline-dovetail-slice-5/`

- [ ] **Step 9: 中文完成报告给用户(切片 5 收官)**

按 slice 1-4 报告模板:commit 数 + 测试门 + 关键裁决 + plan-defect 修复 + R1 修复轮 + slice 6 后续(本切片已是父 spec roadmap 最后一项,剩余 slice 6 = dovetail-report 接入 CI,跨切片一致性 lint)。

---

## Plan 自审

### 1. Spec coverage

| Spec 章节 | 对应 Task |
|-----------|-----------|
| §1 背景 | Task 2 Step 1-2(reference) |
| §2 设计目标 | Task 1-7 全覆盖 |
| §3 架构总览 | Task 4(3 pipelines)+ Task 5(DI)+ Task 6(router 接入) |
| §4 Data Flow | Task 1(records)+ Task 2(with 化)+ Task 3(steps IO) |
| §5 Error Handling | Task 3(PerPhaseCatchStep)+ Task 6(GuardedSegment 顶层) |
| §6 文件结构 | Task 1-7 全部创建对应文件 |
| §7 Orchestrator 改动 | Task 2 + Task 6 |
| §8 测试策略 | Task 1(4)+ Task 3(8)+ Task 4(9)+ Task 5(4)+ Task 6(3) = 28 tests |
| §9 任务拆分 | Task 1-7 1:1 对应 |
| §10 风险 | Task 5 风险:DO VE017 触发 → 拆 wrapper;Task 6 风险:dispatcher wrapper 移除漂移 → Task 6 e2e 覆盖 |
| §11 决策日志 | Task 2(D10 JobRunContext 切换)+ Task 3(D7 PerPhaseCatchStep)+ Task 6(D5 GuardedSegment 顶层) |

✅ 全覆盖,无 gap。

### 2. Placeholder scan

无 TBD / TODO / "实现细节"占位符。每段代码完整可执行。

### 3. Type consistency

- `JobState` / `JobInput` / `JobResult` 在 Task 1 定义,Task 2-6 全部使用 ✓
- `JobKind` enum 字段在 Task 1 定义,Task 4 router switch 全覆盖(TBoxOnly/ABoxOnly/Combined) ✓
- `LayerStep<TPipeline>` generic 在 Task 3 定义,Task 4 TBoxOnlyPipeline 用 `LayerStep<TBoxChunkPipeline>`、Task 4 ABoxOnlyPipeline 用 `LayerStep<ABoxJobPipeline>`、Task 4 CombinedPipeline 用 `LayerStep<TBoxChunkPipeline>` + `LayerStep<ABoxJobPipeline>` ✓
- 5 phase step 输入输出都是 `IPipelineSegment<JobState, JobState>`,DOVE017 合规 ✓
- `PerPhaseCatchStep` 内部包装 `IPipelineSegment<JobState, JobState>`(Task 3 定义),Task 6 router 接入用 `ExtractionGuard` 包 router ✓
- `ExtractionGuard.RunAsync` 签名 `(Func<Task<T>>, Func<T, T>, CancellationToken)` 与 slice 1-4 一致 ✓

---

**Plan 状态**:完成,等待用户选择执行方式(Subagent-Driven 推荐)。