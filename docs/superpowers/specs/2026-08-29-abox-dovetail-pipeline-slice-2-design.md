# ABox Dovetail 抽取流水线 — Slice 2 设计

**日期**:2026-08-29
**作者**:Claude / ISEStudio
**状态**:设计 / 待用户审核
**前置**:Slice 1 (TBox 子 DAG) 已完成 (commit `57d1753`)
**范围**:把 ABox duplicate-class detection 从 [src/ISEStudio/Ontology/DuplicateJudge.cs](src/ISEStudio/Ontology/DuplicateJudge.cs) 单体服务重构为 5 阶段 Dovetail DAG;**第一个 slice 落「merge_classes + cascade retype 真自动应用」**

---

## 1. 背景与现状

ISEStudio 当前 ABox duplicate-class 检测是 `DuplicateJudge.DetectAsync` (596 行,3 个构造依赖),在 `ConflictService.DetectAsync` 内被调用 ([src/ISEStudio/Conflicts/ConflictService.cs:127](src/ISEStudio/Conflicts/ConflictService.cs#L127)):

**当前 3 段流水线**(在 `DuplicateJudge` 方法体内硬编码):
1. **StringCandidates** — Jaccard 字符串相似度(token set),`>= 0.86` 即候选
2. **EmbeddingCandidates** — `baai/bge-m3` 多语 embedding cosine,`>= SemanticCandidateThreshold` 即候选
3. **LLMJudge** — 单次 chat completion 批所有候选 pair 进 `conflict.duplicate_judge` prompt,返回 `same` 索引集合

候选 pair 通过 `Eligible` 过滤(子类/不相交/等价关系/合成词 head 失配),kept pair → `DetectedConflict(merge_classes resolution)`,**只发到 conflict queue**,由 `ConflictAgent.TriageAsync` 异步 triage(P3-11 在 `confidence >= AutoApplyFloor(0.85)` 时 auto-apply,但 duplicate-class 当前走 recommendation 路径,因为不在 P3-11 决策点内)。

**Cascade retype 当前在 [src/ISEStudio/Ontology/OntologyEditor.cs:193-310](src/ISEStudio/Ontology/OntologyEditor.cs#L193)**:每次 `merge_classes` 调用 `CascadeClassMergeAsync`,把所有 `<source_instance> a :source_class` 三元组重写为 `:target_class`,走自己的 capture(防 LockRecursion)。

**Slice 1 (commit `57d1753`) 已经奠定了 Dovetail 流水线模板**:
- `TBoxChunkPipeline` (4 段) + `TBoxJobPipeline` (4 段) 作 `partial class` 落地
- `DovetailPipelineRegistrations.AddDovetailPipelines()` 做 DI 集中注册
- `ExtractionOrchestrator.ExtractAndVerifyAsync` 用 `_chunkPipeline is not null ? pipeline : service` ternary 走新优先 + 老 fallback
- `FailSoftSegment` / `OptionalSegment` / `NoOpSegment` / `GuardedSegment` 四个适配器可复用

---

## 2. 设计目标

| 目标 | Dovetail 给的能力 |
|---|---|
| **可拆分** | 5 段 [Segment] 各自一个文件,每段可独立单测,编译期类型匹配自动派生 DAG(无 string-key、无反射) |
| **可视化** | 自动 Mermaid doc comment + `dovetail-report` HTML 报告(沿用 Slice 1 的 343c0af 工具) |
| **可降级** | 任意阶段失败 → `FailSoftSegment` 包裹 → 退化为空结果 + 上一阶段产物继续走下一段;整段挂 → 走 `OptionalSegment` 退回 `DuplicateJudge` 原服务 |
| **Auto-apply 跟 triage 路径共用** | 高置信走 DAG 自动应用 + cascade retype,低置信 emit conflict 给 triage(同 P3-11 模式) |
| **写图事务** | Stage 4 (MergeApply) + Stage 5 (CascadeRetype) 在同一个 `QuadChangeCapture` 内,失败回滚(revertOnError:false + try/catch MarkError,与 P3-11 修复后模式一致) |
| **不动现有 conflict auto-apply** | P3-11 `ConflictAgent.TryAutoApplyAsync` 仍在 `decision.Confidence >= AutoApplyFloor(0.85)` 路径生效;slice 2 加一个**独立的** `DuplicateAutoApplyFloor` 阈值 |

### 非目标(Slice 2 不做)

- **per-tenant / per-KS 运行时切换流水线拓扑** — Dovetail 编译期类型匹配,做不到 runtime 改图(沿用 Slice 1 决策)
- **持久化 / resume / checkpoint** — Dovetail 单次 in-process 执行
- **把 slice 5 的 5 runner 调度做掉** — Slice 2 只新增 `RunLayerAsync(ABox)`,跟 slice 1 `RunLayerAsync(TBox)` 平级;顶层 5 runner 编排留 Slice 5
- **改 `ConflictAgent` 的 P3-11 自动应用逻辑** — Slice 2 DAG 是新增路径,不污染现有 agent 路径

---

## 3. 5 阶段 DAG 形状

```
                       JobInput(JobId, KsId, GraphIri, StoreWrapper, Chat, MinConfidence)
                              │
                              ▼
                ┌─────────────────────────┐
                │ 1. CandidateGather      │  Jaccard 字符串相似度(原 DuplicateJudge.StringCandidates)
                │    (input.JobInput)     │  → CandidateList(string IriA, string IriB, double? Cosine)
                └────────────┬────────────┘
                             ▼
                ┌─────────────────────────┐
                │ 2. EmbeddingMatch       │  baai/bge-m3 embedding cosine
                │    (input.JobInput +    │  → CandidateList(加 cosine 字段)
                │     CandidateList)      │  (EnableSemanticConflicts=false → NoOpSegment)
                └────────────┬────────────┘
                             ▼
                ┌─────────────────────────┐
                │ 3. LLMJudge             │  单次 chat 批所有 candidate 进
                │    (input.JobInput +    │  conflict.duplicate_judge prompt
                │     CandidateList)      │  → JudgeResult(KeptIndices, Reason)
                │                         │  (VerifyDuplicatesWithLlm=false → NoOpSegment)
                │                         │  (JudgeError → FailSoftSegment: 全 kept)
                └────────────┬────────────┘
                             ▼
                ┌─────────────────────────┐
                │ 4. MergeApply           │  对 JudgeResult.KeptIndices
                │    (input.JobInput +    │  → AppliedMerges + RemainingConflicts
                │     CandidateList +     │
                │     JudgeResult)        │  (Confidence < DuplicateAutoApplyFloor → emit conflict, skip apply)
                └────────────┬────────────┘
                             ▼
                ┌─────────────────────────┐
                │ 5. CascadeRetype        │  对每个 AppliedMerge 调
                │    (input.JobInput +    │  OntologyEditor.CascadeClassMergeAsync
                │     AppliedMerges)      │  → CascadeResult(UpdatedIndividuals)
                │                         │  (Service=null → NoOpSegment)
                └────────────┬────────────┘
                             ▼
                   ABoxJobResult(MergesApplied, ConflictsEmitted, CascadeUpdates)
```

**Dovetail 类型契约**(每个阶段 IPipelineSegment 多输入形):

```csharp
public sealed record ABoxJobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    string GraphIri,
    StoreWrapper Store,
    IChatClient Chat,
    IEmbeddingGenerator<string, Embedding<float>> Embedder,
    double MinConfidence);

public sealed record CandidateList(
    IReadOnlyList<CandidatePair> Pairs);  // (IriA, IriB, double? Cosine)

public sealed record JudgeResult(
    IReadOnlyList<int> KeptIndices,       // 索引进入 CandidateList.Pairs
    string? Reason);

public sealed record AppliedMerges(
    IReadOnlyList<MergedClassPair> Pairs);  // (Source, Target, Confidence)

public sealed record RemainingConflicts(
    IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts);

public sealed record CascadeResult(
    IReadOnlyList<Guid> UpdatedIndividuals);

public sealed record ABoxJobResult(
    AppliedMerges Applied,
    RemainingConflicts Remaining,
    CascadeResult Cascade);
```

---

## 4. 关键设计决策

### D1:薄壳包装(沿用 Slice 1 D1)

每个 step class 是 partial pipeline ctor 参数,**内部调现有服务**:
- `CandidateGatherStep` → `DuplicateJudge.StringCandidates(...)`(已有 static 方法,可直接调)
- `EmbeddingMatchStep` → `DuplicateJudge.EmbeddingCandidatesAsync(...)`(已有 instance 方法,通过 DI 注 DuplicateJudge 或拆出独立 EmbeddingService)
- `LLMJudgeStep` → `DuplicateJudge.JudgeDuplicatesAsync(...)`(已有 instance 方法)
- `MergeApplyStep` → 新增 `ClassMergeApplyService.ApplyAsync(...)`,内部调 `OntologyEditor.ApplyClassMergeAsync` + `AuditLogService.LogAsync` + 阈值分流
- `CascadeRetypeStep` → `OntologyEditor.CascadeClassMergeAsync(...)`(已有方法)

**Outcome**:DuplicateJudge 不删,作为 fallback path;新 DAG 优先,fall back 到老服务调用 (跟 Slice 1 TBox 同模式)

### D2:Step 接口全部 multi-input(IPipelineSegment<T1, ..., TOut>)

沿用 Slice 1 + DOVE006 修复后模式,**bundle record 一律不引入**:
- `CandidateGatherStep : IPipelineSegment<ABoxJobInput, CandidateList>` (单输入)
- `EmbeddingMatchStep : IPipelineSegment<ABoxJobInput, CandidateList, CandidateList>` (双输入,加 cosine)
- `LLMJudgeStep : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult>` (双输入)
- `MergeApplyStep : IPipelineSegment<ABoxJobInput, CandidateList, JudgeResult, (AppliedMerges, RemainingConflicts)>` (三输入)
- `CascadeRetypeStep : IPipelineSegment<ABoxJobInput, AppliedMerges, CascadeResult>` (双输入)

DOVE006 自动保证 input type 必须是 pipeline input 或上一步 output 类型。

### D3:`DuplicateAutoApplyFloor` 独立阈值(新增 ISEStudioOptions 字段)

```csharp
public sealed class ISEStudioOptions
{
    // ... 现有 AutoApplyFloor (P3-11 conflict agent 用) ...
    public double DuplicateAutoApplyFloor { get; set; } = 0.90;  // 新增,默认 0.90 更严
}
```

**为何独立**(不复用 `AutoApplyFloor = 0.85`):
- duplicate-class merge 是不可逆 + 影响 ABox instance retype,影响面比 conflict resolution 大
- LLM judge 输出已是 0/1(same / not-same)二值,没有 "confidence 0.92" 这种连续输出,所以 floor 实际是「LLM 是否通过」二值开关,不用 float
- 0.90 = 跟 Python `DUP_THRESHOLD = 0.86` 字符串阈值 + embedding cosine 0.85 + LLM 通过 三层 AND 后才 auto-apply,跟 P3-11 的单层 confidence 语义不同

**实际 Slice 2 行为**(评审时可改):
- LLM 通过 + cosine >= 0.85 + jaccard >= 0.86 → auto-apply (三层 AND)
- LLM 通过但 cosine < 0.85 或 jaccard < 0.86 → emit conflict 给 triage
- LLM 不通过 → 跳过此 pair

`DuplicateAutoApplyFloor` 字段先建,默认值先按 0.0(永远 auto-apply LLM 通过的),待 Slice 2 review 阶段用户拍板是否要更高门槛。

### D4:Optional / FailSoft 适配器复用

- `EmbeddingMatchStep` 接受 `DuplicateJudge?` nullable;若 `EnableSemanticConflicts = false` → DI 注册 `NoOpSegment` 直接 pass-through
- `LLMJudgeStep` 同 nullable;若 `VerifyDuplicatesWithLlm = false` 或 chat factory 没注册 → `NoOpSegment`
- `MergeApplyStep` 内部分流:`confidence >= DuplicateAutoApplyFloor` → auto-apply;否则 → emit conflict,无第三方依赖,无 `OptionalSegment` 包装
- `CascadeRetypeStep` 接受 `OntologyEditor?` nullable;editor 总是 DI 注册,**不必 OptionalSegment 包装**(只有 Editor 注册失败才退化,但 Production 必注册)

### D5:MergeApply 写图事务安全(沿用 P3-11 fix 模式)

`MergeApplyStep` 内部:
1. `QuadChangeCapture` capture on ABox graph (`revertOnError: false`,符合 P3-29af563 fix)
2. 对每个 kept pair 调 `OntologyEditor.ApplyClassMergeAsync(source, target)`(一个 merge 一个 capture?或一批 merge 一个 capture?评审时定)
3. 失败 `try/catch MarkError(...)` + audit log + cascade 跳过
4. 全部 success → audit `merged` event + CascadeRetypeStage 接续

**不引入新事务原语**(已用 QuadChangeCapture),不改 GraphWriteCoordinator。

### D6:`RunLayerAsync(ABox)` 新增 branch + ConflictService 改 forwarder

`ExtractionOrchestrator` 新增:
```csharp
private async Task<ABoxLayerOutcome> RunLayerAsync(ABoxLayerContext ctx, CancellationToken ct)
{
    var input = new ABoxJobInput(...);
    var pipeline = sp.GetService<ABoxJobPipeline>();  // 走 DI,nullable
    var output = pipeline is not null
        ? await pipeline.ExecuteAsync(input, ct).ConfigureAwait(false)
        : await _duplicateJudge.DetectAsync(...).ConfigureAwait(false);  // fallback
    return new ABoxLayerOutcome(output.Applied, output.Remaining, output.Cascade);
}
```

`ConflictService.DetectAsync` 改 forwarder(单行):
```csharp
public async Task<IReadOnlyList<ConflictOut>> DetectAsync(Guid ksId, CancellationToken ct)
{
    // 旧:调 _duplicateJudge.DetectAsync(...)
    // 新:转发到 ExtractionOrchestrator.RunLayerAsync(ABox)
    var outcome = await _extraction.RunABoxLayerAsync(ksId, ct).ConfigureAwait(false);
    return outcome.Remaining.Conflicts.Select(_conflictMapper.ToConflictOut).ToList();
}
```

### D7:不做 pipeline-as-segment 双重实现(沿用 Slice 1 D7)

`ChunkPipelineStep` 类比为 ABoxJobPipeline 的 input pass-through;ABoxJobPipeline 是顶层 DAG,不嵌 TBox pipeline。Multi-step input 走 Dovetail 原生 multi-input。

---

## 5. 失败模型与适配层

### 5.1 错误处理原则

| 阶段 | 失败模式 | 适配方式 |
|---|---|---|
| CandidateGather | 字符串算法永不抛 | 无 |
| EmbeddingMatch | embedding provider 不可用 / 网络错 | `FailSoftSegment` 包裹 → 空 `CandidateList` 继续(原 DuplicateJudge 行为) |
| LLMJudge | chat 不可用 / JSON parse fail / 网络错 | `FailSoftSegment` 包裹 → `JudgeResult(KeptIndices = All, Reason = "judge_unavailable")` 让下一阶段全 kept |
| MergeApply | 编辑器抛(冲突 / 锁 / 写图失败) | `try/catch MarkError` + audit `merge.failed` event + 该 pair 不进 CascadeRetype;其他 pair 继续 |
| CascadeRetype | 编辑器抛 | `try/catch MarkError` + audit `cascade.failed` event;已应用的 merge 不回滚(P3-11 模式) |

### 5.2 409 envelope 与 Dovetail 关系(沿用 Slice 1)

Slice 1 已建 `IRunWithExtractionGuard` + `ExtractionGuard` + `GuardedSegment`,Slice 2 直接复用:**ABoxJobPipeline ctor 不加 `GuardedSegment`**(同 slice 1 决策,留 slice 5 顶层 wrap)。

### 5.3 Audit 写入

`MergeApplyStep` 成功 auto-apply 一个 pair 后:
```csharp
await _audit.LogAsync(new AuditEventEntity
{
    ActorName = "abox-dovetail-pipeline",
    Action = "duplicate.merge",
    Detail = JsonSerializer.Serialize(new { source, target, confidence, judgeReason }),
    Graph = input.GraphIri,
}, ct);
```

跟 P3-11 ConflictAgent 直写 audit 行模式一致(`ActorName` 字符串 + `ActorId` null)。

---

## 6. 文件结构与类型清单

### 6.1 新建文件

```
src/ISEStudio/Extraction/
└── Dovetail/
    └── ABox/                                  (新)
        ├── ABoxJobPipeline.cs                 // partial, 5 [Segment]
        ├── ABoxJobInputs.cs                   // record: ABoxJobInput, CandidateList, JudgeResult, AppliedMerges, RemainingConflicts, CascadeResult, ABoxJobResult, CandidatePair, MergedClassPair
        ├── Steps/
        │   ├── CandidateGatherStep.cs         // 调 DuplicateJudge.StringCandidates
        │   ├── EmbeddingMatchStep.cs          // 调 DuplicateJudge.EmbeddingCandidatesAsync
        │   ├── LLMJudgeStep.cs                // 调 DuplicateJudge.JudgeDuplicatesAsync
        │   ├── MergeApplyStep.cs              // 调 OntologyEditor.ApplyClassMergeAsync + audit + 阈值分流
        │   └── CascadeRetypeStep.cs           // 调 OntologyEditor.CascadeClassMergeAsync
        └── ABoxPipelineRegistrations.cs       // IServiceCollection 扩展,AddABoxPipelines() 并入 DovetailPipelineRegistrations
```

```
src/ISEStudio.Tests/Extraction/Dovetail/ABox/
├── ABoxJobInputsTests.cs                      // 6 records 形状测试
├── Steps/
│   ├── CandidateGatherStepTests.cs            // 调 DuplicateJudge.StringCandidates stub
│   ├── EmbeddingMatchStepTests.cs             // 调 EmbeddingCandidates stub + null fallback
│   ├── LLMJudgeStepTests.cs                   // 调 JudgeDuplicates stub + JSON parse fail + fail-soft
│   ├── MergeApplyStepTests.cs                 // 高置信 auto-apply + 低置信 emit conflict + 编辑器抛降级
│   └── CascadeRetypeStepTests.cs              // 调 CascadeClassMerge stub
├── ABoxJobPipelineTests.cs                    // happy-path: 5 段 + 空输入
└── ExtractionOrchestratorABoxPipelineTests.cs // DI resolvability + 老服务 fallback
```

### 6.2 现有文件改动

| 文件 | 改动 |
|---|---|
| `src/ISEStudio/Configuration/ISEStudioOptions.cs` | 加 `DuplicateAutoApplyFloor` 字段(默认 0.0) |
| `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` | `AddDovetailPipelines()` 内追加 ABox 步骤注册 + ABoxJobPipeline(auto via AddPipelines()) |
| `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` | 加 `_aboxPipeline` field + ctor 尾参 + `RunABoxLayerAsync(...)` 新方法(走 pipeline 优先 / DuplicateJudge fallback) |
| `src/ISEStudio/Conflicts/ConflictService.cs` | `DetectAsync` 改 forwarder → `_extraction.RunABoxLayerAsync(...)`;移除 `DuplicateJudge?` 直接依赖 |
| `src/ISEStudio/Conflicts/ConflictServiceCollectionExtensions.cs` | 删除 `services.AddScoped<DuplicateJudge>()` 行(由 Extraction DI 注册);注入 ExtractionOrchestrator |
| `src/ISEStudio/Ontology/DuplicateJudge.cs` | **不改 public 签名**;`StringCandidates` / `EmbeddingCandidatesAsync` / `JudgeDuplicatesAsync` 都已 static/instance 可调用,做 fallback path |
| `src/ISEStudio/Ontology/OntologyEditor.cs` | **不改**(MergeApplyStep / CascadeRetypeStep 直接调现有方法) |

### 6.3 类型清单

(沿用 §3,完整列出)

```csharp
public sealed record ABoxJobInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    string GraphIri,
    StoreWrapper Store,
    IChatClient Chat,
    IEmbeddingGenerator<string, Embedding<float>> Embedder,
    double MinConfidence);

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
```

---

## 7. 测试策略

### 7.1 现有测试不变

- `DuplicateJudgeTests`(原 13 tests)继续过 — DuplicateJudge 不删
- `ConflictServiceTests` 调整 1-2 个用例:DetectAsync 现在是 forwarder,断言发到 ExtractionOrchestrator
- 868 unit baseline 维持 / 0 failed / 1 skipped

### 7.2 新增测试(Slice 2)

| Test class | 覆盖 | 估计数量 |
|---|---|---|
| `ABoxJobInputsTests` | 6 records 形状 + record 默认值 | 6 |
| `CandidateGatherStepTests` | 字符串算法 + Eligible 过滤 | 3 |
| `EmbeddingMatchStepTests` | cosine > threshold kept + null service 空返回 + exception fail-soft | 4 |
| `LLMJudgeStepTests` | JSON 解析成功 + 解析失败 fail-soft + null chat → 空集合 | 4 |
| `MergeApplyStepTests` | 高置信 auto-apply + 低置信 emit conflict + 编辑器抛降级 + audit 写入 | 5 |
| `CascadeRetypeStepTests` | 正常 cascade + 编辑器抛降级 + audit 写入 | 3 |
| `ABoxJobPipelineTests` | happy-path: 5 段连贯 + 空输入 | 2 |
| `ExtractionOrchestratorABoxPipelineTests` | DI resolvable + 老 DuplicateJudge fallback | 2 |

**预估新增测试**:29,baseline 868 + 29 = ~897 (实际数字在 plan 阶段精算)

### 7.3 可视化产物(沿用 Slice 1)

`docs/superpowers/diagrams/extraction-abox-dag/`(`dovetail-report --project src/ISEStudio/ISEStudio.csproj --output ...`):
- `index.html`
- `ISEStudio.Extraction.Dovetail.ABox.ABoxJobPipeline.html`(渲染 5 阶段 DAG)
- `vendor/mermaid.min.js` + `vendor/pico.indigo.min.css`

### 7.4 Gate

- 单测 100% 通过(`dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj`)
- 集成测 100% 通过(`dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj`)
- Dovetail 编译期 0 DOVE0xx 错误
- HTML 报告可视化生成成功 + 5 段 DAG 渲染正确

---

## 8. 风险与回退

| 风险 | 缓解 |
|---|---|
| MergeApply 写图事务边界模糊(每个 merge 一个 capture vs 一批一个) | 评审阶段 user 拍板;默认「每个 merge 一个 capture」最安全但慢;Plan 阶段精算 |
| DuplicateJudge 与 ExtractionOrchestrator 循环依赖(ConflictService → ExtractionOrchestrator → ConflictAgent → ConflictService) | ExtractionOrchestrator 不再 inject ConflictService;改 inject DuplicateJudge + OntologyEditor + AuditLogService 直调 |
| EmbeddingGenerator 在 DI 中实例化代价高 | 沿用现有单例注册模式(EmbeddingGeneratorFactory.Create 每次 new 实例;调用次数由 `MinConfidence` 选项控制) |
| `EmbeddingGeneratorFactory.Create` 抛 `InvalidOperationException`(unsupported provider) | `EmbeddingMatchStep` try/catch 返回空 CandidateList(同 DuplicateJudge 行为) |
| LLMJudge fail-soft → 全 kept → 可能误合并 | 跟 Python 行为对齐(Python 也 fail-closed 返回空,但 C# 改为 fail-open 全 kept 是有意为之,因为有 LLM 通过 + cosine + jaccard 三层 AND,LLM 失败时退回双层过滤更安全) |

**回退路径**:跟 Slice 1 一致,`ConflictService.DetectAsync` 是 forwarder;若 ExtractionOrchestrator 抛异常,`RunABoxLayerAsync` 内部 try/catch + 调用 `DuplicateJudge.DetectAsync` 老路径(异常路径而非默认)。

---

## 9. 决策日志

| # | 决策 | 理由 | 替代方案 |
|---|---|---|---|
| 1 | 5 段真流水线(CandidateGather → EmbeddingMatch → LLMJudge → MergeApply → CascadeRetype) | spec 5 阶段清单 + 用户选 A | B (4 段 + merge action),C (3 段 minimal) |
| 2 | 落 ExtractionOrchestrator,ConflictService 改 forwarder | 用户选 B;跟 slice 1 模式一致 | A (ConflictService 同位置) |
| 3 | auto-apply 高置信 + emit conflict 低置信 | 用户选 A;P3-11 已有先例 | B (auto-apply all kept),C (dry-run only) |
| 4 | DAG 输入显式 StoreWrapper + GraphIri + Chat + Embedder | 用户选 A;Stage 4-5 写图需要 transaction context | B (KnowledgeSystemId only) |
| 5 | `DuplicateAutoApplyFloor` 独立阈值(默认 0.0,评审时定) | duplicate merge 不可逆 + 影响 instance retype | 复用 P3-11 AutoApplyFloor(0.85) |
| 6 | Dovetail multi-input 接口(沿用 Slice 1 + DOVE006 fix) | bundle record 不兼容;5 段每段 input 都能由上一步 output 满足 | bundle record(已否决) |
| 7 | MergeApply 写图:每个 merge 一个 capture(默认) | 安全;失败回滚不影响已应用的 | 一批 merge 一个 capture(快但难回滚) |
| 8 | LLMJudge fail-soft → 全 kept(非空集合) | 退回 cosine + jaccard 双层过滤,比 Python fail-closed 更安全 | 沿用 Python fail-closed(空集合) |
| 9 | ExtractionOrchestrator 不再 inject ConflictService,直接 inject DuplicateJudge + OntologyEditor + AuditLogService | 避免循环依赖;切片边界清晰 | 通过 Lazy/Factory 注入(过度工程) |
| 10 | ABoxJobPipeline 不加 GuardedSegment | 沿用 slice 1 决策;slice 5 顶层 wrap | 加 GuardedSegment(过早) |

---

## 10. Spec 自审

### 10.1 Placeholder scan

- ✅ 无 "TBD" / "TODO" / "待定"
- ⚠️ §4 D3 `DuplicateAutoApplyFloor` 默认值评审阶段定 — 已在决策日志 #5 标记
- ⚠️ §8 MergeApply capture 粒度 — 已在决策日志 #7 标记

### 10.2 Internal consistency

- ✅ 5 段名在 §3 / §6.1 / §6.3 / §7.2 一致
- ✅ 类型名在 §3 / §6.3 一致
- ✅ Dovetail multi-input 在 §4 D2 + §6.3 input/output shape 一致
- ✅ ConflictService forwarder 在 §4 D6 + §6.2 改动清单一致

### 10.3 Scope check

- ✅ Slice 2 焦点:ABox sub-DAG 落地,不掺 TBox(已 slice 1)、不掺顶层调度(slice 5)
- ✅ 不改 P3-11 conflict auto-apply 路径
- ✅ 不引入新事务原语

### 10.4 Ambiguity check

- ✅ "auto-apply" 在 §4 D3 + §9 决策 #3 明确定义(三层 AND → auto-apply)
- ✅ "fallback path" 在 §4 D1 + §8 明确定义(DuplicateJudge 不删,走 ConflictService → DuplicateJudge 异常路径)
- ✅ "写图事务" 在 §4 D5 + §5.1 明确定义(QuadChangeCapture + revertOnError:false + try/catch)
