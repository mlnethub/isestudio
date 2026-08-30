# Dovetail 抽取流水线设计

**日期**:2026-08-28
**作者**:Claude / ISEStudio
**状态**:Slice 1-5 全部落地(commit `2376e8a`→`7a39790`,2026-08-30),Slice 6 规划待启动
**范围**:抽取流水线(`ExtractionOrchestrator` 内的 DAG 部分)整体重构到 Dovetail;**第一个 slice = TBox 子 DAG 最小可走通样例**

---

## 1. 背景与现状

ISEStudio 当前抽取流水线是 `ExtractionOrchestrator`(1014 行,17 个构造依赖),其中 DAG 形状硬编码在 `RunLayerAsync` / `RunCorpusRecoveryAsync` / `RunHierarchyRecoveryAsync` 里。TBox 子 DAG(按 P1-5a/P1-5b)P1-3 已经具备雏形:

**TBox 验证三段(critic → adjudicator → denotation)** — 同一 chunk 内的顺序流水线,在 `TBoxVerifyService.VerifyAsync` 内以 `await ... .ConfigureAwait(false)` 串行连接:

- **Critic**(`BoundaryCriticKey`)— 整段 TBox delta(`classes + subclass_of`)一次性交给 LLM,return critic decisions
- **Adjudicator**(`BoundaryAdjudicatorKey`)— 只对 critic 拒绝的 disputed 类;**fail-soft**;return recovered classes
- **Denotation critic**(`DenotationCriticKey`)— 对 critic 接受的 classes 跑一遍,产出 suffix replacement + final accept/reject

adjudicator 与 denotation 在当前代码中是顺序执行,但其实它们消费同一个 critic output,理论上可以并发。当前**没有并发,也没有显式的 DAG 描述**。

**TBox 作业级两段(corpus recovery → hierarchy recovery)**:

- **CorpusRecovery**(`EvidenceSelectorKey` + `CorpusRecoveryKey`)— 跨所有 chunk 的拒绝候选,用 corpus-level evidence 重新评估,return `RecoveredCorpusClass[]`(在 `RunCorpusRecoveryAsync`)
- **HierarchyRecovery**(`HierarchyRecoveryKey` + `HierarchyCriticKey`)— 用最终 class 词汇表提子集边,proposed classes 还会反过来调一次 `TBoxVerifyService.VerifyAsync`(整段 verify 子流水线被复嵌)

可选服务现状:`TBoxVerifyService?` / `CorpusRecoveryService?` / `HierarchyRecoveryService?` 在 hand-built 测试里是 null,生产环境由 `OnToPilotOptions` 决定是否注册。

---

## 2. 设计目标

| 目标 | Dovetail 给的能力 |
|---|---|
| **可定义** | `partial class` + `[Segment]` 构造参数,编译期类型匹配自动派生 DAG;**类型系统即依赖图**,无 string-key 注册、无反射 |
| **可可视化** | 自动生成 Mermaid 流程图(doc comment) + `dovetail-report` CLI 出 HTML 报告 |
| **可重排** | 重排 = 改 Segment 顺序 → 改代码 → 重新编译 → Mermaid 重新画 → 出图变。**接受"改代码"作为重排路径**(用户已确认) |
| **并发内置** | 同层无依赖段自动并发执行,无需手写 `Task.WhenAll`;`[MaxConcurrency(n)]` 节流 |
| **嵌套子流水线** | `IPipeline<TIn, TOut>` 同时实现 `IPipelineSegment<TIn, TOut>` 即可作为段嵌入父流水线 |
| **编译期诊断** | DOVE001-020 错 20 类,把"段依赖错误 / 环 / 不可达 / 类型歧义"在 build 时抓出来 |
| **Tracing** | 自动包 OpenTelemetry Activity,每个段一个嵌套 span(已经引入 OpenTelemetry 1.18.0) |
| **DI** | `services.AddPipelines()` 自动注册所有段(已用 Microsoft.Extensions.DependencyInjection) |

### 非目标(本 spec 不做)

- **per-tenant / per-KS 运行时切换流水线拓扑** — Dovetail 编译期类型匹配,做不到 runtime 改图。如果未来需要,通过 `ConditionalSegment` 包一层读 feature flag(README §Conditional Execution),但本 spec 不引入
- **持久化 / resume / checkpoint** — Dovetail 单次 in-process 执行,不持久化;长跑作业仍走现有 `RunJobSafelyAsync`
- **streaming / incremental result** — Dovetail 一次执行一个 final result
- **替换 `ExtractionOrchestrator` 全部** — 第一个 slice 只切 TBox 子 DAG,其余 4 个 runner(ABox / ConflictAgent / StructureAgent / Vocabulary)与 chunk 级并发调度保留手写

---

## 3. Dovetail 选型与边界声明

### 选型理由

- **本地有 `E:\GitHub\Dovetail`** 项目 + NuGet 包同源;`global.json` 显示 .NET 10 SDK,Dovetail 兼容
- **类型驱动** 比 string-key / 配置驱动更稳:本项目抽取流水线变更频繁,编译期诊断省调试时间
- **Mermaid 自动出图** 与 docs 体系契合:`docs/superpowers/specs/...` 已经 30+ 篇,Dovetail 的图可以作为内嵌可视化附录
- **OpenTelemetry 已有**(SSO/IR 迁移阶段引入 1.18.0),Dovetail 自动 tracing 与之契合

### 考虑过的备选:Microsoft.Agents.AI.Workflows(MAAF)

`E:\GitHub\agent-framework\dotnet\src\Microsoft.Agents.AI.Workflows`(124 文件,Microsoft Agent Framework 子集)。本 spec 评审时也评估过,关键差异:

| 维度 | Dovetail 1.0.0 | MAAF Workflows |
| --- | --- | --- |
| 抽象单元 | `IPipelineSegment<TIn, TOut>` 薄壳 | `Executor` 类 + 消息路由 |
| 连线方式 | `[Segment]` + **编译期类型匹配**自动推 DAG | 显式 Builder + `AddEdge(FanOut/FanIn)`,运行时连线 |
| 编译期诊断 | DOVE001-020,20 类 build-time 错误 | 无;`Build()` 时或运行时报 |
| Source generator | 是(Roslyn codegen) | 否 |
| Checkpointing/持久化 | ❌ | ✅ `CheckpointManager` |
| External request/HIL | ❌ | ✅ `IExternalRequestContext` |
| 并发原语 | 自动数据流 | 显式 `FanOutEdgeData` / `FanInEdgeData` |
| AIAgent 集成 | 松耦合(`IChatClient`,与现状一致) | 紧耦合(`AIAgent` 实例) |
| 多 agent 编排模式 | 不专注 | Sequential / Concurrent / Handoff / GroupChat / Magentic |
| 可视化 | ✅ Mermaid + `dovetail-report` | ❌ 无内置 |
| DI | `AddPipelines()` 自动扫 | 手动 wire executor + edge |
| 框架体量 | 几百行,单包 | 124 文件,核心广 |

**否决 MAAF 的三个具体理由**:

1. **必须把 `TBoxVerifyService` 包成 `AIAgent`**:`BuildSequential` / `BuildConcurrent` 都吃 `AIAgent`,而现有服务直接吃 `IChatClient`。要做就得给每个 LLM 段写 `AIAgent` 适配层,违背"薄壳包装"原则
2. **`ConcurrentWorkflowBuilder` 只给"全并发+聚合"**,不支持 partial-order DAG(critic→{adjudicator, denotation} 共享 critic output 但互不依赖)。TBox 子 DAG 5 步带条件流,MAAF 没有"部分并发"原语,要么全串要么全并发,5 个步骤的并发拓扑无法表达
3. **TBox 子 DAG 用不上 MAAF 80% 能力**:checkpointing / HIL / handoff / groupchat / magentic 全部与本场景无关。引整个框架只用其并发 builder,投入产出比极差

**MAAF 在 ISEStudio 的适用边界**(后续若做以下事再单独评估):HIL 审核中段结果;agent 间的工具调用路由;长跑 agent negotiation 的 checkpoint + resume。当前 P1-1 / P1-3 / P1-4 / P1-5 的 agent chain 都是单次 LLM 调用,不在 MAAF 适用边界内

#### 混合使用评估(Dovetail + MAAF 能否共存)

用户在评审时追问:"自动化抽取中需要人介入时 MAAF 合适,MAAF 与 Dovetail 可否混合?"。结论:**当前无 HIL 需求不需要混合;若未来出现 HIL 需求,有三种混合路径,各有利弊**。

**语义不兼容的根本原因**:

| 维度 | Dovetail | MAAF |
| --- | --- | --- |
| 执行时长 | in-process,秒级 | 长跑,可暂停数小时/数天 |
| 持久化 | 无 | `CheckpointManager` |
| 暂停/恢复 | ❌ 无 `IAsyncEnumerable` | ✅ `ExternalRequest`/`RequestPort` |
| 调用语义 | `Task<TResult> ExecuteAsync(input, ct)` | `Stream` + `RunUntilNextEvent` |
| 段间数据 | 强类型 record 透传 | 消息路由 + 类型翻译器 |

把 Dovetail 段塞进 MAAF Executor,或反之,都会把对方的语义污染掉。

**三种混合路径**:

- **方案 A(MAAF 包 Dovetail)**:MAAF 是父,Dovetail pipeline 整段嵌进一个 MAAF Executor。**问题**:Dovetail 同步 in-process 与 MAAF 长跑可暂停冲突;reviewer 在 Dovetail 之外做意味着等 Dovetail 全跑完才介入,**Dovetail 在此场景里没价值**
- **方案 B(Dovetail 包 MAAF)**:Dovetail 段内部启 MAAF workflow,await 完。**问题**:`IPipelineSegment<TIn, TOut>` 签名是 `Task<TOut>`,要把 MAAF `Stream` 折成 Task 必须 `RunUntilCompletionAsync` 阻塞 → **Dovetail 段变长跑等待,污染并发语义**;同层并发段会启动多个 MAAF workflow 跑很久
- **方案 C(DB 状态协调,推荐)**:`Process A` 跑 Dovetail 自动段 → reviewer 段判 "需人介入" → 写 `ExtractionJobEntity.Status = PendingReview` + 返 `Result<ReviewerDecision>` → Orchestrator 返 caller "已暂停" → `Process B`(后台 worker / HTTP API)监听 approval → 重启 Dovetail pipeline 续跑(传 Pending state)。**优点**:Dovetail 保持 in-process 语义;不引 MAAF;复用现有 `IExtractionJobStore`;无消息路由与持久化复杂度

**当前 ISEStudio 抽取流水线的 HIL 需求评估**(P1-5a/b + P1-1/3/4 + P3-11):**全部 fail-closed 自动决策**;没有任何 reviewer step;决策点(confidence < floor) = 静默拒绝;人介入路径是"用户改 prompt / 修 schema 后重跑",不是中途介入。**当前无 HIL 需求,无需混合**。

**未来 HIL 路径推荐**:优先方案 C(DB 状态协调);reviewer 段产 `ReviewerDecision { AwaitingReview, QuestionText, Context }`,MergeStep 收口;HTTP API `POST /api/extraction/{jobId}/review/approve` 触发异步重启 Dovetail pipeline 续跑。**不引 MAAF**。若后续坚持用 MAAF 做 reviewer step,只在一个边界上用 — MAAF Workflow 是独立 subsystem,独立 host / storage / 部署,与 Dovetail pipeline 通过 DB 协作,无运行时交集。这是"两个独立子系统通过 DB 协作",不是"运行时混合"

### Dovetail 给不了的(明确说出)

| Dovetail 限制 | 本设计对策 |
|---|---|
| 编译期 DAG shape,不可运行时改 | 重排路径 = 改代码 + 重编译(用户已确认) |
| `IPipelineSegment<...>` 同接口多实现 → DOVE017 编译错误 | **不引入"可换实现"概念**;同接口就一个实现,要换就改构造注入的具体类型 |
| 一段抛异常 → 整个 pipeline 失败 | 用 **adapter 段**(`FailSoftSegment`/`OptionalSegment`/`NoOpSegment`)在边界处把异常吞掉返回 fallback,**严格对齐 Python fail-soft 语义** |
| 同类型不能有两个 segment 产(DOVE005) | 段产出的中间类型全部用独立 record,不重用 |
| 链式 endomorphism(`B→B`)只支持一段 | TBoxVerifyService.VerifyAsync 内部的 adjudicator+denotation 合并出 final TBoxDelta 由一个 `MergeSegment` 收口 |
| Generic pipeline 不嵌套 | 不引入泛型 |
| `IPipelineSegment` 最多 8 个输入 | 段间数据交换用 record 聚合,不会超 8 个原子字段 |

---

## 4. 第一个 Slice 范围(本 spec 的实际落地范围)

### Slice 1 — TBox 子 DAG 最小可走通样例

**目的**:把 TBox 子 DAG 全部用 Dovetail 表达,**并且现有 TBox 相关单元/集成测试一个不动全绿**(行为零变化)。

**Slice 1 包含**:

1. **NuGet 引入**:`Dovetail` 包加到 `ISEStudio.csproj`(`ExtractionOrchestrator` 所在项目),版本 `1.0.0`(与本地 `E:\GitHub\Dovetail\Dovetail.csproj` `<Version>1.0.0</Version>` + NuGet tag 一致):
   ```xml
   <PackageReference Include="Dovetail" Version="1.0.0" />
   ```
   Dovetail 是 Roslyn source generator(`IsRoslynComponent=true`),NuGet 包路径 `analyzers/dotnet/cs`,会被自动加载到编译过程,无 `AddPipelines()` 之外的手动注册需求。若后续想跟进 `E:\GitHub\Dovetail` 本地修改,可临时切到 `<ProjectReference Include="..\..\..\E\GitHub\Dovetail\Dovetail\Dovetail.csproj" />`,但 NuGet 包写法是 Slice 1 默认
2. **DI 注册**:在 `ExtractionServiceCollectionExtensions` 加 `services.AddPipelines()`(仅当 `TBoxVerifyService` 等注册时)
3. **薄壳 Segment 类**(新建,不动现有三个 Service):
   - `TBoxChunkPipeline`(partial,`IPipeline<TBoxChunkInput, TBoxVerifyResult>`):
     - 构造参数:`[Segment] CriticStep`、`[Segment] AdjudicatorStep`、`[Segment] DenotationStep`、`[Segment] MergeStep`
   - `TBoxJobPipeline`(partial,`IPipeline<TBoxJobInput, TBoxJobResult>`):
     - 构造参数:`[Segment] ChunkStep`(pipeline-as-segment)、`[Segment] CorpusRecoveryStep`、`[Segment] HierarchyRecoveryStep`、`[Segment] JobMergeStep`
4. **薄壳段实现**:每段做一件事 — 调用现有 Service 的对应方法,不重写业务逻辑:
   - `CriticStep` 调用 `TBoxVerifyService` 内部 critic 部分(把现在 VerifyAsync 里第 1 步抽成 `RunCriticAsync(chat, text, delta, ct)`,内部仍调现有 `CallAsync(BoundaryCriticKey)`)
   - `AdjudicatorStep` 包 try/catch fail-soft
   - `DenotationStep` 调现有 `VerifyClassDenotationsAsync` 等价物
   - `MergeStep` 是纯函数段(把 CriticOutput + AdjudicatorOutput + DenotationOutput 合成 `TBoxVerifyResult`,逻辑等价于现有 VerifyAsync 末尾的合成)
5. **可选服务包装**:`OptionalSegment<TIn, TOut>(inner, isEnabled)` — 若服务未注册(`isEnabled = false`),段直接返回 `TOut` 的"空"实例,等同于现有 null 检查分支
6. **`NoOpSegment`**:`IPipelineSegment<TIn, TOut>` 永远返回 `TOut` 默认值,用于占位
7. **`RunWithExtractionGuardAsync` 适配**:`GuardedSegment<TIn, TOut>(inner, guard)` — 把 409 job_id envelope 守护包在段外(详见 §7)。**注**:本 slice 内只在 pipeline 顶层包装一次,不破坏现有 `RunLayerAsync` 内的 409 调用点
8. **测试**:
   - 现有 TBox 单测全绿(行为零变化)
   - 新增 3-5 个 Dovetail 自身的诊断测试:`[Fact]` 验证 Mermaid doc comment 存在 + 段注册齐全
   - `dovetail-report` 工具生成 TBox 子 DAG HTML 报告,提交到 `docs/superpowers/diagrams/extraction-tbox-dag.html`

**Slice 1 不包含**(留到后续 slice):

- ABox / ConflictAgent / StructureAgent / Vocabulary 的 Dovetail 化
- 顶层 5 runner 并行的 Dovetail 化(暂留 `ExtractionOrchestrator.RunJobSafelyAsync` 手写)
- 抽出 `TBoxVerifyService` 的 critic/adjudicator/denotation 三个独立 public 方法(本 slice 只在内部抽出 `internal` 方法,不改 public 签名)
- `dovetail-report` 接入 CI(后续 slice)

---

## 5. 后续 Slice 路线图(规划)

| Slice | 范围 | 估计切片数 |
|---|---|---|
| **2** | ABox 子 DAG(merge_classes → jaccard → embedding → LLM judge → cascade retype) | 4-6 |
| **3** | ConflictAgent + StructureAgent(p1-1 + p1-3) | 3-4 |
| **4** | Vocabulary 流水线(SyncCore 四遍 + Proposals 排入) | 2-3 |
| **5** | 顶层 5 runner 调度(`ExtractionOrchestrator.RunJobSafelyAsync`) | 2-3 → **8**(2026-08-30 ✅ DONE,commits `2376e8a`→`7a39790`,tests 972→1001/0/1/1002) |
| **6** | `dovetail-report` 接入 CI + 跨 slice 一致性 lint | 1 |

**Slice 5 完成总结**(2026-08-30,详见各 slice 子 spec + memory file):

- **6 LOCKED Rulings**:R7 canonical chain / R8 无 step variants / R11 4-field JobState / R13 pipeline shape / R15 open-generic DI / R18 无 GuardedSegment
- **DOVE017 fix**:6 wrapper records in `JobCarries.cs`(mirrors Slice 4 v1.2 `TerminologyCarries.cs` pattern)
- **DOVE008 fix**:`ChainAdapter<TIn, T1, TOut>` + `NoOpSegment<TIn, T1, TOut>` 3-arity adapter 层保留 2-arity step authoring surface
- **零生产行为变更**:`JobRunContext` mutable struct → `JobState` immutable record 17 字段 行为等价 CombinedRunnerAsync,972+46 baseline 零改全绿
- **5 PARKED items**:详见 `memory/ontopilot-extraction-dovetail-slice5.md`
- **Slice 5 spec 升 v1.0 → v1.1**(commit `7a39790`):6 corrections(§3.1 pipeline shape / §4.2 13→17 fields / §4.5 类型引用 / §5.2 DOVE017 ruling / §6.1 DI block / §3.1 canonical chain)

### Slice 6 范围(规划中)

`dovetail-report` 接入 CI(每个 PR 自动生成 pipeline HTML 报告上传 artifact)+ 跨 slice 一致性 lint(5 个 pipeline 命名约定 / DI 注册块结构 / JobCarries wrapper pattern 复用检查)。预估 1 slice。

每个 slice 独立成 plan,独立 PR,独立 gate(单测 + 集成 + 868 unit baseline)。

---

## 6. 关键设计决策

### D1:薄壳包装而非改造现有服务

**Why**:`TBoxVerifyService` / `CorpusRecoveryService` / `HierarchyRecoveryService` 已经 540+ 行,经过 P1-5a / P1-5b 单元/集成测试密集验证。薄壳包装 = 现有 Service 不改,只把段间控制流抽到 Dovetail,行为零变化的概率最高,回归测试直接复用现有 ~46 个 TBox 相关 unit tests。

**What if 不**:直接把三个 Service 改造成 Segment,会触发大量 DOVE 诊断错误,且现有 static helpers(`ApplyTBoxRoleDecisions` 等)要从 internal static 改造成 partial,风险叠加。

### D2:adjudicator + denotation 暂时保留顺序,不改并发

**Why**:现有 `VerifyAsync` 内顺序 await 是 Python parity(`extract.py:1171-1178` 顺序)的 .NET 移植。adjudicator / denotation 虽消费同一 critic output,**但 adjudicator 失败的 fail-soft 路径会直接 skip denotation 的 fallback 分支**(见 `TBoxVerifyService.cs:159-168`)。改成 Dovetail 并发后,denotation 可能先于 adjudicator 完成,逻辑分支会变。**第一个 slice 保守保留顺序**,后续 slice 单独评估并发化收益。

**实现方式**:adjudicator 段吃 critic output;denotation 段也吃 critic output;**Merge 段**顺序处理 adjudicator fallback 再处理 denotation,等价于当前 VerifyAsync 末尾合成(`TBoxVerifyService.cs:181-200`)。

### D3:`IPipelineSegment<...>` 8 输入上限 + 段间 record 聚合

**Why**:Dovetail `IPipelineSegment` 最多 8 输入,而 TBox 子 DAG 中间产物(per-chunk rejections + per-chunk accepted norms + candidateClasses)可能很多字段。如果用 primitive list 做段输出,会撞 8 输入上限。统一约定:**每段产出一个 record(`CriticOutput` / `AdjudicatorOutput` / `DenotationOutput`),下个段吃整个 record**。record 内字段不限。

### D4:`OptionalSegment` 替代 null 检查

**Why**:现有 `TBoxVerifyService?` / `CorpusRecoveryService?` / `HierarchyRecoveryService?` null 在 hand-built 测试里走 skip 分支。改成 Dovetail 后,段是编译期注册的,无法运行时判 null。用一个 `OptionalSegment` 包装:Dovetail `AddPipelines()` 注册段时,如果 `OnToPilotOptions.UseTBoxVerify` 为 false,就注册 `NoOpSegment` 替代;为 true 就注册真段。**包装在 DI 扩展层做,不是段本身做**。

### D5:`FailSoftSegment` 统一异常 → fallback

**Why**:Dovetail 没有"可选段"概念(README §Exception Handling)。adjudicator 现有的 fail-soft(`catch { return fallback }`)需要从 Service 内部提到段层。`FailSoftSegment<TIn, TOut>(inner, fallbackFactory)` 统一包;adjudicator 调用失败 → 返回空 `AdjudicatorOutput`,下游 Merge 段看到空输出走 fallback 分支,等价于现有 Python 行为(`extract.py:1171-1175`)。

### D6:`GuardedSegment` 在顶层而非每段

**Why**:`RunWithExtractionGuardAsync` 是 job 级 409 envelope 守护,每 chunk 一次,不应每个段都包。pipeline 顶层(`TBoxJobPipeline.ExecuteAsync` 入口处)包一次即可,段内不再判 409 — 段失败由 Dovetail 异常 → `GuardedSegment` 兜底转 409 envelope。这与现有 `RunLayerAsync` 内"409 envelope 在 phase 切换时一次"的语义一致。

### D7:不做 pipeline-as-segment 的双重实现

**Why**:`TBoxJobPipeline.ChunkStep` 用 `TBoxChunkPipeline` 作 segment 是 pipeline-as-segment(README §Pipelines-as-Segments),两层 pipeline 都实现 `IPipelineSegment<...>` 等价。但**`HierarchyRecoveryService.RecoverAsync` 内部也会调 `_verify.VerifyAsync`**,这条调用链改造时如何处理?选项:
- (a) `HierarchyRecoveryStep` 段内部直接调用现有 `TBoxVerifyService.VerifyAsync`,不走 `TBoxChunkPipeline`(语义不变,**最简单**)
- (b) `HierarchyRecoveryStep` 内嵌一个 `TBoxChunkPipeline`,走 Dovetail

选 (a)。**WHY**:HierarchyRecovery 里的 `VerifyAsync` 是"对 proposed classes 跑一遍 verify",语义上是子调用,但**输入数据形态不一样**(不是 chunk + delta,是 filteredClasses + text)。强行套 `TBoxChunkPipeline` 需要再造一层输入转换段,得不偿失。**第一个 slice 走 (a)**;若后续发现重用收益更大再重构。

### D8:不留"先删 TBoxVerifyService 重写"的版本

**Why**:服务与薄壳段并存,长期看冗余。但删服务需要把 `ApplyTBoxRoleDecisions` 等 static helper 也搬到段里,且 unit test fixture 大改。第一个 slice **不删**;在 Slice 2(ABox 子 DAG)完成后,做一次"清理 sweep",把无引用的旧 API 标记 `[Obsolete]`,下一 major 版本删。

---

## 7. 失败模型与适配层

### 7.1 三类适配段

```csharp
// FailSoftSegment — 包 try/catch,失败返回 fallback
public sealed class FailSoftSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut> inner,
    Func<TIn, TOut> fallbackFactory,
    ILogger<FailSoftSegment<TIn, TOut>> logger)
    : IPipelineSegment<TIn, TOut>
{
    public async Task<TOut> ExecuteAsync(TIn input, CancellationToken ct)
    {
        try { return await inner.ExecuteAsync(input, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Segment failed fail-soft");
            return fallbackFactory(input);
        }
    }
}

// OptionalSegment — 编译期决定走 inner 还是 NoOp
public sealed class OptionalSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut>? inner,
    Func<TIn, TOut> noOpFactory)
    : IPipelineSegment<TIn, TOut>
{
    public Task<TOut> ExecuteAsync(TIn input, CancellationToken ct) =>
        inner is null
            ? Task.FromResult(noOpFactory(input))
            : inner.ExecuteAsync(input, ct);
}

// GuardedSegment — job 级 409 envelope
public sealed class GuardedSegment<TIn, TOut>(
    IPipelineSegment<TIn, TOut> inner,
    IExtractionJobStore jobs,
    Guid jobId,
    Func<TIn, TOut> conflictEnvelope)
    : IPipelineSegment<TIn, TOut>
{
    public Task<TOut> ExecuteAsync(TIn input, CancellationToken ct) =>
        RunWithExtractionGuardAsync(jobId, jobs, () => inner.ExecuteAsync(input, ct),
            conflictEnvelope, ct);
}

// NoOpSegment — 占位,返空
public sealed class NoOpSegment<TIn, TOut> : IPipelineSegment<TIn, TOut>
{
    private readonly Func<TIn, TOut> _factory;
    public NoOpSegment(Func<TIn, TOut> factory) => _factory = factory;
    public Task<TOut> ExecuteAsync(TIn input, CancellationToken ct) => Task.FromResult(_factory(input));
}
```

### 7.2 错误处理原则

- **Dovetail 异常传播**:Pipeline 中任一段抛 → 整条 pipeline 失败 → 由 `GuardedSegment` 顶层兜底转 409 envelope(若 job 已被并发请求抢占)或重抛
- **`OperationCanceledException`**:**不**被 `FailSoftSegment` 吞;Dovetail README §Exception Handling 明确禁止
- **LLM 调用失败**:由 Service 内 `CallAsync` 现有 try/catch 掩护(已是现状),不再重复包装
- **DOVE 编译错误**:**不**靠 try/catch 绕;段类型 / 接口写错就让 build fail

### 7.3 409 envelope 与 Dovetail 关系

`RunWithExtractionGuardAsync(jobId, jobs, work, conflictEnvelope, ct)` 是现有 helper,接受一段 work func 与冲突时返回的 envelope。**`GuardedSegment` 把它转成段级别包装**:
- 段成功 → 透传
- 段抛 → 调 `RunWithExtractionGuardAsync` 看是否 409 抢占;若是 → 返回 `conflictEnvelope(input)`;否则重抛

---

## 8. 文件结构与类型清单

### 8.1 新建文件

```
src/ISEStudio/Extraction/
├── Dovetail/
│   ├── Adapters/
│   │   ├── FailSoftSegment.cs        // 薄壳 ~50 行
│   │   ├── OptionalSegment.cs        // 薄壳 ~30 行
│   │   ├── GuardedSegment.cs         // 薄壳 ~60 行
│   │   └── NoOpSegment.cs            // 薄壳 ~20 行
│   ├── TBox/
│   │   ├── TBoxChunkPipeline.cs      // partial, 4 [Segment]
│   │   ├── TBoxJobPipeline.cs        // partial, 4 [Segment]
│   │   ├── TBoxChunkInputs.cs        // record: TBoxChunkInput, CriticOutput, AdjudicatorOutput, DenotationOutput, TBoxJobInput, TBoxJobResult
│   │   ├── Steps/
│   │   │   ├── CriticStep.cs         // 内部调 TBoxVerifyService.RunCriticAsync
│   │   │   ├── AdjudicatorStep.cs    // 内部调 TBoxVerifyService.RunAdjudicatorAsync, fail-soft
│   │   │   ├── DenotationStep.cs     // 内部调 TBoxVerifyService.RunDenotationAsync
│   │   │   ├── ChunkMergeStep.cs     // 纯函数,合成 TBoxVerifyResult
│   │   │   ├── ChunkPipelineStep.cs  // pipeline-as-segment, TBoxChunkPipeline.ExecuteAsync
│   │   │   ├── CorpusRecoveryStep.cs // 内部调 CorpusRecoveryService.RecoverAsync
│   │   │   ├── HierarchyRecoveryStep.cs // 内部调 HierarchyRecoveryService.RecoverAsync
│   │   │   └── JobMergeStep.cs       // 纯函数,合成 TBoxJobResult
│   └── DovetailPipelineRegistrations.cs // IServiceCollection 扩展,根据 options 选 NoOp/真段
└── ExtractionServiceCollectionExtensions.cs (改) // 加 AddPipelines + DovetailPipelineRegistrations
```

### 8.2 现有文件改动

```
src/ISEStudio/Extraction/
├── ExtractionOrchestrator.cs        // 不动(Slice 5 再动);仅可能动 RunLayerAsync(TBox) 让其调 TBoxJobPipeline.ExecuteAsync
├── TBoxVerifyService.cs             // 抽出 internal 方法:RunCriticAsync / RunAdjudicatorAsync / RunDenotationAsync / VerifyClassDenotationsAsync(改 private → internal)
├── CorpusRecoveryService.cs         // 不动 public 签名,Service 类不改
└── HierarchyRecoveryService.cs      // 不动 public 签名,Service 类不改
```

`TBoxVerifyService` 改造细节(审慎,只暴露 internal):

```csharp
// 现有:private async Task<TBoxVerifyResult> VerifyClassDenotationsAsync(...)
// 改成 internal,允许 Step 调
internal async Task<TBoxVerifyResult> VerifyClassDenotationsAsync(...)

// 新增 internal,允许 Step 分别调:
// - RunCriticAsync(chat, text, delta, ct) → CriticOutput(delta + acceptedNorms + rejectedNorms + criticPayload)
// - RunAdjudicatorAsync(chat, text, originalDelta, criticOutput, ct) → AdjudicatorOutput(recovered + failures)
// - RunDenotationAsync(chat, text, delta, eligibleNorms, ctx, ct) → DenotationOutput(acceptedClasses + replacements + recoveries)
```

### 8.3 类型清单(完整)

```csharp
// 输入/输出 record(全部 sealed record,immutable)
public sealed record TBoxChunkInput(int ChunkId, string Text, TBoxDelta Delta, IChatClient Chat);
public sealed record CriticOutput(
    TBoxDelta VerifiedDelta,
    IReadOnlySet<string> AcceptedNorms,
    IReadOnlyList<RejectedClass> Rejections,
    IReadOnlyList<RejectedClass> CriticRejections);
public sealed record AdjudicatorOutput(
    IReadOnlyList<ClassMutation> Recovered,
    bool Succeeded);
public sealed record DenotationOutput(
    TBoxDelta VerifiedDelta,
    IReadOnlyList<RejectedClass> Rejections,
    IReadOnlyList<RecoveredClass> Recoveries);
public sealed record TBoxJobInput(
    Guid JobId,
    IReadOnlyList<TBoxVerifyResult> ChunkResults,
    IReadOnlyList<CorpusRecoveryChunk> PerChunkRejections,
    IReadOnlyList<string> FinalClassVocabulary,
    IChatClient Chat);
public sealed record CorpusRecoverySegmentOutput(
    CorpusRecoveryResult Result,
    bool Enabled);
public sealed record HierarchyRecoverySegmentOutput(
    HierarchyRecoveryResult Result,
    bool Enabled);
public sealed record TBoxJobResult(
    IReadOnlyList<TBoxVerifyResult> ChunkResults,
    CorpusRecoveryResult Corpus,
    HierarchyRecoveryResult Hierarchy);
```

---

## 9. 测试策略

### 9.1 现有测试不变

- `TBoxVerifyServiceTests`(现有 ~21 个 unit test,行为零变化)
- `CorpusRecoveryServiceTests`(现有 unit test)
- `HierarchyRecoveryServiceTests`(现有 unit test)
- 集成测试:`ExtractionJobStore` / `ExtractionOrchestrator` 端到端(PG testcontainers,Slice 1 内不重跑)

### 9.2 新增测试(Slice 1)

| 测试 | 验证 |
|---|---|
| `TBoxChunkPipelineSchemaTests.HasExpectedMermaidDocComment` | Dovetail 生成的 ExecuteAsync doc comment 含 Mermaid `flowchart TD` 块 |
| `TBoxChunkPipelineSchemaTests.AllStepsRegistered` | 4 个 step 都被 DI 注册(用 `AddPipelines()` 后 `GetServices<IPipelineSegment<...>>()` 计数) |
| `TBoxChunkPipelineExecutionTests.HappyPath` | 输入 `(chunk, delta, chat)`,跑 `pipeline.ExecuteAsync`,输出 `TBoxVerifyResult` 与现有 `TBoxVerifyService.VerifyAsync` 等价 |
| `TBoxChunkPipelineExecutionTests.AdjudicatorFailureFailsSoft` | 注入一个抛异常的 adjudicator mock,验证 pipeline 不抛,adjudicator output.Succeeded=false |
| `TBoxJobPipelineExecutionTests.HappyPath` | 输入 `(jobId, chunkResults, perChunkRejections, vocab, chat)`,输出 `TBoxJobResult` 与现有手写顺序等价 |
| `OptionalSegmentTests.NullInnerReturnsNoOp` | 验证 null inner → noOpFactory 调用 |
| `FailSoftSegmentTests.ExceptionTriggersFallback` | 验证 inner 抛 → fallback 返回,不传播 |
| `GuardedSegmentTests.JobAlreadyRunningReturnsConflictEnvelope` | 模拟并发抢占,验证 409 envelope 返回 |

### 9.3 可视化产物

- `dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams/extraction-tbox-dag.html`
- 提交 HTML 报告作为 Slice 1 的产物(visual snapshot)

### 9.4 Gate

- `dotnet test --no-restore` ≥ 868 / 0 fail / 1 skip(当前基线)
- `dotnet build` 无 DOVE 编译错误
- 集成测试(PG testcontainers)全绿
- `pnpm test` / `pnpm lint` / `pnpm build` 0 error(本 slice 不动前端,但跑一遍确保无副作用)

---

## 10. 风险与回退

| 风险 | 缓解 | 回退 |
|---|---|---|
| Dovetail 包与现有 NuGet 不兼容 | 引入前先 `dotnet restore`,看 NU1605 / NU1700 | 若冲突,固定 Dovetail 版本;或用 `<PackageReference Include="Dovetail" Version="x.y.z" />` 锁版本 |
| DOVE 编译错误漏报导致运行时 NRE | 编译期 DOVE001-020 已经覆盖主要错;但 segment 多于 8 输入等边界错可能漏 → 严格 unit test + 集成测试双层 | 若运行时炸,先把对应段切成 `IPipelineSegment<TIn, TOut>` 8 输入以内的最简形态,再排查 |
| 现有 868 unit test 中有 1-2 个 flaky 行为变化 | thin-shell 不改业务逻辑,行为等价 | 若某个 test 因 Dovetail 并发/异常顺序变化失败,**先确认是 behavior 漂移还是测试过度严格**;前者改 test,后者改 segment |
| Dovetail 是新依赖,长期维护负担 | 本地 `E:\GitHub\Dovetail` + NuGet 同源,可独立升级 | 若 Dovetail 上游停更,薄壳段可平滑迁回手写编排(段本身无依赖) |
| 第一个 slice 推迟会拖延后续 slice | Slice 1 限定范围明确,且 unit test 全绿作为硬 gate | 若 Slice 1 卡住,可砍掉 `OptionalSegment` / `FailSoftSegment`,只保留 `GuardedSegment`,最小可走通 |

---

## 11. 决策日志

- **D1 薄壳包装**(2026-08-28):保留现有 Service,新建 Segment 包装层,行为零变化
- **D2 顺序保留**(2026-08-28):adjudicator + denotation 第一个 slice 不并发,等 Slice 2 评估
- **D3 record 聚合**(2026-08-28):段间数据走 record,不撞 8 输入上限
- **D4 OptionalSegment**(2026-08-28):DI 层选 NoOp vs 真段,不用运行时 null 检查
- **D5 FailSoftSegment**(2026-08-28):fail-soft 提到段层,统一包装
- **D6 GuardedSegment 顶层**(2026-08-28):409 envelope 只在 pipeline 入口包一次
- **D7 (a) 不双重实现**(2026-08-28):HierarchyRecovery 内 VerifyAsync 走 Service 直调,不走 pipeline-as-segment
- **D8 不删旧 Service**(2026-08-28):薄壳 + Service 并存,后续清理
- **可重排 = 改代码**(用户拍板,2026-08-28):接受 Dovetail 编译期 DAG 限制,重排路径 = 重编译
- **第一个 slice = TBox 子 DAG 最小样例**(用户拍板,2026-08-28)
- **D9 备选 MAAF 评估 + 混合方案**(2026-08-28,用户追问触发):Dovetail 与 MAAF 语义不兼容(in-process 秒级 vs 长跑可暂停);三种混合路径 A/B/C 中 A/B 污染对方语义,C(DB 状态协调)唯一可行;当前抽取流水线无 HIL 需求,无需混合;未来 HIL 路径推荐 C 不引 MAAF;若坚持用 MAAF 做 reviewer,只在一个边界用,独立 subsystem 与 Dovetail pipeline 通过 DB 协作,无运行时交集(详见 §3.考虑过的备选.混合使用评估)
- **D12 Slice 5 完成**(2026-08-30):Dovetail extraction pipeline refactor 5-slice roadmap 闭环。详见各 slice 子 spec v1.x + memory entries + spec §5 表格 + 本条完成总结。零生产行为变更,base 972 unit / 46 integration tests 零改全绿,5-slice 累计落地 47 commits。Slice 6(`dovetail-report` CI + 跨切片一致性 lint)规划中。

---

## 12. Spec 自审

### 12.1 Placeholder scan

- ✅ 无 TBD / TODO / "实现细节"占位符
- ✅ 每个新建类型签名明确(§8.3)
- ✅ 每个测试有具体断言(§9.2)

### 12.2 Internal consistency

- ✅ §4 Slice 1 范围与 §6 D1-D8 决策一致(都是薄壳 + 不动 Service)
- ✅ §8.1 文件清单与 §6 D3(record 聚合)一致
- ✅ §7 适配层与 §6 D4-D6 一致

### 12.3 Scope check

- ✅ 单个 plan 可落地(范围明确,TBox 子 DAG + 4 个适配段)
- ✅ 不涉及前端 / 数据库 / Docker

### 12.4 Ambiguity check

- §6 D2 "顺序保留"明确说"等价于现有 VerifyAsync 末尾合成" → 无歧义
- §7 适配段代码已写全 → 实施时无歧义
- §8.3 record 签名明确字段名与类型 → 无歧义

---

**Spec 状态**:待用户审核
**下一步**:用户批准后,invoke `superpowers:writing-plans` 写实施计划
