# P1-3: Structure Agent wire-up（孤立类自动附加 isolated-class attach）

**状态**: 已完成（实现 + 17 单元测试 + 全量回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**提交**: `38a6320`
**范围**: `src/OnToPilot/Ontology/StructureAgent.cs`（新）+ `PromptLocales.cs` + `OnToPilotOptions.cs` + `TBoxGuard.cs` + `OntologyServiceCollectionExtensions.cs` + `InternalOperationDispatcher.cs` + 1× 新测试文件

---

## 1. 背景

Python 后端在三个调用点运行 `structure_agent.attach_isolated_bg`：
1. `backend/app/api/conflicts.py:150` — detect 端点在 conflict agent 之后（同一个 `not extraction_active` gate 内）；
2. `backend/app/api/extraction.py:340` — TBox-only 抽取管线，conflicts sync + conflict agent 之后（`job.phase = "structure"`）；
3. `backend/app/api/extraction.py:554` — combined 抽取管线的 TBox 段，同样位置。

其职责：抽取后有些类"落单"（无父类、无子类、不是任何属性的 domain/range）。对每个孤立类，agent 基于来源摘录建议一个更宽泛父类（已有类或 new=true 的新一般类），对"置信度 ≥ 0.85 + 来源 grounded + 词法安全 + 非 catch-all"的建议自动 `subclass_of` 附加，其余留人工。.NET 侧 slice 9 / P1-1 已完成冲突队列与 conflict agent，但 structure agent 完全缺失，`tbox.structure_repair` prompt 仍是 `NotWired()` stub。

## 2. 根因（与 Python parity 对比）

| 缺口 | Python | .NET 移植前 |
|---|---|---|
| agent 服务 | `structure_agent._decide` / `attach_isolated_bg` | 无 |
| prompt | `prompt_config.register(key="tbox.structure_repair")` | `PromptLocales` 里是 `NotWired()` stub |
| 配置 gate | `settings.agentic_isolated_classes`（默认 True）+ `structure_max_same_parent`（5）+ `conflict_auto_apply_floor`（0.85，被 structure agent 复用） | `OnToPilotOptions` 无对应项 |
| wire-up | detect 端点 conflict agent 之后；extraction 管线 TBox 段之后 | 无 |
| 孤立类判定 | `_isolated`（`build_view` 的 superclasses / domain_members / range_members） | 无（`OntologyView` 已具备全部所需字段） |
| 附加写 | `store.capture(revert_on_error=True)` 包住 `editor.apply_edit`（add_class / add_axiom subclass）+ `cap.diff()` + `audit.record(actor_name="structure-agent")` | 无 |
| 边验证 | `_verified_source_edge` → `role_evidence.evidence_is_grounded` + `extract._verify_tbox_candidates`（LLM role-critic 管线） | `RoleEvidence.EvidenceIsGrounded` ✓；LLM critic 管线未移植 |

## 3. 决策

### 3.1 verified 降级：grounded + 词法规则替代 LLM critic 管线

Python `_verified_source_edge` 除 groundedness 检查外还调用 `extract._verify_tbox_candidates` —— TBox 抽取自己的多层 LLM 验证管线（`tbox.boundary.critic` / `tbox.boundary.adjudicator` / denotation check），.NET 侧整体未移植（7 个 boundary prompt 均为 stub，属独立的 TBox 验证缺口，见 gap tracker）。完整移植该管线超出本切片范围，故 verified 降级为：
1. `RoleEvidence.EvidenceIsGrounded(sourceText, evidence)`（与 Python 同一实现）；
2. `Guard.IsLexicallySafeSubclass(child, parent)` —— 复合词头规则（`Centrifugal Pump` ⊑ `Pump` 安全；`Centrifugal Pump` ⊑ `Machine` 不安全）。

降级是 **fail-closed**：词法不安全或 evidence 不 grounded 的边一律 left。代价是 Python 中"词法不安全但 LLM critic 确认"的边 .NET 会拒绝——结构 attach 只加边、不删数据，且可在人工 UI 补救，可接受。`_verify_tbox_candidates` 移植后，此处仅需把 `VerifiedSourceEdge` 换成该管线（见 §6）。

### 3.2 RDF 写：外层 capture + 直接 AddQuads，不走 OntologyEditor

Python `editor.apply_edit` 是裸函数（调用者负责 capture），而 .NET `OntologyEditor.ApplyEditAsync` 自带 capture —— 若外层再包 capture 会触发 `GraphWriteCoordinator` 的 `LockRecursionException` → 409（ABoxService 注释已有先例警告）。因此 StructureAgent 直接复刻 Python 的 RDF 形状：
- `add_class`：`rdf:type owl:Class` + `rdfs:label`（`Vocabulary.ClassNode(baseIri, label)`，与 `OntologyEditor.ApplyEditNoStore` 的 IRI 计算一致）；
- `add_axiom subclass`：`sub rdfs:subClassOf sup`（sub != sup 时）。

capture 语义注意：.NET `revertOnError: true` 是"dispose 时**无条件回滚**"（与 Python 相反），所以按 ABoxService/OntologyEditor 先例开 `revertOnError: false` + catch 里 `MarkError()`。diff 用 `DumpNQuads(pre) / DumpNQuads(post) / DiffNQuads`（ABoxService 模式）。

### 3.3 Pass 1 并发：Task.WhenAll + SemaphoreSlim(provider.ConcurrencyLimit)

Python 用 `ThreadPoolExecutor(max_workers=min(len(isolated), llm_concurrency()))` 并发提案、按提交顺序收集。.NET 用 `Task.WhenAll` 保序 + `SemaphoreSlim` 限流（上限取 provider 的 `ConcurrencyLimit`，`Math.Max(1, …)` 对齐 `max(1, workers)`）。测试用 `ConcurrencyLimit = 1` 保证 FakeChat 队列消费顺序确定。

### 3.4 audit 直写：`actor_id = null` 的 agent 行

Python `audit.record(actor_id=None, actor_name="structure-agent")` 无人类 actor。.NET `AuditLogService.RecordAsync` 强制 `UserEntity actor` 非 null，故 StructureAgent 注入 `LegacyIdAllocator` 直接 `AllocateAndPersistAsync(new AuditEventEntity { ActorId = null, ActorName = "structure-agent", ... })`（DocumentService 同款模式；`AuditEventEntity.ActorId` 为 nullable ✓）。detail 逐字对齐 Python：`{class, parent, new, reason, evidence, confidence, agent: true}`；空 diff 存 null（AuditLogService 约定）。

### 3.5 语义细节逐字对齐

- `_isolated`：无父（Superclasses 空）∧ 无子（无人以它为 superclass）∧ 不在任何属性的 DomainMembers/RangeMembers。
- user prompt 逐字（含 Python list repr `['A', 'B']`）。
- `bool(data.get("new"))` 的 Python 语义：非空字符串为真（`"false"` → true）、非零数为真——`ReadNewFlag` 复刻。
- `float(confidence or 0.0)`：number/string 均可，失败回 0.0；`str(...)` 转换复刻（`None` → `"None"`）。
- gate 顺序：`AgenticIsolatedClasses` → `_store null` → `FindActiveJobAsync`（detect 端点的 `not extraction_active`）→ provider 解析失败吞掉。
- Pass 2 过滤顺序：空 parent / conf < 0.85 / parent == child（同 norm）→ 同一 left 文案；catch-all（votes > `StructureMaxSameParent`）→ left；未 verified → left；`idx` miss 且 `new=false` → 静默 continue（不发明类）。
- 新建父类后同步内存 idx（`idx.class_by_norm` 对应物），后续 proposal 复用同一 IRI。
- 每条 audit 一个 capture（Python 一个 proposal 一个 `with store.capture`）。

### 3.6 detect 端点 wire；extraction 管线 defer

`InternalOperationDispatcher.InvokeConflictDetectAsync` 在 `TriageAsync` 之后调 `AttachIsolatedAsync`（与 Python conflicts.py:148-150 顺序一致，两个 agent 各自自守 extraction-active gate）。**extraction 管线的两个调用点（extraction.py:340/554）本轮不 wire**：.NET `ExtractionOrchestrator` 尚未移植 conflicts sync + agent 链段（`_sync_conflicts_bg` → conflict agent → structure agent → `job.phase = "structure"`），属独立缺口（§6），wire 时一并接入。

## 4. 实施

- `StructureAgent.cs`（新，~530 行）：`PromptKey = "tbox.structure_repair"`、`AttachIsolatedAsync`、`IsolatedClasses`、`DecideAsync`（单轮 JSON）、`VerifiedSourceEdge`、`ReadAsString/ReadNewFlag/ReadConfidence`（Python 语义）、`PythonListRepr`、`BuildProviderConfigAsync`（mirror ConflictAgent）。
- `OnToPilotOptions.cs`：+ `AgenticIsolatedClasses = true`、`AutoApplyFloor = 0.85`、`StructureMaxSameParent = 5`。
- `PromptLocales.cs`：`tbox.structure_repair` 从 stub 换为真实 en（逐字抄 `structure_agent._SYSTEM`）/ zh-CN（逐字抄 `prompt_locales.py:346-356`）；类注释更新为 5 个 wired call-sites。
- `TBoxGuard.cs`：`IsLexicallySafeSubclass` 升 `internal static`（供 StructureAgent 复用为词法验证）。
- `OntologyServiceCollectionExtensions.cs`：+ `AddScoped<StructureAgent>()`。
- `InternalOperationDispatcher.cs`：`InvokeConflictDetectAsync` detect → triage → attach（rows 仍是 pre-agent 快照，与 Python 一致）。

## 5. 验证

- **新增 17/17**：已有父类附加（audit 行断言 action/actor_name/actor_id=null/summary/detail/Added 字节）、new=true 建类附加、低置信 left、evidence 不 grounded left、catch-all（max_same_parent=1）left、无孤立类零 LLM 调用、gate off、extraction active no-op、provider 缺失 no-op、client 构建失败 no-op、malformed 回复 left、new=false 且父类不存在静默跳过、第二个 proposal 复用新建父类 IRI、parent == child left、reason 截断 200、zh-CN prompt、无 source 零调用。
- **全量**: 全 solution build 0 错误 0 警告；`OnToPilot.Tests` 613+17=630/630；`OnToPilot.ApiContract.Tests` 167/167。

## 6. 遗留 / 不在本次范围

- **`_verify_tbox_candidates` LLM 管线**（§3.1）：`tbox.boundary.critic` / `tbox.boundary.adjudicator` / denotation check 等 7 个 prompt 仍是 stub。这是 TBox 抽取验证层的独立缺口，移植后 `VerifiedSourceEdge` 应改挂该管线。
- **extraction 管线 agent 链 wire**（§3.6）：`ExtractionOrchestrator` 的 TBox 层完成后缺少 Python 的 conflicts sync → conflict agent → structure agent（`job.phase = "conflicts"/"structure"` + log 追加 + refresh_stats）段，属独立切片（依赖 P1-1 + P1-3 两个 agent）。
- Python `model_config.llm_concurrency()` 是 KS 级连接池上限；.NET 用 provider 的 `ConcurrencyLimit` 近似（语义差异仅影响并发度，不影响结果）。

## 7. 参考

- [[2026-08-23-p1-1-conflict-agent]] — 同 detect 端点链的前一个 agent，wire 模式与 gate 约定
- [[2026-08-23-p0-captive-dep-and-a11y]] — §5 P1 缺口登记
- `backend/app/ontology/structure_agent.py` — `_SYSTEM` / `_isolated` / `_decide` / `_verified_source_edge` / `attach_isolated_bg`
- `backend/app/ontology/editor.py` — `_add_class` / `_add_axiom` RDF 形状（`_ensure_labeled_class`）
- `backend/app/api/conflicts.py:137-155` / `backend/app/api/extraction.py:320-360` — 三个调用点
- `backend/app/config.py:119/125/129` — 三个 settings
- `src/OnToPilot/Ontology/StructureAgent.cs` / `src/OnToPilot.Tests/Ontology/StructureAgentTests.cs`
