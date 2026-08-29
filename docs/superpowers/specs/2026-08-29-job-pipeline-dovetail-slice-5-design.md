# Dovetail 顶层 Job 流水线设计(Slice 5)

**版本**:v1.1(2026-08-30 实施后修正,DOVE017 wrapper records + canonical chain + 4-field JobState 扩展 + DI fix)
**日期**:2026-08-29
**作者**:Claude / ISEStudio
**状态**:实施完成 / v1.1 修正已合入
**范围**:`ExtractionOrchestrator.RunJobSafelyAsync` → 顶层 `ExtractionJobPipeline` 薄壳(3 变体)+ 5 phase-runner-as-segment + 409 envelope 提到 pipeline 顶层 + `JobRunContext`(mutable struct)→ `JobState`(immutable record)。**A 方案纯薄壳**,沿父 spec §5 roadmap 第 5 项。

**父 spec**:`docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`(v1.0)
**前置切片**:Slice 1(TBox)/ Slice 2(ABox)/ Slice 3(AgentChain)/ Slice 4(Vocabulary)— 4 个 sub-DAG 已 Dovetail 化

---

## 1. 背景与现状

ISEStudio 顶层抽取入口(`ExtractionOrchestrator.RunJobSafelyAsync`,`ExtractionOrchestrator.cs:352-372`)是 god-method 边界,**目前仍是手写编排**:

```
RunJobSafelyAsync
 ├─► MarkRunningAsync
 └─► runner(context)  ← 3 种 kind delegate 之一:
      ├─ TBoxOnlyRunnerAsync:   Layer(TBox) → Corpus → Hierarchy → Agent → Terminology
      ├─ ABoxOnlyRunnerAsync:   Layer(ABox) → Terminology
      └─ CombinedRunnerAsync:   Layer(TBox) → Agent → Corpus → Hierarchy → Layer(ABox) → Terminology
 └─► MarkCompletedAsync (only if runner返回 true)
      OR MarkFailedAsync (SafeMarkFailedAsync,现状保留)
```

**5 phase-runner 现状**(基于 explore 报告):

| # | Method | 文件:行 | 返回 | 顺序 |
|---|--------|---------|------|------|
| 1 | `RunLayerAsync(TBox/ABox)` | `ExtractionOrchestrator.cs:865-933` | `Task<bool>` | TBoxOnly #1 / Combined #1+#5 / ABoxOnly #1 |
| 2 | `RunAgentChainAsync` | `ExtractionOrchestrator.cs:733-847` | `Task` | TBoxOnly #4 / Combined #2 |
| 3 | `RunCorpusRecoveryAsync` | `ExtractionOrchestrator.cs:1163-1196` | `Task` | TBoxOnly #2 / Combined #3 |
| 4 | `RunHierarchyRecoveryAsync` | `ExtractionOrchestrator.cs:1230-1273` | `Task` | TBoxOnly #3 / Combined #4 |
| 5 | `RunTerminologyAsync` | `ExtractionOrchestrator.cs:550-603` | `Task` | 全部 #last |

**5 phase-runner 的 Dovetail 化现状**:
- ✅ Slice 1 `TBoxJobPipeline`(Layer 内部 `RunLayerAsync` 调 `ExtractAndVerifyAsync` 已走 Dovetail)
- ✅ Slice 2 `ABoxJobPipeline`(`RunABoxLayerAsync` 已走 Dovetail)
- ✅ Slice 3 `AgentChainPipeline`(`RunAgentChainAsync` 已走 Dovetail)
- ✅ Slice 4 `TerminologyPipeline`(`RunTerminologyAsync` 已走 Dovetail)
- ❌ `RunCorpusRecoveryAsync` / `RunHierarchyRecoveryAsync` **仍手写**,本 slice 新增薄壳 step

**JobRunContext**(`ExtractionOrchestrator.cs:138-152`):mutable struct,5 phase-runner 都修改其字段(`ProcessedChunks`、`PerChunkRejections`、`Vocabulary` 等)。Dovetail record 是 immutable,需切到 `JobState` immutable record。

**409 envelope 现状**:HTTP 入口(`extraction.run[_combined|_instances]` 3 arm,`InternalOperationDispatcher.cs:132-140`)由 `RunWithExtractionGuardAsync` 包 1 次;dispatcher 内 → `RejectIfExtractionActiveAsync` → `FindAnyActiveJobAsync`(cross-KS lock,Stage 2/3 将切到 KS-scoped,父 spec roadmap 后续切片)。

**关键不变量**:
- `ExtractionJobStore` / `JobStatus` 状态机 / `MarkFailedAsync` / `MarkCompletedAsync` **零改动**
- Terminology **fail-soft 契约**保留(phase 5 不可能 fail job,`termCapture.MarkError()` 仍由 RunTerminologyAsync 内部 try/catch 处理)
- 现有 972 unit + 46 integration 全绿作为硬 gate
- DOVE001-020 编译期诊断全过

---

## 2. 设计目标

| 目标 | Dovetail 给的能力 |
|---|---|
| **顶层编排类型化** | 3 个 `ExtractionJobPipeline` 变体作为 `IPipeline<JobInput, JobResult>`,编译期类型匹配自动派生 DAG;5 phase-runner-as-segment + OptionalSegment + GuardedSegment 全装在顶层 |
| **JobState immutable** | `JobState` record 在 5 phase sequential 透传,每段 `state with { ... }` 表达式返新 record;与 Dovetail 函数式 record 一致 |
| **3 runner kind 显式化** | 3 个独立 JobPipeline(TBoxOnly/ABoxOnly/Combined)各自 1 个 DAG,编译期各自明确;Mermaid 各自出图 |
| **Optional skip 静态决定** | DI 层根据 options/nullable state 选 NoOp vs 真段(父 spec D4 沿用),不引入 runtime flags |
| **409 envelope 顶层化** | `GuardedSegment` 在 pipeline 顶层包 1 次;dispatcher 移除 `RunWithExtractionGuardAsync` wrapper,改直接调 application service(父 spec D6) |
| **零行为变化** | 现有测试零改全绿;Terminology fail-soft 保留;`JobStatus` 状态机保留;error envelope shape 保留 |

### 非目标(本 slice 不做)

- **per-tenant / per-KS 运行时切换流水线拓扑** — Dovetail 编译期类型匹配,做不到 runtime 改图(父 spec 非目标继承)
- **partial-failure recovery 升级** — A 方案纯薄壳,error 模型零改(per-phase `Error` 字段透传给 JobResult,但状态机仍是 binary pass/fail;`JobStatus.Partial` 不引入)
- **chunk-level fan-out 并发** — 5 phase sequential 保持,不改并发拓扑(父 spec D2 继承)
- **KS-scoped lock** — `FindAnyActiveJobAsync` 不切到 KS-scoped(Stage 2/3 后续切片)
- **Cancellation token 传播到 background task** — 现状 `CancellationToken.None` 透传保留(后续切片)
- **删旧 `JobRunContext`** — 一次性切换;本 slice 不留兼容层

---

## 3. 架构总览

```
HTTP POST /api/knowledge/{id}/extract*
  └─► ExtractionController → InternalOperationDispatcher "extraction.run[_combined|_instances]"
       └─► IExtractionApplicationService.RunAsync(无 RunWithExtractionGuardAsync wrapper,slice 5 移除)
            └─► ExtractionOrchestrator.StartAsync (sync: read doc + CreateAsync + Task.Run → 返 job row 立刻返)
                     └─► [Task.Run background] ExtractionOrchestrator.RunJobSafelyAsync  ← SLICE 5 改动点
                          ├─► MarkRunningAsync (现状保留)
                          └─► JobPipelineRouter.Resolve(kind).ExecuteAsync(JobState.From(context), ct)
                                │ 顶层 GuardedSegment (1 处,409 envelope)
                                └─► ExtractionJobPipeline (3 变体之一):
                                      ├─► TBoxOnlyJobPipeline:  LayerStep(TBox) → CorpusStep → HierarchyStep → AgentStep → TerminologyStep
                                      ├─► ABoxOnlyJobPipeline:  LayerStep(ABox) → TerminologyStep
                                      └─► CombinedJobPipeline:  LayerStep(TBox) → AgentStep → CorpusStep → HierarchyStep → LayerStep(ABox) → TerminologyStep
                                       ↑ 每个 phase step 外层包 OptionalSegment(skip 条件 1:1 保留现状)
                          └─► MarkCompletedAsync (only if JobResult.Succeeded)
                               OR SafeMarkFailedAsync (现状保留)
```

### 3.1 3 个 JobPipeline 变体

| Pipeline | 段数 | 顺序 | Shape |
|----------|------|------|-------|
| `TBoxOnlyJobPipeline` | 6 | `JobState → TBoxLayerCarry → AgentCarry → CorpusCarry → HierarchyCarry → ABoxLayerCarry → TerminologyCarry` | `IPipeline<JobState, TerminologyCarry>` |
| `ABoxOnlyJobPipeline` | 6 | 同上 canonical chain,前 4 段 `NoOpSegment<,,>` 替换 | `IPipeline<JobState, TerminologyCarry>` |
| `CombinedJobPipeline` | 6 | canonical chain 全部启用 | `IPipeline<JobState, TerminologyCarry>` |

**v1.1 修正(2026-08-30 实施)**:
- 实际 pipeline shape = `IPipeline<JobState, TerminologyCarry>`,**不是** spec v1.0 草稿写错的 `IPipeline<JobInput, JobResult>`。3 变体共享 canonical chain shape,JobInput→JobState 与 JobState→JobResult 投影由 `JobPipelineRouter` 完成(避免 `JobInput`/`JobResult` 污染 Dovetail generic arg 域;Dovetail static-typed DAG 只接收 JobState,JobInput/JobResult 是 orchestrator-boundary DTO)。
- canonical chain order = `CombinedRunnerAsync` 真实顺序(`ExtractionOrchestrator.cs:580-598`):`Layer(TBox) → Agent → Corpus → Hierarchy → Layer(ABox) → Terminology`(R7 LOCKED)。
- canonical chain + NoOp 替换 + `NoOpAgentStep` 静态工厂,**不**需要 6 个 step 变体 7 个 task 实现(R8 LOCKED;TBoxOnlyPipeline 跳过 Agent + ABoxLayer;ABoxOnlyPipeline 跳过前 4 段)。

**DOVE002 合规**:3 个 pipeline 各自 1 个 `IPipeline<JobState, TerminologyCarry>`,合法。

**DOVE006 合规**:每个 pipeline 内部 6 段 sequential(前 2 段或前 4 段 NoOp 替换为 identity),每段输入 = pipeline input 或 prior output,合法。

**DOVE017 触发**:6 段 sequential 形状虽 fold 不同字段,**但** assembly-wide interface type-argument uniqueness 强制 2 段同 `(JobState, JobState) → JobState` 编译失败,因此需要 `JobCarries.cs` 6 个 wrapper records(`TBoxLayerCarry` / `AgentCarry` / `CorpusCarry` / `HierarchyCarry` / `ABoxLayerCarry` / `TerminologyCarry`),每个 1-line `sealed record Xxx(JobState State)`(详见 §5.1)。

---

## 4. Data Flow(immutable JobState record 透传)

### 4.1 JobInput record(pipeline 入口)

```csharp
public enum JobKind { TBoxOnly, ABoxOnly, Combined }

public sealed record JobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<int> ChunkIds,
    IChatClient Chat,
    JobKind Kind,
    IReadOnlyList<string>? InitialVocabulary, // HierarchyStep 初始 vocab(Combined 模式从 TBox 末态取)
    CancellationToken CancellationToken);
```

### 4.2 JobState record(5 phase sequential 透传)

```csharp
public sealed record JobState
{
    public Guid JobId { get; init; }
    public Guid KnowledgeSystemId { get; init; }
    public IReadOnlyList<int> ChunkIds { get; init; }
    public IChatClient Chat { get; init; }
    public JobKind Kind { get; init; }
    public IReadOnlyList<string>? InitialVocabulary { get; init; }

    // v1.1 实施后扩展(R11 LOCKED):Dovetail static-typed DAG 无 runtime closure injection,
    // per-job closure arguments(KsContext + Request + Chunks + PerChunk)必须在 state 里
    public KnowledgeSystemContext KsContext { get; init; } = null!;            // Task 4 R11
    public ExtractionJobRequest Request { get; init; } = null!;                 // Task 4 R11
    public IReadOnlyList<ChunkRecord> Chunks { get; init; } = Array.Empty<ChunkRecord>(); // Task 4 R11
    public IReadOnlyDictionary<int, PerChunkState> PerChunk { get; init; } = ImmutableDictionary<int, PerChunkState>.Empty; // Task 4 R11

    // Phase outputs (mutated by steps via 'with' expressions)
    public IReadOnlyList<ChunkResult> TBoxChunkResults { get; init; } = Array.Empty<ChunkResult>();
    public IReadOnlyList<ChunkResult> ABoxChunkResults { get; init; } = Array.Empty<ChunkResult>();
    public IReadOnlyList<int> PerChunkRejections { get; init; } = Array.Empty<int>(); // RunLayerAsync 已写入
    public IReadOnlyList<string> FinalClassVocabulary { get; init; } = Array.Empty<string>(); // HierarchyStep 产出
    public JobTerminology? Terminology { get; init; } // TerminologyStep 产出
    public long ProcessedChunks { get; init; }

    // Per-phase error propagation
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

**JobState 修改契约**:每个 phase step 内部对 `JobState` 的"读取 + 修改"通过 `state with { ... }` 表达式返回新 record。orchestrator 现有 5 phase-runner 方法签名内部修 `JobRunContext`(mutable struct)→ 改为读/返 `JobState`(immutable record)。这是 Slice 5 **唯一**的中量业务逻辑改动。

**v1.1 修正字段集合**:
- spec v1.0 草稿:13 字段(JobId/KnowledgeSystemId/ChunkIds/Chat/Kind/InitialVocabulary + 6 phase outputs + Error/CancellationToken)
- 实际实施(v1.1):**17 字段**(13 + R11 扩展 4 字段 `KsContext`/`Request`/`Chunks`/`PerChunk`)
- R11 rationale:Dovetail partial ctor 实例化 step 时只有 state 可以传;runtime closure(per-job JobRequest + KnowledgeSystemContext + chunk records + per-chunk state)无法用 ctor 注入 → state 必须承载这些字段
- 字段类型:`KnowledgeSystemContext` 是 ISEStudio 既有 value type(分层服务上下文的基线),`ExtractionJobRequest` 是 dispatcher DTO,`ChunkRecord`/`PerChunkState` 是 chunk 维度 tracking record

### 4.3 5 phase IO record

| Step | 输入 | 输出 |
|------|------|------|
| `LayerStep<TBoxPipeline>` | `JobState` | `state with { TBoxChunkResults = …, ProcessedChunks = …, PerChunkRejections = … }` |
| `LayerStep<ABoxJobPipeline>` | `JobState` | `state with { ABoxChunkResults = …, ProcessedChunks = … }` |
| `AgentStep` | `JobState` | `state`(零字段修改,UpdateProgressAsync 内部已写) |
| `CorpusStep` | `JobState` | `state with { TBoxChunkResults = … }`(增量追加 CorpusRecovered classes) |
| `HierarchyStep` | `JobState` | `state with { TBoxChunkResults = …, FinalClassVocabulary = … }` |
| `TerminologyStep` | `JobState` | `state with { Terminology = …, ProcessedChunks = … }` |

**注**:LayerStep 是 generic(`LayerStep<TPipeline>`),DI 注册时分别实例化 `LayerStep<TBoxChunkPipeline>` 和 `LayerStep<ABoxJobPipeline>`。两个 generic 实例接口形状不同(`TBoxChunkPipeline` vs `ABoxJobPipeline`),DOVE017 合规。

### 4.4 JobResult record(pipeline 出口)

```csharp
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
```

### 4.5 支持 record

```csharp
// v1.1 修正(Task 1 Ruling):spec v1.0 草稿引用不存在的 `TBoxClass` / `TBoxProperty` / `TBoxAxiom` types
// (这些是 P1-5 系列后续切片才落地的 strongly-typed domain types,本切片尚未存在)
// 实施后 canonical shape = `IReadOnlyList<object>` 占位,typed consumer 可在后续切片引入
public sealed record ChunkResult(int ChunkId, IReadOnlyList<object> Added, IReadOnlyList<object> PropertiesAdded, IReadOnlyList<object> AxiomsAdded);

public sealed record JobTerminology(long TermsAdded, long TermsMapped, long ProposalsQueued, string? Error);
```

---

## 5. Error Handling(三层)

| 层 | 来源 | 行为 |
|---|------|------|
| **per-phase inner try/catch** | 现有 5 phase-runner 内部的 fail-soft(`CorpusStep` / `HierarchyStep` 已 swallow;`TerminologyStep` 已 MarkError-on-capture) | **保留原状**,不重复包装(Slice 5 = 薄壳,不动 inner fail-soft) |
| **per-phase outer catch** | Dovetail 段异常 → Step 内部 try/catch 转 `state with { Error = ex.Message, ShouldSkipRemaining = true }`(新引入,等效 Slice 1-4 的 try/catch 模式) | per-phase 异常 → `Error` 字段透传;后续 phase 看到 `state.ShouldSkipRemaining == true` 走 no-op branch,等价 skip |
| **top-level GuardedSegment** | pipeline 顶层包 1 次 409 envelope(`ExtractionGuard.RunAsync`,已有 seam `IRunWithExtractionGuard` slice 1-4 引入) | job 并发抢占 → 返 conflict envelope;其他异常 → 重抛给 `RunJobSafelyAsync` outer catch → `SafeMarkFailedAsync`(现状保留) |

**OperationCanceledException**:**不**被 per-phase outer catch 吞(Dovetail README §Exception Handling 明确禁止);`CancellationToken` 由 pipeline input 传入,JobState 携带,5 phase step 自行决定是否 honor(现状 = 全 `CancellationToken.None` 透传,Slice 5 不改)。

### 5.1 Optional skip 条件(DI 层静态决定,**沿用父 spec D4**)

| Step | Optional 条件 | 包装策略 |
|------|---------------|----------|
| `LayerStep(TBox)` | 永远启用(无可 null) | 无包装 |
| `LayerStep(ABox)` | 永远启用 | 无包装 |
| `AgentStep` | `_scopes is null`(生产由 host 注册,hand-built 测试 null) | DI 层注册 `NoOpAgentStep` 替代真段(同 Slice 1-4 `null!` 模式) |
| `CorpusStep` | `_corpus is null \|\| _verify is null \|\| PerChunkRejections.Count == 0` | 段内 runtime 短路条件(1:1 保留现状;DI 层不区分 NoOp,因 `corpus`/`verify` 生产总是非 null,测试环境总是非 null) |
| `HierarchyStep` | `_hierarchy is null \|\| _verify is null \|\| PerChunkRejections.Count == 0` | 同 CorpusStep |
| `TerminologyStep` | 永远启用(契约要求) | 无包装(Terminology fail-soft 保留) |

**`NoOpAgentStep`**:`IPipelineSegment<JobState, JobState>`,返 input 零修改(`state`,无字段 fold)。DI 注册 `AddScoped<AgentStep>(sp => sp.GetService<IServiceScopeFactory>() is null ? new NoOpAgentStep() : new AgentStep(...))` — last-registration-wins + slice 1-4 `null!` 模式复用。

**`PerPhaseCatchStep<TIn, TOut>`**(Dovetail adapter):包 try/catch,失败返 fallback。封装为 `IPipelineSegment<TIn, TOut>` 装饰器。`OptionalSegment` 父 spec D4 已定义;`PerPhaseCatchStep` 是 Slice 5 新引入(语义对齐 Slice 1-4 orchestrator helper try/catch 模式)。

### 5.2 DOVE017 wrapper records ruling(2026-08-30 实施裁定)

**spec v1.0 错误假设**:5 phase sequential 形状天然唯一(每段 fold 不同字段子集到 `JobState`),无需 wrapper record。**实际** §3.1 实施后,6 段 sequential 中 `TBoxLayerStep` 与 `ABoxLayerStep` 共享 `(JobState, JobState) → JobState` 接口形状,DOVE017 触发:**2 段同 shape → CS 编译错误**,Dovetail source generator 不容忍 assembly-wide interface type-argument 重复。

**v1.1 修正:6 个 wrapper records 在 `JobCarries.cs`**(Task 3 提交 `652d795`,切片 Slice 4 v1.2 `TerminologyCarries.cs` precedent 复用):

```csharp
// src/ISEStudio/Extraction/Dovetail/Job/JobCarries.cs(每个 1-line)
public sealed record TBoxLayerCarry(JobState State);
public sealed record AgentCarry(JobState State);
public sealed record CorpusCarry(JobState State);
public sealed record HierarchyCarry(JobState State);
public sealed record ABoxLayerCarry(JobState State);
public sealed record TerminologyCarry(JobState State);
```

**variant 裁决**:每段自己的 wrapper record,**不**复用 `SliceCarry(State)` 共享类型(语义不清晰,后续读代码的人不知道哪个 carry 对应哪段);保持命名 stack 与 chain order 一致(`TBoxLayerCarry → AgentCarry → CorpusCarry → HierarchyCarry → ABoxLayerCarry → TerminologyCarry`,canonical chain)。

**生产代码 fold**:step 内 `state with { ... }` 直接修改 `TerminologyCarry.State`,不显式 `.Carry.State` 解包(Dovetail partial ctor `IPipelineSegment<JobState, XxxCarry, NextCarry>` 自动透传)。**测试代码**构造 stub state 时显式 `new XxxCarry(new JobState { ... })`。

**Task 4 Ruling(影响**:Task 4 引入 3 个 `ChainAdapter<TIn, T1, TOut>` + `NoOpSegment3`(DOVE008 architectural compromise,2-arity step → 3-arity pipeline segment 包装层);Task 5 DI fix(R15)显式 register `typeof(NoOpSegment<,,>)` + `typeof(ChainAdapter<,,>)` MS.DI open-generic self-registration。

---

## 6. 文件结构

```
src/ISEStudio/Extraction/Dovetail/Job/
├── JobInput.cs                # JobInput record + JobKind enum
├── JobState.cs                # JobState record (immutable, 含 ShouldSkipRemaining)
├── JobResult.cs               # JobResult record + JobTerminology record + ChunkResult record
├── JobPipelineRouter.cs       # 按 JobKind 选 3 pipeline 之一
├── Pipelines/
│   ├── TBoxOnlyJobPipeline.cs       # partial, [Segment] × 5
│   ├── ABoxOnlyJobPipeline.cs       # partial, [Segment] × 2
│   └── CombinedJobPipeline.cs       # partial, [Segment] × 6
├── Steps/
│   ├── LayerStep.cs                 # generic LayerStep<TPipeline>, 2 实例化: TBox + ABox
│   ├── AgentStep.cs                 # 调 AgentChainPipeline(slice 3 透传)
│   ├── CorpusStep.cs                # 新: 包 CorpusRecoveryService.RecoverAsync
│   ├── HierarchyStep.cs             # 新: 包 HierarchyRecoveryService.RecoverAsync
│   ├── TerminologyStep.cs           # 调 TerminologyPipeline(slice 4 透传) + P3-1 agent folding
│   ├── NoOpAgentStep.cs             # _scopes null 替代
│   └── PerPhaseCatchStep.cs         # Dovetail adapter: 包 try/catch 转 JobState.Error
└── DovetailJobAdapters.cs           # IServiceCollection 扩展(可选,若需)

src/ISEStudio/Extraction/
├── ExtractionOrchestrator.cs        # 改: JobRunContext 弃用 → JobState; RunJobSafelyAsync 走 pipeline
└── Dovetail/DovetailPipelineRegistrations.cs  # append §9 Job block

src/ISEStudio/Integration/
└── InternalOperationDispatcher.cs   # 改: 移除 RunWithExtractionGuardAsync wrapper 在 3 个 extraction arm

docs/superpowers/diagrams/
└── extraction-job-dag.html          # dovetail-report 产物(3 变体)
```

### 6.1 DovetailPipelineRegistrations §9 注册块

```csharp
// 9. Job slice 5 (per spec §5 + §6)。
// SCOPED: orchestrator resolves JobPipeline from per-job scope(Slice 3 R2 lifecycle)。
// v1.0 草稿错把 NoOpAgentStep 入 DI(实际是 static factory);v1.1 仅 6 step classes 入 DI
services.AddScoped<TBoxLayerStep>();
services.AddScoped<ABoxLayerStep>();
services.AddScoped<CorpusStep>();
services.AddScoped<HierarchyStep>();
services.AddScoped<TerminologyStep>();
services.AddScoped<AgentStep>();  // NoOp 替换在构造函数内部做(sp.GetService<IServiceScopeFactory>() is null → NoOpAgentStep)

services.AddScoped<TBoxOnlyJobPipeline>();
services.AddScoped<ABoxOnlyJobPipeline>();
services.AddScoped<CombinedJobPipeline>();
services.AddScoped<JobPipelineRouter>();

// v1.1 实施(Task 6 R15 DI fix):Dovetail source generator 只 register 2-arity `NoOpSegment<,>`
// open generic,**没有**为 3-arity `NoOpSegment<,,>` 和 `ChainAdapter<,,>`(Task 4 引入的 DOVE008
// architectural compromise 助手)生成 DI registration。Pipeline partial ctor 内部 `ChainAdapter<TIn, T1, TOut>`
// + `NoOpSegment<TIn, T1, TOut>` 实例化需要 MS.DI 显式 open-generic self-registration。
// MS.DI supports open-generic self-registration(用 `typeof()` 显式)→ pipeline partial ctor 内部激活
// generic types 时能找到 ctor。
// 12 总注册:10 scoped + 2 open-generic self-registration(self-register 通用 helpers for DOVE008 助手)
services.AddScoped(typeof(NoOpSegment<,,>));
services.AddScoped(typeof(ChainAdapter<,,>));
```

---

## 7. Orchestrator 改动(最小化)

### 7.1 JobRunContext 弃用 → JobState 一次性切换

**改前**(`ExtractionOrchestrator.cs:138-152`):
```csharp
public struct JobRunContext  // mutable
{
    public Guid JobId;
    public Guid KnowledgeSystemId;
    public IReadOnlyList<int> ChunkIds;
    // ... 5 phase-runner 都修改 ProcessedChunks / PerChunkRejections / Vocabulary
}
```

**改后**:
- `JobRunContext` struct **删除**(全仓 grep 无引用后)
- `JobState` immutable record 在 `Dovetail/Job/JobState.cs` 新建(本 spec §4.2)
- 5 phase-runner 方法签名 `RunLayerAsync(JobState state, CancellationToken ct) → JobState` 等

### 7.2 RunJobSafelyAsync 替换

**改前**(`ExtractionOrchestrator.cs:352-372`):
```csharp
private async Task RunJobSafelyAsync(JobRunContext context, Func<JobRunContext, Task<bool>> runner, CancellationToken ct)
{
    try
    {
        await _jobs.MarkRunningAsync(context.JobId, CancellationToken.None);
        var succeeded = await runner(context);
        if (!succeeded) return;
        await _jobs.MarkCompletedAsync(context.JobId, CancellationToken.None);
    }
    catch (OperationCanceledException) { await SafeMarkFailedAsync(context.JobId, "Cancelled."); }
    catch (Exception ex) { await SafeMarkFailedAsync(context.JobId, ex.Message); }
}
```

**改后**:
```csharp
private async Task RunJobSafelyAsync(JobInput input, CancellationToken ct)
{
    try
    {
        await _jobs.MarkRunningAsync(input.JobId, CancellationToken.None);

        // Slice 5: 替换 runner(context) 为 JobPipelineRouter + Dovetail 顶层 pipeline
        var router = _scopes is null
            ? new JobPipelineRouter(/* hand-built test path: use NoOp router */)
            : _scopes.CreateScope().ServiceProvider.GetRequiredService<JobPipelineRouter>();
        var jobResult = await router.ExecuteAsync(input, ct);

        if (!jobResult.Succeeded) return;
        await _jobs.MarkCompletedAsync(input.JobId, CancellationToken.None);
    }
    catch (OperationCanceledException) { await SafeMarkFailedAsync(input.JobId, "Cancelled."); }
    catch (Exception ex) { await SafeMarkFailedAsync(input.JobId, ex.Message); }
}
```

### 7.3 3 个 top-level runner delegate 弃用

**改前**(`ExtractionOrchestrator.cs:400-532`):
```csharp
private Task<bool> TBoxOnlyRunnerAsync(JobRunContext context) { ... }
private Task<bool> ABoxOnlyRunnerAsync(JobRunContext context) { ... }
private Task<bool> CombinedRunnerAsync(JobRunContext context) { ... }
```

**改后**:
- 3 个 runner delegate **删除**
- `JobPipelineRouter.ExecuteAsync(JobInput input, ct)` 按 `input.Kind` 分发到 3 pipeline 之一
- 5 phase-runner(`RunLayerAsync` / `RunAgentChainAsync` / `RunCorpusRecoveryAsync` / `RunHierarchyRecoveryAsync` / `RunTerminologyAsync`)**保留**,方法签名改为 `JobState → JobState`

### 7.4 Dispatcher 改动

**改前**(`InternalOperationDispatcher.cs:132-140`):
```csharp
"extraction.run"           => RunWithExtractionGuardAsync(request, ct, () => InvokeExtractionRunAsync(request, "extraction.run", ct))
"extraction.run_combined"  => RunWithExtractionGuardAsync(request, ct, () => InvokeExtractionRunAsync(request, "extraction.run_combined", ct))
"extraction.run_instances" => RunWithExtractionGuardAsync(request, ct, () => InvokeExtractionRunAsync(request, "extraction.run_instances", ct))
```

**改后**:
```csharp
"extraction.run"           => InvokeExtractionRunAsync(request, "extraction.run", ct)
"extraction.run_combined"  => InvokeExtractionRunAsync(request, "extraction.run_combined", ct)
"extraction.run_instances" => InvokeExtractionRunAsync(request, "extraction.run_instances", ct)
```

`RunWithExtractionGuardAsync` 在 dispatcher 内 **删除**(`RejectIfExtractionActiveAsync` 保留,被 `ExtractionGuard.RunAsync` 在 pipeline 顶层 `GuardedSegment` 调用)。

`FastApiErrorMiddleware` 零改(409 envelope shape 不变)。

---

## 8. 测试策略

### 8.1 测试门基线 + 目标

| 维度 | 改前基线 | 改后目标 |
|------|----------|----------|
| Unit tests | 972 / 0 / 1 / 973 | ≥ 990 / 0 / 1 / ≥ 991 |
| Integration tests | 46 / 0 / 46 | 46 / 0 / 46 |
| Build warnings | 0 | 0(grep over touched files) |

新增 tests 数:~18(Slice 4 持平)

### 8.2 新增测试

| 测试类 | 数量 | 验证 |
|--------|------|------|
| `JobStateMutationTests` | 4 | JobState `with` 表达式语义、immutability、`ShouldSkipRemaining` 计算、`JobState.From(JobInput)` 投影 |
| `JobPipelineSchemaTests` | 3 | 3 个 JobPipeline 的 Mermaid doc comment + segment 注册齐全(`GetServices<IPipelineSegment<...>>()` 计数 = 5/2/6) |
| `JobPipelineExecutionTests.HappyPath` | 3 | 每 pipeline 跑一遍 stub JobState → JobResult.FromJobState 字段透传 |
| `JobPipelineExecutionTests.PerPhaseFailSoft` | 3 | 注入一个抛异常的 phase step mock → pipeline 不抛,该 phase 后续 skip,JobResult.Succeeded=false + Error msg 透传 |
| `JobPipelineExecutionTests.OptionalSkip` | 2 | (a) NoOpAgentStep 替代 AgentStep(JobState 透传无修改); (b) PerChunkRejections.Count=0 → Corpus/Hierarchy no-op |
| `JobPipelineRouterTests.KindDispatch` | 3 | JobKind.TBoxOnly/ABoxOnly/Combined 各自分发到对应 pipeline(route 输出类型正确) |
| `GuardedSegmentTopLevelTests.ConflictEnvelope` | 1 | pipeline 顶层 409 envelope:并发抢占 → JobResult 含 conflict envelope 形状;不抢占 → 正常 JobResult |
| `ExtractionOrchestratorJobPipelineE2ETests` | 1 | orchestrator.RunJobSafelyAsync 端到端(DAG-first,断言 path-agnostic — Slice 4 同款 PARKED) |

### 8.3 回归 gate

- 现有 972 unit + 46 integration 全绿(零行为变化)
- Dovetail 编译期 DOVE001-020 全过
- `dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams/extraction-job-dag.html` 产 3 变体 HTML,提交

### 8.4 PARKED items(继承 Slice 1-4)

- **path-agnostic e2e 断言**:Dovetail DAG-first 与手写 runner 等价性规则固有限制,plan-mandated
- **seam 互作 quirk**:hand-built 同时传坏容器+seam 时 catch 先返 null — 实践不可达,先例同样无 catch
- **JobState vs JobRunContext 同步期**:本 spec 一次性切换,不保留兼容层(若生产已用 JobRunContext,需 1 个 commit 同步切换,scheduler 配合)

---

## 9. 任务拆分

| Task | 范围 | Tests |
|------|------|-------|
| 1 | JobInput/JobState/JobResult + JobKind enum + ChunkResult/JobTerminology + JobStateMutationTests | +4 |
| 2 | ExtractionOrchestrator: JobRunContext 弃用 + 5 phase-runner 内部 `with` 化 + RunJobSafelyAsync 替换 + JobPipelineRouter 占位 | 0(零 test 改动 gate) |
| 3 | 5 phase step classes(LayerStep generic + AgentStep + CorpusStep + HierarchyStep + TerminologyStep + NoOpAgentStep + PerPhaseCatchStep)+ 8 tests | +8 |
| 4 | 3 JobPipeline 变体(TBoxOnly/ABoxOnly/Combined)+ JobPipelineRouter + 9 tests | +9 |
| 5 | DI registrations(§9 Job block)+ 4 tests | +4 |
| 6 | RunJobSafelyAsync 接入 JobPipelineRouter + dispatcher 移除 RunWithExtractionGuardAsync + 2 tests + 1 e2e | +3 |
| 7 | dovetail-report HTML 产 3 变体 + spec 落地 + memory 落地 + 收尾 | n/a |

预计 commit 数:~10-12,与 Slice 4 持平。

---

## 10. 风险与回退

| 风险 | 缓解 | 回退 |
|------|------|------|
| 5 phase-runner 内部 JobState 切换范围大于预期 | Task 2 单独执行,现有 972 test 全绿作为硬 gate;若失败,定位到具体 phase-runner 修复 | 若 JobState 切换污染行为,Task 2 回滚到 mutable JobRunContext,Task 3-6 暂缓 |
| 3 个 JobPipeline 变体的 DI 注册复杂度 | §9 注册块单文件 append,沿用 Slice 1-4 模式 | 若 DOVE017 触发(同一切片内 LayerStep generic 实例化 shape 冲突),拆 `LayerTBoxStep` + `LayerABoxStep` 2 个独立 step |
| Dispatcher 移除 RunWithExtractionGuardAsync 后并发行为漂移 | Task 6 e2e 测试覆盖并发抢占场景 + cross-KS lock 行为 | 若 409 envelope 行为漂移,Task 6 在 dispatcher 加回 RunWithExtractionGuardAsync,JobPipelineRouter 不包 GuardedSegment,409 envelope 留 dispatcher |
| 现有 hand-built 测试 `_scopes is null` 路径断裂 | NoOpAgentStep 替代真段(同 Slice 3 R2 模式);Task 3 包含 NoOpAgentStep 测试 | 若 NoOp 替换破坏 test fixture,Task 3 回滚 AgentStep 改用 `_scopes is null` runtime 检查 |
| 现有 `TBoxOnlyRunnerAsync` / `ABoxOnlyRunnerAsync` / `CombinedRunnerAsync` 删除后回归 | Task 2 一次性切换,所有现有测试必须全绿;Task 2 提交后跑全量 | 若 5 phase-runner 顺序在新 JobPipeline 变体中漂移,Task 2 暂时保留 3 个 runner delegate 作为 fallback,JobPipelineRouter 暂不启用 |

---

## 11. 决策日志

- **D1 薄壳包装**(2026-08-29,继承父 spec):不重写 5 phase-runner 业务逻辑;只把段间控制流抽到 Dovetail
- **D2 A 方案纯薄壳**(2026-08-29,用户拍板):3 个 JobPipeline 变体 + OptionalSegment + GuardedSegment 顶层 + 零 error 模型升级;2 slice 之内
- **D3 3 JobPipeline 变体**(2026-08-29,用户拍板):TBoxOnly/ABoxOnly/Combined 各自 1 个独立 pipeline,各自 Mermaid 出图;不引入运行时 kind flags
- **D4 immutable JobState 透传**(2026-08-29,用户拍板):`JobState` record 在 5 phase sequential 透传;JobRunContext mutable struct 一次性删除;不改 Dovetail 函数式 record 调子
- **D5 409 envelope 提到 pipeline 顶层**(2026-08-29,继承父 spec D6):`GuardedSegment` 包 1 次在 JobPipelineRouter.ExecuteAsync 入口;dispatcher 移除 `RunWithExtractionGuardAsync` wrapper
- **D6 Optional skip 静态决定**(2026-08-29,继承父 spec D4):DI 层选 NoOp vs 真段;runtime 不引入 flags
- **D7 per-phase try/catch 包装**(2026-08-29,继承 Slice 1-4):`PerPhaseCatchStep<TIn, TOut>` Dovetail adapter,失败转 `JobState.Error` + `ShouldSkipRemaining`;`OperationCanceledException` 不吞
- **D8 Terminology fail-soft 契约保留**(2026-08-29):TerminologyStep 不动内部 try/catch,Terminology 异常由 RunTerminologyAsync 内部 `termCapture.MarkError()` 处理
- **D9 零 Cancellation 传播升级**(2026-08-29):背景 task 仍 `CancellationToken.None` 透传,后续切片单独评估
- **D10 JobRunContext 一次性切换**(2026-08-29):不留兼容层;Task 2 提交后 grep 全仓无 JobRunContext 引用

---

## 12. Spec 自审

### 12.1 Placeholder scan

- ✅ 无 TBD / TODO / "实现细节"占位符
- ✅ 每个 record 签名明确(§4)
- ✅ 每个测试有具体断言(§8.2)
- ✅ DI 注册块写明(§6.1)

### 12.2 Internal consistency

- ✅ §3 架构图与 §4 Data Flow 字段一致(5 phase IO record 表)
- ✅ §5 Error Handling 三层与 §6 文件结构中 `PerPhaseCatchStep` / `GuardedSegment` 一致
- ✅ §7 Orchestrator 改动与 §1 现状的 5 phase-runner 行号引用一致
- ✅ §8 测试策略与 §9 任务拆分的 Tests 列一致

### 12.3 Scope check

- ✅ 单个 plan 可落地(7 task,2 slice 之内)
- ✅ 不涉及前端 / 数据库 schema / Docker
- ✅ JobStatus 状态机 / ExtractionJobStore 零改(纯薄壳)

### 12.4 Ambiguity check

- §4.2 JobState 字段表明确;`ShouldSkipRemaining` 计算定义清晰
- §5.1 Optional 条件与父 spec D4 一致;`NoOpAgentStep` 复用 Slice 1-4 `null!` 模式
- §7.2 RunJobSafelyAsync 改后代码完整;router 二选一分支显式
- §8.2 测试名 + 断言目标明确

---

**Spec 状态**:待用户审核
**下一步**:用户批准后,invoke `superpowers:writing-plans` 写实施计划