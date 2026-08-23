# P1-4: Extraction 管线 agent 链 wire-up（conflicts sync → conflict agent → structure agent）

**状态**: 已完成（实现 + 6 单元测试 + 全量回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**提交**: `5594371`
**范围**: `ExtractionEnums.cs` + `ExtractionOrchestrator.cs` + `ConflictAgent.cs` + `StructureAgent.cs` + 1× 新测试文件

---

## 1. 背景

Python 的 extraction 管线在 TBox 层提交之后跑一段 agent 链（`backend/app/api/extraction.py:325-344` TBox-only / `:541-558` combined）：

1. `job.phase = "conflicts"` → `_sync_conflicts_bg`（`sync_conflicts(session, ks)`，semantic=True）→ 检测 + upsert 冲突队列；
2. `conflict_agent.resolve_open_conflicts_bg(ks_id, model)` → 对 open duplicate / predicate_specialization 冲突附加 LLM 推荐；
3. `job.phase = "structure"` → `structure_agent.attach_isolated_bg(ks_id, model)` → 孤立类自动附加；
4. `refresh_ks_stats(session, ks)` → agents 可能 merge/加类，重算缓存计数。

combined 管线把这段放在 TBox 与 ABox 之间（谓词 merge 要作用于空 ABox；结构 agent 附加的类要供 ABox typing）。P1-1 / P1-3 交付了两个 agent 本体和 detect 端点接线，但 .NET `ExtractionOrchestrator` 的 TBox 层之后直接进 terminology / ABox——链完全缺失。

## 2. 根因（与 Python parity 对比）

| 缺口 | Python | .NET 移植前 |
|---|---|---|
| phase 枚举 | `job.phase = "conflicts" / "structure"` | `ExtractionPhase` 只有 TBox/ABox/Terminology/Finalizing |
| conflicts sync | `_sync_conflicts_bg`（默认 semantic=True） | `ConflictService.DetectAsync` 已有（semantic 参数被 .NET 检测器忽略，见 §3.3），未在管线调用 |
| conflict agent | `resolve_open_conflicts_bg(ks_id, model)` —— **无 extraction_active gate**（gate 只在 detect 端点） | `ConflictAgent.TriageAsync` 内建 `FindActiveJobAsync` gate（为 detect 端点设计）→ 管线内调用会被 job 自己的 running 行误杀 |
| structure agent | `attach_isolated_bg(ks_id, model)` —— 同样无 extraction_active gate | `StructureAgent.AttachIsolatedAsync` 内建同款 gate → 同样误杀 |
| model 透传 | 两个 agent 都收 job 的 model | `TriageAsync` 硬编码 `model: null` |
| stats 刷新 | `refresh_ks_stats`（:344/:558） | 仅 `ExtractionJobStore.MarkCompletedAsync` 完成时刷一次 |
| 服务解析 | Python bg 函数自开 Session | orchestrator 是 singleton，不能直接注入 scoped 的 ConflictService/ConflictAgent/StructureAgent/KnowledgeStatsService |

## 3. 决策

### 3.1 gate 语义：管线调用显式跳过 extraction-active gate

Python 的 gate 位置在 detect 端点（`not extraction_active`），两个 `*_bg` 函数本身不检查——extraction 管线调用它们时 job 自己就是 active。.NET 两个 agent 把 gate 内建了（为 detect 端点服务，P1-1/P1-3 决策）。为对齐 Python，给两个方法加尾参：

- `ConflictAgent.TriageAsync(Guid ksId, CancellationToken ct, string? model = null, bool skipActiveExtractionGate = false)`
- `StructureAgent.AttachIsolatedAsync(Guid ksId, string? model, CancellationToken ct, bool skipActiveExtractionGate = false)`

管线调用传 `skipActiveExtractionGate: true`；detect 端点保持默认 false。参数放在 ct 之后保证既有调用点（`InternalOperationDispatcher`）零改动。`model` 参数同时补齐 Python `resolve_open_conflicts_bg(ks_id, model)` 的透传（原 .NET 硬编码 null）。

### 3.2 服务解析：IServiceScopeFactory 可选 seam（ExtractionJobStore 先例）

orchestrator 是 singleton（`AddSingleton<ExtractionOrchestrator>`，后台 `Task.Run` 状态），链上的四个服务都是 scoped（共享请求 DbContext）。沿用 `ExtractionJobStore._scopes` 的既有模式：构造函数加可选 `IServiceScopeFactory? scopes = null`，生产 DI 自动注入（singleton 注入 singleton 合法）；手工 new 的测试 orchestrator 传 null → **链整体跳过**（既有 ExtractionStateTests 的相位断言不受影响，同时成为"无 scope 时跳过"的回归保护）。

链内 `using var scope = _scopes.CreateScope()`，一个 scope 解析全部四个服务——ConflictService 与 ConflictAgent 共享同一 scoped `OnToPilotDbContext`，所以 `DetectAsync` 刚写的 open 行对 `TriageAsync` 的查询立即可见（Python 同一 session 的语义）。

### 3.3 conflicts sync 用 DetectAsync（semantic 参数现状）

Python `_sync_conflicts_bg` 调 `sync_conflicts(session, ks)`（默认 semantic=True）。.NET `ConflictService.DetectAsync` 注释已声明"Mirrors sync_conflicts(session, ks, semantic=True)"，但其 `ConflictDetection.Detect(store, graphIri, semantic: true)` 的 semantic 参数**目前被忽略**（"Reserved for the deferred duplicate pass"——duplicate 语义检测是独立缺口，见 gap tracker）。管线调用 DetectAsync 不改变该现状：结构与 predspec 检测照跑，duplicate 检测仍待语义 pass。

### 3.4 相位历史与 structure 日志

Python 把 structure 日志追加进 `job.log`（自由文本：`"structure: A ⊑ B; C ⊑ D"`）。.NET 的 `ExtractionJobEntity.Log` 被既有决策用作逗号分隔的**相位历史**（`ExtractionJobLog`），自由文本不进列。因此链只追加 `conflicts` / `structure` 两个相位；agent 返回的详情日志不持久化（structure agent 已写 `tbox.attach_isolated` audit 行可追溯；conflict agent 的推荐落在 conflict payload）。Python 的 `.strip()`/`\n` 拼接无对应物。相位顺序与 Python 一致：TBox → conflicts → structure（→ ABox in combined）→ terminology → finalizing。

### 3.5 异常语义：job failed 但 TBox 层保留

Python 里 agent 段在 `cap.diff()`（capture 已提交）之后，任何异常 → `except` → job failed，图写入保留。.NET 相同：`RunAgentChainAsync` 不设自己的 catch，异常冒泡到 `RunJobSafelyAsync` → `MarkFailedAsync`；TBox capture 在 `RunLayerAsync` 返回 true 时已提交。链尾的 stats 刷新例外——best-effort（try/catch 吞），与 `MarkCompletedAsync` 的既有约定一致（Python 这里 `refresh_ks_stats` 失败会让 job failed，属 SQLite 锁时代的历史行为；.NET 让 stats 失败绝不倒灌 job，且完成时会再刷一次）。Python 的中间 `refresh_ks_stats`（:323，conflicts 之前那次）省略：conflict 检测不读 counters，无可见差异，ADR 记录。

### 3.6 combined 的 terminology 位置差异（既有决策，不在本切片）

Python combined 顺序：TBox → agents → terminology → ABox；.NET 既有实现把 terminology 放在两层之后。本切片只在 TBox 后插入 agent 链（Python 位置），不改 terminology 的既有位置——.NET 顺序变为 TBox → conflicts → structure → ABox → terminology → finalizing。差异与 ABox 无交互（terminology 写在词汇图，独立 capture），保留。

## 4. 实施

- `ExtractionEnums.cs`：+ `ExtractionPhase.Conflicts` / `Structure` + ToWire 映射。
- `ConflictAgent.cs`：`TriageAsync` + `model` / `skipActiveExtractionGate` 尾参；gate 条件 `_jobs is not null && !skipActiveExtractionGate`；`BuildProviderConfigAsync` 透传 model。
- `StructureAgent.cs`：`AttachIsolatedAsync` + `skipActiveExtractionGate` 尾参（gate 条件同上）。
- `ExtractionOrchestrator.cs`：
  - 构造 + `IServiceScopeFactory? scopes = null`（最后，可选）；
  - 新 `RunAgentChainAsync(ctx)`：scopes null → 直接返回；否则 UpdateProgressAsync(conflicts) → `DetectAsync` → `TriageAsync(skip: true)` → UpdateProgressAsync(structure) → `AttachIsolatedAsync(skip: true)` → best-effort `KnowledgeStatsService.RefreshAsync`；
  - `TBoxOnlyRunnerAsync`：RunLayerAsync 成功后、RunTerminologyAsync 前插入；
  - `CombinedRunnerAsync`：tboxOk 后、labels 刷新与 ABox 前插入（Python 位置）。
- `ExtractionAgentChainTests.cs`（新，6 测试）：fixture 复用 ExtractionStateTests 模式 + `ServiceCollection` 建 scope provider（scoped DbContext factory、store、job store、chat factory、两个 agent、ConflictService、KnowledgeStatsService、OntologyViewBuilder）。种子 TBox：Person/Dog/Cat + `trains Dog`/`trains Cat`（真实 predspec 家族，检测器可复现）+ 孤立类 Centrifugal Pump；chunk/文档文本含其标签供 evidence grounding。

## 5. 验证

- 新增 6/6：TBox 相位序列 + stats 计数（6 类 2 属性）；管线内 conflict agent 附加 recommendation（predspec 行断言 resolution_id/confidence）；管线内 structure agent 附加 subclass + `tbox.attach_isolated` audit 行（actor_name=structure-agent、actor_id=null）；combined 相位顺序（tbox,conflicts,structure,abox,terminology,finalizing）+ ABox 图写入；无 scope factory 时链跳过（相位与 conflicts 表双断言）；链异常 → job failed 但 TBox 层保留（类数 > 种子）。
- **全量**: 全 solution build 0 错误 0 警告；`OnToPilot.Tests` 630+6=636/636；`OnToPilot.ApiContract.Tests` 167/167。

## 6. 遗留 / 不在本次范围

- **duplicate 语义检测**（§3.3）：`ConflictDetection.Detect` 的 semantic pass（embedding cosine + LLM judge）仍未实现——`AutoTypes` 里的 duplicate 分支实际无数据源，与 P1-1 时一致。
- **extraction.run audit 行**：Python 管线写 `extraction.run` / `abox.extract` 审计行，.NET orchestrator 从未实现（B6b 切片范围外），与本切片无交互但同属 extraction 管线 parity 缺口。
- **`_verify_tbox_candidates` LLM 管线**、**`llm_concurrency()` 近似**：见 P1-3 ADR §6。

## 7. 参考

- [[2026-08-23-p1-1-conflict-agent]] — ConflictAgent 本体与 detect 端点接线
- [[2026-08-23-p1-3-structure-agent]] — StructureAgent 本体与 detect 端点接线（§3.6 预告本切片）
- `backend/app/api/extraction.py:325-344` / `:541-558` — Python 两个管线的 agent 链段
- `backend/app/ontology/conflict_agent.py:165-174` / `backend/app/ontology/structure_agent.py:152-162` — 两个 bg 函数的 gate 形状
- `src/OnToPilot/Extraction/ExtractionOrchestrator.cs` / `src/OnToPilot.Tests/Extraction/ExtractionAgentChainTests.cs`
