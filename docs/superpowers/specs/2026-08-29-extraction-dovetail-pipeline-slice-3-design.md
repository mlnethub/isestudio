# Slice 3: AgentChainPipeline Dovetail 化设计

**日期**:2026-08-29
**作者**:Claude / ISEStudio
**状态**:设计 / 待用户审核
**父 spec**:`docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`(Dovetail 抽取流水线整体)
**Predecessor**:Slice 1 TBox sub-DAG(commit 57d1753)+ Slice 2 ABox sub-DAG(commit 250af1a)
**Scope**:把 `ExtractionOrchestrator.RunAgentChainAsync`(P1-4 commit 5594371 落地的 4 步手写链)替换为 Dovetail DAG

---

## 1. 背景与现状

P1-4(commit 5594371)已经把 `RunAgentChainAsync` 落地到 `ExtractionOrchestrator`,完整还原 Python `extract.py:325-344/541-558` 的 conflicts → structure 段。当前手写实现是:

```csharp
// P1-4 ExtractingOrchestrator.RunAgentChainAsync (post-P1-4, pre-Slice-3)
RunAgentChainAsync(ctx):
    UpdateProgressAsync(phase=Conflicts)
    DetectAsync()                  // 1. ConflictService.DetectAsync
    TriageAsync(conflicts, model)  // 2. ConflictAgent.TriageAsync
    UpdateProgressAsync(phase=Structure)
    AttachIsolatedAsync(maxSame)   // 3. StructureAgent.AttachIsolatedAsync
    RefreshAsync(ksId)             // 4. best-effort KnowledgeStatsService.RefreshAsync
```

四步串行 await,异常冒泡 → job failed 但 TBox capture 已 commit 保留。`IServiceScopeFactory` seam 让 hand-built 测试 orchestrator 传 null → 整段跳过。两个 agent 走 `skipActiveExtractionGate: true`(P1-4 关键决策:Python `_bg` 变体不携带 gate,.NET agent 内建 gate 会误杀当前 job)。

**Slice 1 spec roadmap 表第 175 行明确**:Slice 3 = "ConflictAgent + StructureAgent (p1-1 + p1-3),3-4 个任务"。Slice 1 + 2 已落地 TBox + ABox 子 DAG,Slice 3 是 conflict/structure agent chain 的 Dovetail 化。

---

## 2. 设计目标

| 目标 | Dovetail 给的能力 |
|---|---|
| **可定义** | agent chain 重排/插入段 = 改 Segment 顺序 → 编译期类型匹配自动派生 DAG |
| **可观测** | 每个 agent 段一个 OpenTelemetry span,ConflictAgent / StructureAgent / StatsRefresh 单独追踪 |
| **可测试** | 每个 step 单独 unit test,hand-built pipeline null 测试仍走 fallback 路径 |
| **薄壳** | step 类只调 agent + 包装 try/catch,不引入新业务逻辑 |

### 非目标

- **替换 `ConflictService.DetectAsync`** — DetectAsync 留在 DAG 外,由 orchestrator 调完后构造 `AgentChainInput.Conflicts`。跨 DAG 整合留给 Slice 5(top-level 5-runner scheduling)
- **持久化 / resume / checkpoint** — Dovetail 一次执行,same as Slice 1/2
- **替换 ABoxJobPipeline** — Slice 2 已落地,本 slice 不动
- **替换 ConflictAgent / StructureAgent 内部** — 只包成 segment,不重写 agent 业务逻辑

---

## 3. Dovetail DAG 形状

```
AgentChainPipeline (DOVE006 多输入)
│
├─[1] ConflictAgentStep
│      Input : AgentChainInput
│      Output: ConflictTriageResult
│      Logic : _agent.TriageAsync(input.Conflicts, input.KnowledgeSystemId,
│                                 input.Model, skipActiveExtractionGate: true, ct)
│              → 包 ConflictTriageResult(TriagedConflicts, RecommendationsAttached)
│
├─[2] StructureAgentStep
│      Inputs : AgentChainInput + ConflictTriageResult
│      Output : StructureAttachResult
│      Logic : _agent.AttachIsolatedAsync(input.KnowledgeSystemId, maxSame,
│                                          skipActiveExtractionGate: true, ct)
│              → 包 StructureAttachResult(IsolatedAttached, NewClassesCreated)
│
└─[3] StatsRefreshStep
       Inputs : AgentChainInput + ConflictTriageResult + StructureAttachResult
       Output : AgentChainResult(Triage, Structure)
       Logic : best-effort _stats.RefreshAsync(input.KnowledgeSystemId, ct)
              → try/catch 吞异常(fail-soft,同 P1-4);返回 AgentChainResult(triage, structure)
```

### DOVE006 契约验证

- Step 1: 1 input(`AgentChainInput`)→ 1 output(`ConflictTriageResult`)
- Step 2: 2 inputs(`AgentChainInput` + `ConflictTriageResult`)→ 1 output(`StructureAttachResult`)
- Step 3: 3 inputs(`AgentChainInput` + `ConflictTriageResult` + `StructureAttachResult`)→ 1 output(`AgentChainResult`)

每个 step 的所有 input 必须是 pipeline input 或前序 step 的 output:**无 bundle record**。✓ DOVE006 通过。

---

## 4. Records(4 个,verbatim)

```csharp
namespace ISEStudio.Extraction.Dovetail.AgentChain;

public sealed record AgentChainInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts,
    string? Model);

public sealed record ConflictTriageResult(
    IReadOnlyList<ConflictDetection.DetectedConflict> TriagedConflicts,
    int RecommendationsAttached);

public sealed record StructureAttachResult(
    int IsolatedAttached,
    int NewClassesCreated);

public sealed record AgentChainResult(
    ConflictTriageResult Triage,
    StructureAttachResult Structure);
```

注:`ConflictDetection.DetectedConflict` 在 `ISEStudio.Ontology` 命名空间(Slice 2 已确认)。

---

## 5. Decisions

### D1 — `ConflictService.DetectAsync` 留在 DAG 外

`AgentChainInput.Conflicts` 由 `RunAgentChainAsync` 在调用 `pipeline.ExecuteAsync(input, ct)` 之前用 `ConflictService.DetectAsync(...)` 填充。**WHY**:DetectAsync 是 ABox 与 AgentChain 共享的入口(Slice 2 ABoxJobPipeline 也内部调它),Slice 5 整合前保持独立调用语义最清晰。

### D2 — `RunAgentChainAsync` body 替换为 DAG 路径或 fallback

```csharp
// Post-Slice-3 RunAgentChainAsync
public async Task RunAgentChainAsync(CancellationToken ct)
{
    if (_scopes is null) return;  // P1-4 seam: hand-built 测试 orchestrator 跳过

    using var scope = _scopes.CreateScope();
    var conflicts = await _conflictService.DetectAsync(_currentKsId, ct);

    var input = new AgentChainInput(
        JobId: _jobId,
        KnowledgeSystemId: _currentKsId,
        Conflicts: conflicts,
        Model: null);

    if (_agentChainPipeline is not null)
    {
        var result = await _agentChainPipeline.ExecuteAsync(input, ct);
        return;
    }

    // Fallback: P1-4 手写链(hand-built 测试 orchestrator 或 DI 失败时)
    var triage = await _conflictAgent.TriageAsync(conflicts, _currentKsId, null, true, ct);
    var structure = await _structureAgent.AttachIsolatedAsync(_currentKsId, maxSameParent, true, ct);
    try { await _stats.RefreshAsync(_currentKsId, ct); } catch { /* fail-soft */ }
}
```

**WHY**:与 Slice 1 D8 thin-shell + service coexist 模式一致。`AgentChainPipeline` 注册为 null 时退化为 P1-4 手写链,既有 6 个 `ExtractionAgentChainTests` 不需要全部重写(可分流:DI 路径测 DAG,hand-built 路径测 fallback)。

### D3 — `skipActiveExtractionGate: true` 透传

两个 agent 段都必须传 `skipActiveExtractionGate: true`,与 P1-4 一致。**WHY**:P1-4 的根因决策 — Python 的 `_bg` 变体不带 gate,仅 detect 端点带 gate。.NET agent 内建 gate → 管线内调用会被 job 自己的 running 行误杀。

### D4 — `StatsRefreshStep` 内 try/catch 吞异常

DAG 不 propagate stats 刷新失败。**WHY**:P1-4 现状(Python 会让 job failed 的历史行为不复刻)。如果未来要做 stats failure telemetry,改 `StatsRefreshStep` 内部 ILogger,不动 DAG 形状。

### D5 — `IServiceScopeFactory` seam 保留

`ExtractionOrchestrator._scopes` 字段 + ctor tail param 保留(hand-built 测试传 null → chain 整体跳过)。**WHY**:同 P1-4。`AgentChainPipeline` 不需要直接拿 scope(每步拿 scoped DbContext 是 step 内部通过 constructor 注入 — pipeline 本身 singleton)。

### D6 — DI 注册走 concrete step type factory

`DovetailPipelineRegistrations` 追加 3 个 step 注册,使用 `sp.GetService<T>()` factory 模式(nullable service deps)。**WHY**:Slice 1 F-1 教训 — 不用 `IPipelineSegment<...>` factory,因为 pipeline ctor 拿 concrete types。

### D7 — 无新 ISEStudioOptions 字段

`AgenticConflictResolution` / `ConflictAgentMaxSteps` / `AutoApplyFloor` / `StructureMaxSameParent` 沿用 `ISEStudioOptions` 现有字段(由 agent 内部读取,不经过 DAG)。

---

## 6. 文件清单

### 6.1 新增(8 个,verbatim)

| 文件 | 行数估算 | 说明 |
|------|----------|------|
| `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainInputs.cs` | ~40 | 4 sealed records |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainPipeline.cs` | ~30 | `public partial class` + 3 `[Segment]` ctor params |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStep.cs` | ~50 | `IPipelineSegment<AgentChainInput, ConflictTriageResult>` |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StructureAgentStep.cs` | ~60 | `IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult>` |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStep.cs` | ~50 | `IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult, AgentChainResult>` |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainInputsTests.cs` | ~60 | 4 record shape tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStepTests.cs` | ~80 | 2 tests(null agent + happy-path) |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/StructureAgentStepTests.cs` | ~80 | 2 tests(null agent + happy-path) |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStepTests.cs` | ~90 | 3 tests(null stats + happy-path + stats throws fail-soft) |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainPipelineTests.cs` | ~60 | 1 happy-path test(Dovetail source-gen emit verify) |
| `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorAgentChainPipelineTests.cs` | ~70 | 2 DI tests(positive + negative) |

**总计:~670 行 / 14 新测试**

### 6.2 修改(3 个)

| 文件 | 改动 | 行数估算 |
|------|------|----------|
| `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` | 加 `_agentChainPipeline` 字段 + ctor tail param + `RunAgentChainAsync` body 替换 + AgentChainInput 构造点 | +35 / -10 |
| `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` | 追加 3 个 step DI 注册 | +25 |
| `src/ISEStudio.Tests/Extraction/ExtractionAgentChainTests.cs` | 6 个 P1-4 测试分流:DI 路径断言 DAG 调用,hand-built 路径断言 fallback | +20 / -5 |

### 6.3 不动

- `src/ISEStudio/Conflicts/ConflictAgent.cs`(P1-1)
- `src/ISEStudio/Ontology/StructureAgent.cs`(P1-3)
- `src/ISEStudio/Knowledge/KnowledgeStatsService.cs`
- `src/ISEStudio/Conflicts/ConflictService.cs`(Slice 2 已 forwarder)

---

## 7. 测试策略

### 7.1 新增(~14 tests)

- **Records (4)**: `AgentChainInput_EmptyConstruction_*` x4(每个 record 一个 shape test)
- **Steps (7)**:
  - `ConflictAgentStep_ExecuteAsync_NullAgent_ReturnsEmptyTriage`(fail-soft)
  - `ConflictAgentStep_ExecuteAsync_HappyPath_ReturnsRecommendationsAttached`
  - `StructureAgentStep_ExecuteAsync_NullAgent_ReturnsZeroAttached`
  - `StructureAgentStep_ExecuteAsync_HappyPath_ReturnsIsolatedCount`
  - `StatsRefreshStep_ExecuteAsync_NullStats_ReturnsAgentChainResult`
  - `StatsRefreshStep_ExecuteAsync_HappyPath_RefreshesAndBundles`
  - `StatsRefreshStep_ExecuteAsync_StatsThrows_FailsSoft_StillReturnsResult`
- **Pipeline (1)**: `AgentChainPipeline_DovetailEmitsExecuteAsync`(验证 source-gen emit)
- **Orchestrator (2)**:
  - `AgentChainPipeline_IsResolvable_FromOrchestratorServices`(positive)
  - `AgentChainPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted`(negative)

### 7.2 现有 P1-4 测试改动(6 tests)

现有 `ExtractionAgentChainTests` 6 个测试断言 `ConflictAgent.TriageAsync` 被调 → 改为:
- DI scope path:断言 `_agentChainPipeline.ExecuteAsync` 被调(或其内 Triage 间接被调)
- hand-built path:断言 fallback 手写链被调

测试断言数不变(6 个测试,内容更新)。如果现有测试断言过细(检查特定 call order / log 调用),允许 implementer 改写(不增加测试数)。

### 7.3 Gate

- `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo` → expect **940 / 0 / 1 / 941**
- `dotnet test --no-restore src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --nologo` → expect 4 / 0 / 0 / 4(Docker unavailable pre-existing)
- 0 build warnings

---

## 8. 风险与前置

### 8.1 已知 controller-accepted 风险(从 Slice 2 复用)

- **DetectAsync 在 DAG 外**:slice 2 也是这种模式(Slice 2 内 `RunABoxLayerAsync` 调 DetectAsync 走 DuplicateJudge,不是 DAG segment)。语义清晰
- **`IChatClient?` nullable**:Slice 2 已处理,本 slice 复现(`_agent` nullable 时 step 走 fail-soft 返回 empty)
- **AuditLogService 调用**:步骤层不调 audit(P3-11 pattern),audit 留给 orchestrator 层
- **`ConflictAgent` / `StructureAgent` 已经接受 scoped DbContext + actor context**:不需要在 step 层再构造

### 8.2 必须由 implementer 验证的签名

| 项 | 期望 | 验证方式 |
|----|------|----------|
| `ConflictAgent.TriageAsync(conflicts, ksId, model, skipActiveExtractionGate, ct)` 签名 | 5 参数 + Task | `src/ISEStudio/Conflicts/ConflictAgent.cs` |
| `StructureAgent.AttachIsolatedAsync(ksId, maxSameParent, skipActiveExtractionGate, ct)` 签名 | 4 参数 + Task<int> | `src/ISEStudio/Ontology/StructureAgent.cs` |
| `KnowledgeStatsService.RefreshAsync(ksId, ct)` 签名 | 2 参数 + Task | `src/ISEStudio/Knowledge/KnowledgeStatsService.cs` |
| `IChatClient` 10.9.0 要求 `IEnumerable<ChatMessage>`(非 `IList`) | brief 已留意 | standard |
| `Dovetail 1.0.0` source-gen emit `AgentChainPipeline.g.cs` | 3 segment wrappers | build verify |

### 8.3 不在 Slice 3 scope

- Slice 5 跨 DAG 整合(ABox DAG ↔ AgentChain DAG 共享 DetectAsync 输出)
- 持久化 agent chain 进度到 job.phase
- StatsRefreshStep 失败时的 telemetry / 日志
- ConflictAgent / StructureAgent 内部重写(本 slice 仅薄壳包装)

---

## 9. 任务分解

预计 5 任务:

1. **Task 1**:`AgentChainInputs.cs` + 4 record tests(commit 1)
2. **Task 2**:3 step classes + 7 step tests(commit 2)
3. **Task 3**:`AgentChainPipeline` partial class + 1 happy-path test + Dovetail emit verify(commit 3)
4. **Task 4**:DI 注册(`DovetailPipelineRegistrations`)+ 1 pipeline DI test(commit 4)
5. **Task 5**:`ExtractionOrchestrator` 改造(`_agentChainPipeline` 字段 + ctor tail param + `RunAgentChainAsync` body 替换 + 6 个 P1-4 测试分流)+ 2 orchestrator tests(commit 5)
6. **Task 6**:`dovetail-report` HTML 出图(commit 6)

预计耗时:与 Slice 2 同量级(2-3 小时)。

---

## 10. LOCKED 默认值

- `DuplicateAutoApplyFloor = 0.90` 已在 Slice 2 落 LOCKED,本 slice 复用
- `skipActiveExtractionGate = true` 已在 P1-4 落 LOCKED,本 slice 复用
- `StatsRefreshStep` fail-soft 已在 P1-4 落 LOCKED,本 slice 复用
- 不引入新 LOCKED option

---

## 11. 与 ADR gap 关联

[[ontopilot-adr-gap-2026-08-23]] 中:
- 🟢 0 项可推进
- 🟡 0 项产品决策点
- ⚪ 10 项已决策不做
- 🔴 3 项跨阶段(Audit/cutover/RBAC)
- 长周期(Guid PK Phase 2 / Ontology Stage 3)— 已 DONE(Phase 2/3)

**Slice 3 不动任何 ADR gap 项**。只是把 P1-4 已落地的 agent chain 包进 Dovetail DAG,无新架构决策。

---

## 12. 与 Slice 1/2 的关系

| 维度 | Slice 1 (TBox) | Slice 2 (ABox) | **Slice 3 (AgentChain)** |
|------|----------------|----------------|--------------------------|
| DAG 入口 | `TBoxJobPipeline.ExecuteAsync` | `ABoxJobPipeline.ExecuteAsync` | `AgentChainPipeline.ExecuteAsync` |
| 输入 | chunks + verify 配置 | candidates + min confidence | conflicts(DetectAsync 外调) |
| 输出 | TBox mutations | ABox mutations + remaining conflicts | triaged conflicts + attached isolated |
| 接通 orchestrator | `RunLayerAsync(TBox)` | `RunABoxLayerAsync` | `RunAgentChainAsync` |
| Fallback | `TBoxVerifyService` | `DuplicateJudge` | 手写 4 步链 |
| Records 数 | 9(+TBoxMerge 10) | 10(+MergeApplyOutput) | **4** |
| Steps 数 | 6 (TBoxJob) + 4 (TBoxChunk) | 6 | **3** |
| 估算任务数 | 10 | 6 | **6** |
| 已完成 commit | 57d1753 | 250af1a | (本 slice) |

---

## 13. 验收

- [ ] Spec 自审:placeholder / 一致性 / scope / 歧义 全过
- [ ] Spec commit + 用户 review
- [ ] Plan 通过 writing-plans skill 生成
- [ ] 5 任务全部 DONE + reviewer APPROVED 或 APPROVED_WITH_CONCERNS(仅 LOW)
- [ ] 最终 whole-branch review:0 critical / 0 high
- [ ] 测试 baseline:927 → **940 / 0 / 1 / 941**
- [ ] DOVE006 多输入契约 verify
- [ ] Slice 1 F-1 dead-DI 教训不重演(concrete step type)
- [ ] Slice 1 D8 thin-shell fallback 保持
- [ ] 5 PARKED LOW 与 slice 2 类似可接受
- [ ] Memory file 更新 `ontopilot-extraction-dovetail-slice3.md`
- [ ] MEMORY.md 索引追加

---

## 14. 相关链接

- 父 spec: `docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`
- Slice 1 spec: `docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`(同一文档,slice 1 是第一个实现)
- Slice 2 spec: `docs/superpowers/specs/2026-08-29-abox-dovetail-pipeline-slice-2-design.md`
- P1-1 spec: `docs/superpowers/specs/2026-08-23-p1-1-conflict-agent.md`
- P1-3 spec: `docs/superpowers/specs/2026-08-23-p1-3-structure-agent.md`
- P1-4 spec: `docs/superpowers/specs/2026-08-23-p1-4-extraction-agent-chain.md`
- 内存:`[[ontopilot-p1-1-conflict-agent]]` / `[[ontopilot-p1-3-structure-agent]]` / `[[ontopilot-p1-4-extraction-agent-chain]]` / `[[ontopilot-extraction-dovetail-slice2]]`

---

## 15. 版本与变更

- **v1.0** (2026-08-29):初始设计,基于 Slice 1/2 已确立的 Dovetail 模式
