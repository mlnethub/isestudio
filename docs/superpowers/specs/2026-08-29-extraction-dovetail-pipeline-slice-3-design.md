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
│      Logic : _agent.TriageAsync(input.KnowledgeSystemId, ct,
│                                 model: input.Model, skipActiveExtractionGate: true)
│              → 包 ConflictTriageResult(TriageLog)
│              (conflicts 在 agent 内部查询 — Task 2 BLOCKED finding,见 §4 变更记录)
│
├─[2] StructureAgentStep
│      Inputs : AgentChainInput + ConflictTriageResult
│      Output : StructureAttachResult
│      Logic : _agent.AttachIsolatedAsync(input.KnowledgeSystemId, input.Model, ct,
│                                          skipActiveExtractionGate: true)
│              → 包 StructureAttachResult(AttachLog)
│              (maxSameParent 在 agent 内部读取 — Task 2 BLOCKED finding)
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

/// <summary>
/// Input to the agent chain Dovetail pipeline. Conflicts are detected
/// externally by <c>ConflictService.DetectAsync</c> (per §5 D1) and passed
/// in here. The pipeline runs ConflictAgent → StructureAgent → StatsRefresh
/// as three typed segments.
/// </summary>
public sealed record AgentChainInput(
    Guid JobId,
    Guid KnowledgeSystemId,
    IReadOnlyList<ConflictDetection.DetectedConflict> Conflicts,
    string? Model);

/// <summary>
/// Output of <c>ConflictAgentStep</c>. Holds the job-log summary lines
/// produced by <see cref="ISEStudio.Conflicts.ConflictAgent.TriageAsync"/>.
/// Note: P1-1's agent returns <c>Task&lt;IReadOnlyList&lt;string&gt;&gt;</c>
/// (job-log summary, NOT a typed count). Records faithfully wrap the
/// real return shape so DOVE006 is satisfied without semantic distortion.
/// </summary>
public sealed record ConflictTriageResult(
    IReadOnlyList<string> TriageLog);

/// <summary>
/// Output of <c>StructureAgentStep</c>. Holds the job-log summary lines
/// produced by <see cref="ISEStudio.Ontology.StructureAgent.AttachIsolatedAsync"/>.
/// Same caveat as <see cref="ConflictTriageResult"/>: real agent returns
/// <c>Task&lt;IReadOnlyList&lt;string&gt;&gt;</c>.
/// </summary>
public sealed record StructureAttachResult(
    IReadOnlyList<string> AttachLog);

/// <summary>
/// Final output of <c>AgentChainPipeline</c>. Bundles the two intermediate
/// results for the orchestrator to log/expose.
/// </summary>
public sealed record AgentChainResult(
    ConflictTriageResult Triage,
    StructureAttachResult Structure);
```

注:`ConflictDetection.DetectedConflict` 在 `ISEStudio.Ontology` 命名空间(Slice 2 已确认)。

**Records 变更记录**:初版 spec §4 把 `ConflictTriageResult` / `StructureAttachResult` 设计成 `int` 计数 records(`RecommendationsAttached` / `IsolatedAttached` + `NewClassesCreated`),Task 2 BLOCKED 发现 P1-1 / P1-3 agents 实际返回 `Task<IReadOnlyList<string>>` job log。变更后 records 改为持有 job log,语义忠实于实际 agents。变更由 Task 2 BLOCKED ruling 触发(ledger Ruling §4),已落地 commit 0c28162(Task 2 fix wave,连同 3 个新 interface)。

---

## 5. Decisions

### D1 — `ConflictService.DetectAsync` 留在 DAG 外

`AgentChainInput.Conflicts` 由 `RunAgentChainAsync` 在调用 `pipeline.ExecuteAsync(input, ct)` 之前用 `ConflictService.DetectAsync(...)` 填充。**WHY**:DetectAsync 是 ABox 与 AgentChain 共享的入口(Slice 2 ABoxJobPipeline 也内部调它),Slice 5 整合前保持独立调用语义最清晰。

### D2 — `RunAgentChainAsync` body 替换为 DAG 路径或 fallback

实际签名保留 `RunAgentChainAsync(JobRunContext ctx)`(初版 sketch 的 `(CancellationToken)` 与 `_jobId` / `_currentKsId` / `_conflictService` 等字段名是对实际 orchestrator 形状的错误假设 — Task 5 实现时验证纠正)。落地形态(commit 891df6f + R2 bed8021):

```csharp
// Post-Slice-3 RunAgentChainAsync(JobRunContext ctx) — 实际形状
private async Task RunAgentChainAsync(JobRunContext ctx)
{
    if (_scopes is null) return;  // P1-4 seam: hand-built 测试 orchestrator 跳过

    using var scope = _scopes.CreateScope();
    var services = scope.ServiceProvider;
    // conflicts / structure 两个 phase 的 UpdateProgressAsync 保留(P1-4 job log 断言依赖)

    var conflictService = services.GetRequiredService<ConflictService>();
    await conflictService.DetectAsync(ctx.Request.KnowledgeSystemId, CancellationToken.None);

    var input = new AgentChainInput(
        JobId: ctx.JobId,
        KnowledgeSystemId: ctx.Request.KnowledgeSystemId,
        Conflicts: Array.Empty<ConflictDetection.DetectedConflict>(), // DetectAsync 返回 wire DTO;steps 不消费
        Model: ctx.Request.Model);                                  // 保留 job 请求的 model(P1-4 行为)

    // R2(final review MEDIUM):scope 解析优先 — 单例 orchestrator 不持有 root
    // 捕获的管线;ctor param 仅作 hand-built 测试 seam。steps 注册为 scoped,
    // 每 job 独立 agents + DbContext(P1-4 per-job 生命周期)。
    var pipeline = services.GetService<AgentChainPipeline>() ?? _agentChainPipeline;
    if (pipeline is not null)
    {
        await pipeline.ExecuteAsync(input, CancellationToken.None);
        return;
    }

    // Fallback: P1-4 手写链(接口键控 GetService<IFoo>(),§5 D6)。
    // 真实签名(Task 2 BLOCKED finding):TriageAsync(ksId, ct, model, gate)
    // / AttachIsolatedAsync(ksId, model, ct, gate) / RefreshAsync(ksId, ct)。
}
```

**WHY**:与 Slice 1 D8 thin-shell + service coexist 模式一致。管线不可得时退化为 P1-4 手写链,既有 6 个 `ExtractionAgentChainTests` 无需重写(hand-built 路径全走 fallback;DAG 路径由新测试覆盖)。

### D3 — `skipActiveExtractionGate: true` 透传

两个 agent 段都必须传 `skipActiveExtractionGate: true`,与 P1-4 一致。**WHY**:P1-4 的根因决策 — Python 的 `_bg` 变体不带 gate,仅 detect 端点带 gate。.NET agent 内建 gate → 管线内调用会被 job 自己的 running 行误杀。

### D4 — `StatsRefreshStep` 内 try/catch 吞异常

DAG 不 propagate stats 刷新失败。**WHY**:P1-4 现状(Python 会让 job failed 的历史行为不复刻)。如果未来要做 stats failure telemetry,改 `StatsRefreshStep` 内部 ILogger,不动 DAG 形状。

### D5 — `IServiceScopeFactory` seam 保留

`ExtractionOrchestrator._scopes` 字段 + ctor tail param 保留(hand-built 测试传 null → chain 整体跳过)。**WHY**:同 P1-4。每步拿 scoped DbContext 是 step 内部通过 constructor 注入;**pipeline 由 Dovetail 注册为 transient,steps 注册为 scoped(R2)**,从 per-job scope 解析 → 每 job 独立 agents + DbContext(P1-4 生命周期,非进程级 singleton — 最终审查 MEDIUM 修复)。

### D6 — DI 注册走 interface-keyed concrete type factory

`DovetailPipelineRegistrations` 追加 3 个 step 注册(`AddScoped`,R2),使用 `sp.GetService<IFoo>()` factory 模式(nullable interface deps;interface 缺失时 factory 返回 `null!` — pipeline 会在 ExecuteAsync 时 NRE,latent,生产恒有 forwarder)。底层 agent / service 类需新增 3 个 interface (`IConflictAgent` / `IStructureAgent` / `IKnowledgeStatsService`),现有 `ConflictAgent` / `StructureAgent` / `KnowledgeStatsService`(`public sealed class`)只需在声明加 `: IFoo` 一行(方法已匹配 interface shape)。**WHY**:实际代码中三个底层 service 都是 `sealed class` + 非虚方法,没有 mocking framework(无 Moq / NSubstitute),加 interface 是唯一干净的单元测试路径(对照 Task 2 BLOCKED ruling)。Slice 1 F-1 教训仍适用 — step ctor 不取 `IPipelineSegment<...>`,仍取 concrete step type。

**生产 DI 注册(R1 修复,commit 63b7411)**:初版 spec 假设 interface 注册已存在,实际没有 — Task 5 审查发现生产 DI 只注册具体类,agent chain 会静默 no-op。三个 module 在具体类注册旁加 forwarder:`AddScoped<IFoo>(sp => sp.GetRequiredService<Foo>())`(interface 与 concrete 共享同一 scoped 实例;**不替换** — 6 个生产服务 ctor 注入具体 `KnowledgeStatsService`)。位置:`ConflictServiceCollectionExtensions.cs:37`、`OntologyServiceCollectionExtensions.cs:25+47`。

### D7 — 无新 ISEStudioOptions 字段

`AgenticConflictResolution` / `ConflictAgentMaxSteps` / `AutoApplyFloor` / `StructureMaxSameParent` 沿用 `ISEStudioOptions` 现有字段(由 agent 内部读取,不经过 DAG)。

---

## 6. 文件清单

### 6.1 新增(实际落地,post-slice 修订)

| 文件 | 说明 |
| ------ | ------ |
| `src/ISEStudio/Conflicts/IConflictAgent.cs` | interface(Task 2 fix wave) |
| `src/ISEStudio/Ontology/IStructureAgent.cs` | interface(Task 2 fix wave) |
| `src/ISEStudio/Knowledge/IKnowledgeStatsService.cs` | interface(Task 2 fix wave) |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainInputs.cs` | 4 sealed records |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/AgentChainPipeline.cs` | `public partial class` + 3 `[Segment]` ctor params + `IPipeline<AgentChainInput, AgentChainResult>` |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/ConflictAgentStep.cs` | `IPipelineSegment<AgentChainInput, ConflictTriageResult>` |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StructureAgentStep.cs` | `IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult>` |
| `src/ISEStudio/Extraction/Dovetail/AgentChain/Steps/StatsRefreshStep.cs` | `IPipelineSegment<AgentChainInput, ConflictTriageResult, StructureAttachResult, AgentChainResult>` |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainInputsTests.cs` | 4 record shape tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/Steps/{ConflictAgent,StructureAgent,StatsRefresh}StepTests.cs` | 2 + 2 + 3 tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainPipelineTests.cs` | 1 test(Dovetail source-gen emit verify) |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/DovetailPipelineRegistrationsAgentChainTests.cs` | 5 tests(Task 4)+ 1 scoped-lifetime test(R2) |
| `src/ISEStudio.Tests/Extraction/Dovetail/AgentChain/AgentChainProductionDiTests.cs` | 3 forwarder tests(R1,真实生产 extension methods) |
| `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorAgentChainPipelineTests.cs` | 2 DI tests(positive + negative) |

**总计:24 新测试**(spec 初版估算 14;Task 2 fix wave +3 接口测试、Task 4 +4、R1 +3、R2 +2 超出)

### 6.2 修改(实际落地)

| 文件 | 改动 |
|------|------|
| `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` | `_agentChainPipeline` 字段 + ctor tail param + `RunAgentChainAsync` body 替换 + R2 scope 解析优先 |
| `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` | 追加 3 个 step DI 注册(AddScoped,R2) |
| `src/ISEStudio/Conflicts/ConflictServiceCollectionExtensions.cs` | +1 forwarder `IConflictAgent`(R1) |
| `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs` | +2 forwarders `IStructureAgent` / `IKnowledgeStatsService`(R1) |
| `src/ISEStudio/Conflicts/ConflictAgent.cs` / `Ontology/StructureAgent.cs` / `Knowledge/KnowledgeStatsService.cs` | 各加 `: IFoo` 一行(方法体零改动) |
| `src/ISEStudio.Tests/Extraction/ExtractionAgentChainTests.cs` | BuildServices 3 行接口键控注册(测试体零改动)+ 1 个 R2 端到端 DAG 测试 |

### 6.3 不动

- `src/ISEStudio/Conflicts/ConflictService.cs`(Slice 2 已 forwarder)
- ABox/TBox 步骤的 singleton 注册(进程级持有 scoped 依赖的同一模式为 Slice 2 继承,最终审查 MEDIUM 的 ABox 侧 — **parked,Slice 5 整合时统一修**)

---

## 7. 测试策略

### 7.1 新增(实际落地 24 tests)

- **Records (4)**: `ConflictTriageResult_EmptyConstruction_HasEmptyTriageLog` / `StructureAttachResult_EmptyConstruction_HasEmptyAttachLog` / `AgentChainInput_EmptyConstruction_HasEmptyConflictsAndNullModel` / `AgentChainResult_AllSubresultsRoundTrip`
- **Steps (7)**:
  - `ConflictAgentStepTests`: `ExecuteAsync_NullAgent_ReturnsEmptyTriageLog` / `ExecuteAsync_HappyPath_ReturnsLogEntries`
  - `StructureAgentStepTests`: `ExecuteAsync_NullAgent_ReturnsEmptyAttachLog` / `ExecuteAsync_HappyPath_ReturnsLogEntries`
  - `StatsRefreshStepTests`: `ExecuteAsync_NullStats_ReturnsAgentChainResult` / `ExecuteAsync_HappyPath_RefreshesAndBundles` / `ExecuteAsync_StatsThrows_FailsSoft_StillReturnsResult`
- **Pipeline (1)**: `AgentChainPipeline_DovetailEmitsExecuteAsync`(验证 source-gen emit)
- **Registrations (6)**: `ConflictAgentStep_IsResolvable_WhenAgentRegistered` / `StructureAgentStep_IsResolvable_WhenAgentRegistered` / `StatsRefreshStep_IsResolvable_WhenStatsRegistered` / `AllAgentChainSteps_ResolveNull_WhenUnderlyingServicesNotRegistered` / `AgentChainPipeline_IsResolvable_WhenAllStepsResolve` / `AgentChainSteps_AreScoped_NotProcessSingleton`(R2 — 两 scope 返回不同 step 实例)
- **Forwarders (3, R1)**: `AddConflictServices_RegistersIConflictAgent` / `AddOntologyServices_RegistersIStructureAgent` / `AddOntologyServices_RegistersIKnowledgeStatsService`(调用真实生产 extension methods,断言 `IsType<Concrete>`)
- **Orchestrator (2)**: `AgentChainPipeline_IsResolvable_FromOrchestratorServices`(positive)/ `AgentChainPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted`(negative)
- **端到端 (1, R2)**: `Scope_resolved_agent_chain_dag_runs_without_ctor_pipeline`(BuildServices + AddDovetailPipelines,不传 ctor pipeline,scope 路径跑通完整链)

### 7.2 现有 P1-4 测试改动(6 tests)

实际落地:**6 个测试体零改动**(commit 891df6f)。唯一共享改动是 `BuildServices` helper 3 行注册从具体类改为接口键控(`AddScoped<IConflictAgent, ConflictAgent>()` 等) — hand-built orchestrator 不传 `agentChainPipeline`(默认 null),6 个测试全部走 fallback 路径,行为与断言不变。初版 spec 设想的"DI 路径断言 DAG 被调"分流被 Task 5 实现者判定不必要(DAG 路径由新测试覆盖,见 7.1 端到端)。

### 7.3 Gate

- `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo` → expect **951 / 0 / 1 / 952**(R2 后终值;spec 初版 940/0/1/941 基于未含 fix wave/R1/R2 的估算)
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

实际落地 6 任务 + 2 个修复轮次:

1. **Task 1**:`AgentChainInputs.cs` + 4 record tests(a4bb442)
2. **Task 2**:3 step classes + 7 step tests — BLOCKED(实际 agent 签名与 spec sketch 不符)→ **fix wave**:amend records + 3 新 interface + step 类(0c28162)
3. **Task 3**:`AgentChainPipeline` partial class + 1 happy-path test + Dovetail emit verify(7ef55ca)
4. **Task 4**:DI 注册(`DovetailPipelineRegistrations`)+ 5 registrations tests(1179f83)
5. **Task 5**:`ExtractionOrchestrator` 改造 + 2 orchestrator tests(891df6f)→ **R1**:3 生产 DI forwarders + 3 tests(63b7411,Task 5 CRITICAL 关注点)
6. **Task 6**:`dovetail-report` HTML 出图(54faec9)
7. **R2**:最终审查 MEDIUM 修复 — scope 解析优先 + steps AddScoped + 2 tests(bed8021)

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
| 已完成 commit | 57d1753 | 250af1a | **9 commits**: a4bb442, 0c28162, 7ef55ca, 1179f83, 891df6f, 63b7411, 54faec9, bed8021(+ 344e9a7 spec 修订) |

---

## 13. 验收

- [x] Spec 自审:placeholder / 一致性 / scope / 歧义 全过
- [x] Spec commit + 用户 review
- [x] Plan 通过 writing-plans skill 生成
- [x] 6 任务 + 2 修复轮次全部 DONE + reviewer APPROVED
- [x] 最终 whole-branch review:0 critical / 0 high(1 MEDIUM → R2 修复 + scoped re-review APPROVE)
- [x] 测试 baseline:927 → **951 / 0 / 1 / 952**(spec 初版估算 940/0/1/941,见 §7.3)
- [x] DOVE006 多输入契约 verify
- [x] Slice 1 F-1 dead-DI 教训不重演(concrete step type)
- [x] Slice 1 D8 thin-shell fallback 保持(6 个 P1-4 测试走 fallback 全绿)
- [x] PARKED 发现:MEDIUM ABox/TBox singleton 持有 scoped 依赖(Slice 2 继承,Slice 5 统一修)+ LOW `null!` factory 不 fail-fast + LOW root 惰性构造残留
- [x] Memory file 更新 `ontopilot-extraction-dovetail-slice3.md`
- [x] MEMORY.md 索引追加

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
- **v1.1** (2026-08-29,post-slice 修订):§3 对齐实际 agent 签名(Task 2 BLOCKED);§5 D2 落地形态 + R2 scope 解析;§5 D5/D6 生命周期与 forwarder 依据;§6/§7 实际文件与 24 个测试清单;§9 实际任务与 commit 栈;§13 验收勾选
