# Slice 4: Vocabulary Pipeline Dovetail 化设计

> 父 spec: `docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md` §5 路线图 Slice 4 — "Vocabulary 流水线(SyncCore 四遍 + Proposals 排入)"。

## 1. 背景与现状

当前 `ExtractionOrchestrator.RunTerminologyAsync`(TBox/ABox/Combined 三个 runner 共用)是一条手写链:

1. `QuadChangeCapture`(VocabularyGraph,revertOnError: false)包住整条链
2. `TerminologyService.SyncAsync` — **一个 ~500 行 monolith(`SyncCore`),内部四遍顺序执行**:
   - **Pass 1** stale mappings(清掉指向已不存在实体的 `op:mapsTo`)
   - **Pass 2** entity sync(4 分支决策树:已有映射 / label 冲突 / 收养 unmapped concept / 新建 mapped concept)
   - **Pass 3** alias additions(每个 mapped concept 补实体 label 为 `skos:altLabel`)
   - **Pass 4** broader additions(子类关系 → `skos:broader`)
3. `UpdateProgressAsync(terminology phase)` + 条件 gating(`_options.TerminologySuggestDuringExtraction && _scopes != null && term.Error == null && SchemeIri != null`)→ scoped `TerminologyAgent.SuggestAsync`(LLM 排入 `TermProposal` rows)
4. `RecordTerminologyAsync` + catch → `QuadChangeCapture.MarkError()`(best-effort,terminology 失败绝不 fail job — Python parity)

pass 间共享状态:`view`(SchemaBuilder TBox 视图)、`ontologyIris`/`aboxIris`、`propertyCount`、`preView`(SkosView)、`schemeIri`(EnsureScheme)、`conceptByMapping`/`mappedIndex`(Pass 2 内部)。

## 2. 设计目标

把 `SyncCore` 四遍 + Proposals 排入封装为 5 段 Dovetail DAG,**行为零变化**(现有全部测试全绿为回归网)。与 Slice 1/2/3 相同的薄壳模式:不动业务逻辑,只把控制流抽到 Dovetail。

### 非目标

- 不改 `TerminologyService.SyncAsync` public 签名与语义
- 不改 `Ontology/VocabularyService.cs`(HTTP CRUD 层)
- 不并发化四遍(四遍有顺序依赖:Pass 2 创建的概念是 Pass 3/4 的输入)
- 不动 `TerminologyAgent.SuggestAsync` 内部(LLM prompt / 解析逻辑)
- Slice 5 的跨 DAG 整合(5 runner 调度)

## 3. Dovetail DAG 形状

```text
TerminologyPipeline (DOVE006 多输入)
│
├─[1] StaleMappingStep
│      Inputs : TerminologyInput
│      Output : TermSyncCarry
│      Logic : carry0 = _terminology.PrepareCarry(input.Ks, ct)   ← init 段内化(§5 D3)
│               carry1 = _terminology.PassStaleMappings(input.Ks, carry0, ct)
│               → carry1
│
├─[2] EntitySyncStep
│      Inputs : TerminologyInput + TermSyncCarry
│      Output : TermSyncCarry
│      Logic : _terminology.PassEntitySync(input.Ks, carry, ct)
│
├─[3] AliasStep
│      Inputs : TerminologyInput + TermSyncCarry
│      Output : TermSyncCarry
│      Logic : _terminology.PassAliasAdditions(input.Ks, carry, ct)
│
├─[4] BroaderStep
│      Inputs : TerminologyInput + TermSyncCarry
│      Output : TermSyncCarry
│      Logic : _terminology.PassBroaderAdditions(input.Ks, carry, ct)
│
└─[5] ProposalStep
       Inputs : TerminologyInput + TermSyncCarry
       Output : TerminologyResult        ← 复用现有 record(orchestrator RecordTerminologyAsync 零改动)
       Logic : gating(input.SuggestEnabled && carry.Error is null && carry.SchemeIri 非空)
               → 查询 chunkIds + _agent.SuggestAsync(ks, schemeIri, chunkIds, input.Model, ct)
               → FoldCarry(carry) with ProposalsQueued;gating 不过 → FoldCarry(carry)
```

### DOVE006 契约验证

- Step 1:1 input → 1 output ✓
- Step 2-4:2 inputs(pipeline input + 前序 carry)→ 1 output ✓
- Step 5:2 inputs(pipeline input + 前序 carry)→ 1 output(`TerminologyResult`)✓

每段输入 = pipeline input 或前序 output,无 bundle record(共享状态全部在 carry record 内 — 父 spec D3"每段产出一个 record"约定)。✓ DOVE006 通过。

## 4. Records(verbatim)

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
/// </summary>
public sealed record TermSyncCarry(
    string? SchemeIri,
    OntologyView View,
    SkosView PreView,
    int PropertyCount,
    int StaleMappingsRemoved = 0,
    int TermsAdded = 0,
    int TermsMapped = 0,
    int MappingConflicts = 0,
    int AliasesAdded = 0,
    int BroaderAdded = 0,
    string? Error = null);
```

输出复用现有 `ISEStudio.Extraction.TerminologyResult`(不新增 output record — orchestrator 的 `RecordTerminologyAsync` 与三个 runner 零改动)。

**注**:`OntologyView` / `SkosView` 均为 `ISEStudio.Ontology` 命名空间(SchemaBuilder.BuildView / SkosManager.BuildView 返回类型)。

## 5. Decisions

### D1 — SyncCore 四遍拆 4 段(用户已锁定)

四遍是路线图标题所指,每 pass 一个 `[Segment]`。不做"整包单段"(DAG 收益薄)也不做"Proposals 先行"(多一个切片周期)。

### D2 — carry record 接力

pass 间共享状态(view / preView / schemeIri / 计数 / Error)全部放进 `TermSyncCarry`,每段吃 `TerminologyInput` + 前序 carry、产新 carry。`SkosManager` 是无状态 wrapper(ctor 只存 store),各 pass 内部 `new SkosManager(_store)` 与现 SyncCore new 一次行为等价;Pass 3/4 的 `BuildView` 重建(读 Pass 2 写入)原样保留在各自 pass 内。

### D3 — init 段内化(无"第 0 段")

SyncCore 头部(view 构建 + ontologyIris + EnsureScheme + preView)抽成 `internal PrepareCarry(ks, ct)`。**Step 1 内部先调 PrepareCarry 再调 Pass 1** — DAG 第一步不能吃不存在的 carry,DOVE006 约束决定 init 必须段内化。PrepareCarry 处理的短路语义(`_store is null` → 零 carry、`ontologyIris.Count == 0` → 零 carry、EnsureScheme null → carry.SchemeIri null)由 FoldCarry 还原出与原 SyncCore 相同的 TerminologyResult 形状。

### D4 — TerminologyService 外科拆分,public 行为零变化

- 新增 6 个 internal 成员:`PrepareCarry` / `PassStaleMappings` / `PassEntitySync` / `PassAliasAdditions` / `PassBroaderAdditions` / `static FoldCarry`(签名见 §6.1)
- `SyncAsync` public 签名不动,body 改为顺序调用 5 个 internal 方法 + FoldCarry;整包 try/catch 保留
- 每个 pass 方法体 = 现 SyncCore 对应注释块**逐行搬移**(含 `ThrowIfCancellationRequested`),共享局部变量改从 carry 读
- Steps 与 Service 同 assembly,直接调 internal 方法(Slice 1 "只抽 internal 方法不改 public 签名"先例)

### D5 — 错误语义:step 内 catch → carry.Error,下游短路

每个 pass step 的 ExecuteAsync:`catch (OperationCanceledException) { throw; } catch (Exception ex) { return carry with { Error = ex.Message }; }`。下游 step 首行判 `carry.Error is not null → return carry`。等价现 `SyncAsync` 整体 catch(任何 pass 抛 → 后续 pass 不执行 → 结果带 Error)。orchestrator 外层 `QuadChangeCapture.MarkError()` 保留。

### D6 — ProposalStep gating 在 step 内判

- gating 条件:`input.SuggestEnabled && carry.Error is null && carry.SchemeIri 非空`
- `_scopes is not null` 条件在 DAG 路径内恒真(只有 scope 存在 orchestrator 才会走 DAG 路径),step 省略 — 记录于本节
- step 内部逻辑 = 现 `RunTerminologyAgentAsync` 的搬移:AsNoTracking 查 `KnowledgeSystemEntity` → Join Documents 查 chunkIds(ordered,Take `TerminologySuggestionMaxChunks`)→ `agent.SuggestAsync(ks, carry.SchemeIri!, chunkIds, input.Model, ct)` → 折叠
- `TerminologyAgent` 为 nullable ctor 参数(hand-built 测试不注册 agent 时 fail-soft 折叠 — 与 Slice 2/3 的 nullable service 模式一致);生产恒非空

### D7 — FoldCarry 双路径复用

`internal static TerminologyResult FoldCarry(TermSyncCarry carry)` 由 `SyncAsync`(fallback 路径)与 `ProposalStep`(DAG 路径)共用,保证两条路径产出的 `TerminologyResult` 形状严格一致。折叠规则:全零 carry → 与现 `TerminologyResult.Zero` 逐字段一致;SchemeIri null → `(0,0,0,null,null,Properties: carry.PropertyCount, 0,0,0,0)`(现 EnsureScheme null 分支形状)。

### D8 — Orchestrator 接线:scope 解析优先(R2 模式)

- `RunTerminologyAsync` body:`QuadChangeCapture` 留在 DAG 外;`UpdateProgressAsync(terminology phase)` 保留;DAG 路径 = `services.GetService<TerminologyPipeline>() ?? _terminologyPipeline`(per-job scope 解析优先,ctor tail param `TerminologyPipeline?` 为 hand-built 测试 seam — 与 Slice 3 R2 相同的 MEDIUM 教训,不重蹈 ctor-injected singleton 捕获)
- DAG 路径内 `term` 变量替换为 pipeline 输出的 `TerminologyResult`;`RecordTerminologyAsync` 原样保留
- fallback:`_terminology.SyncAsync` 整包调用(hand-built 测试 orchestrator 不传 pipeline)
- 三个 runner 调用点(TBox/ABox/Combined,line 417/442/517)不动

### D9 — 步骤 DI 注册(AddScoped,统一 Slice 3 口径)

4 个 pass step 的依赖只有 singleton `TerminologyService`(无 scoped captive 风险),但**仍注册 AddScoped** — 统一 Slice 3 R2 确立的"pipeline 从 per-job scope 解析 → steps scoped"口径,避免未来有人把 scoped 依赖加进这些 step 时踩同一个坑。`ProposalStep` 必须 AddScoped(scoped `TerminologyAgent` + DbContext)。`TerminologyService` 保持 AddSingleton + `ITerminologySync` forwarder(现有注册不动)。

## 6. 文件清单

### 6.1 新增

| 文件 | 说明 |
| ------ | ------ |
| `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyInputs.cs` | `TerminologyInput` + `TermSyncCarry`(§4 verbatim) |
| `src/ISEStudio/Extraction/Dovetail/Terminology/TerminologyPipeline.cs` | `public partial class` + 5 `[Segment]` ctor params + `IPipeline<TerminologyInput, TerminologyResult>` |
| `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/StaleMappingStep.cs` | `IPipelineSegment<TerminologyInput, TermSyncCarry>`;PrepareCarry + Pass 1 + try/catch |
| `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/EntitySyncStep.cs` | `IPipelineSegment<TerminologyInput, TermSyncCarry, TermSyncCarry>`;Error 短路 + Pass 2 + try/catch |
| `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/AliasStep.cs` | 同上形状,Pass 3 |
| `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/BroaderStep.cs` | 同上形状,Pass 4 |
| `src/ISEStudio/Extraction/Dovetail/Terminology/Steps/ProposalStep.cs` | `IPipelineSegment<TerminologyInput, TermSyncCarry, TerminologyResult>`;gating + agent 搬移 + FoldCarry |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyInputsTests.cs` | 2 record shape tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/StaleMappingStepTests.cs` | 2 tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/EntitySyncStepTests.cs` | 2 tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/AliasStepTests.cs` | 2 tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/BroaderStepTests.cs` | 2 tests |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/Steps/ProposalStepTests.cs` | 3 tests(gating 不过 / happy-path / agent 抛 fail-soft) |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/TerminologyPipelineTests.cs` | 1 emit verify |
| `src/ISEStudio.Tests/Extraction/Dovetail/Terminology/DovetailPipelineRegistrationsTerminologyTests.cs` | ~4 DI tests |
| `src/ISEStudio.Tests/Extraction/ExtractionOrchestratorTerminologyPipelineTests.cs` | 2 DI tests(positive + negative) |

### 6.2 修改

| 文件 | 改动 |
| ------ | ------ |
| `src/ISEStudio/Extraction/TerminologyService.cs` | SyncCore body 拆为 6 个 internal 成员 + SyncAsync body 重写(§5 D4) |
| `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` | 追加 5 个 step 注册(AddScoped,ProposalStep 的 agent 为 `GetService<TerminologyAgent>()` nullable factory) |
| `src/ISEStudio/Extraction/ExtractionOrchestrator.cs` | `_terminologyPipeline` 字段 + ctor tail param + `RunTerminologyAsync` body 替换 + `RunTerminologyAgentAsync` 删除(逻辑搬入 ProposalStep) |
| P1-4 词汇相关测试(hand-built orchestrator 走 fallback 整包 SyncAsync) | 预期零改动;仅当某测试断言依赖被删除的 `RunTerminologyAgentAsync` 具体行为时才最小改写(不删断言) |

### 6.3 不动

- `src/ISEStudio/Extraction/TerminologyAgent.cs`(P3-1)
- `src/ISEStudio/Ontology/VocabularyService.cs` / `SkosManager.cs` / `SchemaBuilder.cs`
- `src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs`(现有注册全部保留)
- `ITerminologySync` 接口与 contract tests

## 7. 测试策略

### 7.1 新增(约 20 tests)

- **Records (2)**:`TerminologyInput_EmptyConstruction_*`、`TermSyncCarry_DefaultConstruction_AllZero`
- **Pass steps (8)**:每个 pass step 2 tests — 真实 `TerminologyService` + 内存 store 小 fixture(现 TerminologyServiceTests 的 fixture 模式复用):happy-path(carry 计数推进 + quads 落盘)与 fail-soft(pass 抛 → carry.Error,下游短路)
- **ProposalStep (3)**:gating 不过(Error 非空 / SchemeIri null / SuggestEnabled false)→ FoldCarry;happy-path(fake agent 返回 rows → ProposalsQueued);agent 抛 → Error carry
- **Pipeline (1)**:`TerminologyPipeline_DovetailEmitsExecuteAsync`(source-gen emit verify)
- **DI (4)**:5 steps resolvable(接口/具体注册齐全)+ ProposalStep 在 agent 缺失时 null(如沿用 Slice 3 的 `null!` 工厂口径)+ pipeline resolvable + 负向
- **Orchestrator (2)**:`TerminologyPipeline_IsResolvable_FromOrchestratorServices`(positive)+ `ResolveFails_WhenAddDovetailPipelinesOmitted`(negative)
- **端到端 (1)**:scope 解析跑通(不传 ctor pipeline,AddDovetailPipelines + 真实 services,job 完成 + phases 含 terminology + RecordTerminologyAsync 落盘)

### 7.2 现有测试(零改动目标)

- `TerminologyServiceTests`(SyncAsync 整包行为)+ contract tests:**拆分正确性的回归网** — 全部保持全绿
- P1-4 词汇相关测试:hand-built orchestrator 不传 pipeline → 走 fallback 整包 SyncAsync,行为不变
- 现有测试若断言 `RunTerminologyAgentAsync` 的具体行为,以"行为不变"为准则最小改写(不允许删除断言)

### 7.3 Gate

- `dotnet test --no-restore src/ISEStudio.Tests/ISEStudio.Tests.csproj --nologo` → 951 baseline + 21 新增 = **972 / 0 / 1 / 973**(2 records + 8 pass steps + 3 proposal + 1 pipeline + 4 DI + 2 orchestrator + 1 端到端)
- 集成测试 4 / 0 / 0 / 4(Docker unavailable pre-existing)
- 0 build warnings

## 8. 风险与前置

### 8.1 主要风险

1. **SyncCore 拆分是最重的一块**:pass 间共享局部变量搬进 carry 必须逐行对照(尤其 Pass 2 的 `conceptByMapping`/`mappedIndex` 在循环内刷新、Pass 3 的 `labelOwners` 在写入后刷新)。缓解:拆分 commit 单独成任务,先跑现有 TerminologyServiceTests 全绿再进 step 任务。
2. **`null!` factory 口径**:ProposalStep 的 agent 缺失时 factory 行为需与 Slice 3 一致(负向测试断言 step null);若改用"非空 step + nullable agent 字段"则测试形状不同 — 二选一在 Task 6 实现时由 plan 锁定,spec 推荐沿用 Slice 3 的 `null!` 口径(一致性优先)。
3. **Pass 3/4 的 BuildView 重建**依赖 Pass 2 写入已落盘 — carry 传递的是引用快照,不传"更新后的 view",每 pass 重建行为与原 SyncCore 一致(原代码即每 pass 重建)。

### 8.2 已知 controller-accepted 口径(从 Slice 1/2/3 复用)

- `QuadChangeCapture.MarkError()` best-effort(Python parity)保留在 orchestrator 层
- `TerminologyService` singleton 无 scoped captive 风险 — steps AddScoped 只为口径统一(§5 D9)
- steps 调 internal 方法(Slice 1 先例),step ctor 取 concrete step type 不取 `IPipelineSegment<...>`(Slice 1 F-1 教训)
- dovetail-report HTML 出图(每 slice 惯例)

## 9. 任务分解

预计 8 任务:

1. **Task 1**:`TerminologyInputs.cs` + 2 record tests
2. **Task 2**:`TerminologyService` 拆分(6 internal 成员 + SyncAsync body 重写)+ 现有测试全绿验证(拆分正确性 gate)
3. **Task 3**:4 个 pass step 类 + 8 tests
4. **Task 4**:`ProposalStep` + 3 tests
5. **Task 5**:`TerminologyPipeline` partial + 1 emit verify
6. **Task 6**:DI 注册 + 4 tests
7. **Task 7**:orchestrator 接线 + 2 DI tests + 1 端到端
8. **Task 8**:dovetail-report HTML 出图

## 10. LOCKED 默认值

- `skipActiveExtractionGate` / `DuplicateAutoApplyFloor = 0.90` / StatsRefresh fail-soft 均为先前 slice LOCKED,本 slice 复用不动
- `TerminologySuggestDuringExtraction`(ISEStudioOptions 现有字段)+ `TerminologySuggestionMaxChunks` — 沿用现有选项,无新字段
- 四遍顺序执行(Python parity + pass 间依赖),**不并发化** — LOCKED
- 不引入新 LOCKED option

## 11. 与 ADR gap 关联

本 slice 不动任何 ADR gap 项。只是把 P3-1 / Python parity 已落地的 terminology 链包进 Dovetail DAG,无新架构决策。

## 12. 与 Slice 1/2/3 的关系

| 维度 | Slice 1 (TBox) | Slice 2 (ABox) | Slice 3 (AgentChain) | **Slice 4 (Terminology)** |
| ------ | ------ | ------ | ------ | ------ |
| DAG 入口 | `TBoxJobPipeline` | `ABoxJobPipeline` | `AgentChainPipeline` | `TerminologyPipeline` |
| 输入 | chunks + verify 配置 | candidates | conflicts(空载) | KsContext + 开关 |
| 输出 | TBox mutations | ABox mutations | triaged + attached | **复用 TerminologyResult** |
| 接通 orchestrator | `RunLayerAsync` | `RunABoxLayerAsync` | `RunAgentChainAsync` | `RunTerminologyAsync` |
| Fallback | `TBoxVerifyService` | `DuplicateJudge` | 手写 4 步链 | `SyncAsync` 整包 |
| Records | 9+10 | 10 | 4 | **2 新增 + 复用 1** |
| Steps | 6+4 | 6 | 3 | **5** |
| Service 拆分 | internal 方法抽取 | 无 | 无(加 interface) | **SyncCore 拆 4 pass internal** |

## 13. 验收

- [ ] Spec 自审:placeholder / 一致性 / scope / 歧义 全过
- [ ] Spec commit + 用户 review
- [ ] Plan 通过 writing-plans skill 生成
- [ ] 8 任务全部 DONE + reviewer APPROVED
- [ ] 最终 whole-branch review:0 critical / 0 high
- [ ] 测试 baseline:951 → 新增 ≈20,**全绿**(含现有 TerminologyServiceTests 零改动全绿)
- [ ] DOVE006 多输入契约 verify
- [ ] Slice 1 F-1 dead-DI 教训不重演(concrete step type)
- [ ] R2 教训不重演(scope 解析优先 + steps AddScoped)
- [ ] SyncAsync 整包行为零变化(现有测试全绿 = 拆分正确性证据)
- [ ] Memory file 更新 `ontopilot-extraction-dovetail-slice4.md`
- [ ] MEMORY.md 索引追加

## 14. 相关链接

- 父 spec: `docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`
- Slice 3 spec: `docs/superpowers/specs/2026-08-29-extraction-dovetail-pipeline-slice-3-design.md`
- P3-1 spec: `docs/superpowers/specs/2026-08-23-p3-1-terminology-proposals.md`(TerminologyAgent 出处)
- 内存: `[[ontopilot-extraction-dovetail-slice3]]` / `[[ontopilot-python-retirement]]`

## 15. 版本与变更

- **v1.0** (2026-08-29):初始设计,基于 Slice 1/2/3 已确立的 Dovetail 模式 + 用户锁定的"四遍拆 4 段"决策
