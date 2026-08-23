# P1-1: Conflict Agent wire-up（LLM 冲突分诊 recommendation attach）

**状态**: 已完成（实现 + 13 单元测试 + 全量回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**提交**: `1b4a95b`
**范围**: `src/OnToPilot/Conflicts/ConflictAgent.cs`（新）+ `PromptLocales.cs` + `OnToPilotOptions.cs` + `ConflictService.cs` + `ConflictServiceCollectionExtensions.cs` + `InternalOperationDispatcher.cs` + `FakeChat.cs`（测试设施）+ 1× 新测试文件

---

## 1. 背景

Python 后端在 `POST /{ks_id}/conflicts/detect` 里,于 `sync_conflicts` 之后调用 `asyncio.to_thread(conflict_agent.resolve_open_conflicts_bg, ks.id, None)` —— 一个 LLM agent 对 open 的 `duplicate` / `predicate_specialization` 冲突做分诊,把**推荐**写到 `payload.recommendation` 供人工一键确认。.NET 侧 slice 9 已完成冲突队列 CRUD 与 3 个 conflict op,但 agent 层完全缺失:detect 之后没有任何 LLM 分诊,`payload.recommendation` 永远不存在。

## 2. 根因（与 Python parity 对比）

| 缺口 | Python | .NET 移植前 |
|---|---|---|
| agent 服务 | `conflict_agent._resolve/_decide` | 无 |
| prompt | `prompt_config.register(key="conflict.resolution")` | `PromptLocales` 里是 `NotWired()` stub |
| 配置 gate | `settings.agentic_conflict_resolution`（默认 True）+ `conflict_agent_max_steps`（3） | `OnToPilotOptions` 无对应项 |
| wire-up | `conflicts.py` detect 里 sync 之后、`not extraction_active` 时调用 | `InvokeConflictDetectAsync` 只调 `DetectAsync` |
| 工具 | `retrieval.get_neighborhood`（`schema.build_view` 上查结构上下文） | 无（`SchemaBuilder.BuildView` 已具备全部所需数据） |

## 3. 决策

### 3.1 只移植可达行为：recommendation attach，不移植 auto-apply

Python `AUTO_APPLY_TYPES = set()` 为空集 → `_resolve` 的 auto-apply 分支（editor.apply_edit + 双 capture + audit 记录）**永远不可达**。.NET 端口同样只实现 recommendation 路径,auto-apply 分支在类注释里说明不移植。若未来 Python 打开 `AUTO_APPLY_TYPES`,auto-apply 需在此处补 `OntologyEditor.ApplyEditAsync` + TBox/ABox capture + `conflict.resolve` audit。

### 3.2 独立 scoped `ConflictAgent` 服务,dispatcher 里 detect 之后调用

- 服务放在 `OnToPilot.Conflicts`,构造依赖与 `ConflictService` 同模式（`IChatClientFactory` 必选,`StoreWrapper?`/`ExtractionJobStore?` 可选默认 null —— 契约测试工厂无 Oxigraph 时自然降级）。
- gate 全部在 `TriageAsync` 内部(镜像 Python 顺序):
  1. `AgenticConflictResolution == false` → 空列表（Python `resolve_open_conflicts_bg` 开头早退）。
  2. `_store is null` → 空列表（契约测试路径）。
  3. `ExtractionJobStore.FindActiveJobAsync` 非 null → 空列表（Python detect 端点的 `extraction_active` gate）。
  4. provider 解析失败 / chat client 构建失败 / 每轮 LLM 调用失败 → 全部吞掉,该冲突留给人工（Python `_decide` 的 except 语义）。
- dispatcher 的 `InvokeConflictDetectAsync` 在 `DetectAsync` 后 `await agent.TriageAsync(...)`,返回的 rows 仍是 triage 前快照 —— 与 Python 一致(recommendation 落库,下次 list/context 读才可见)。

### 3.3 ReAct 工具循环逐字对照 `_decide`

- user prompt 逐字: `Conflict type: {ctype}\n{title}: {detail}\nEntities: {labels}\n\nResolutions:\n- id="…": …\n\nInspect if needed, then finish.`
- 每轮回复必须是单 JSON 对象: `finish` → 返回 `{resolution, confidence, reason≤200}`;`get_neighborhood` → 查询后把 `get_neighborhood result:\n{json}` 追加为 user 消息继续;非法 JSON → `Reply with a single JSON object.`;未知 action → `Unknown action. Use get_neighborhood or finish.`。
- 轮次上限 `range(ConflictAgentMaxSteps)` —— **0 预算 = 0 轮**(不写 `Math.Max(1, …)`,避免改变 Python 语义)。
- `confidence` 接受 number 或 string(Python `float(...)`),解析失败回 0.0。
- resolution id 为 `skip` 或不在 payload.resolutions 里 → 跳过,不写任何东西。

### 3.4 `get_neighborhood` 用 `SchemaBuilder.BuildView`（不引 embedding）

Python `retrieval.get_neighborhood` 基于 `schema.build_view` 纯图查询（label 大小写不敏感或 IRI 匹配 class → superclasses/subclasses/disjoint/equivalent/properties_out/properties_in）。.NET `OntologyView`（Classes/ObjectProperties/DataProperties/Axioms）+ `PropertyView.Domain/DomainLabel/Range/RangeLabel` 已含全部所需字段,直接映射即可,**不需要** embedding 层。序列化用 `UnsafeRelaxedJsonEscaping` 对齐 Python `json.dumps(..., ensure_ascii=False)`（该 payload 不参与任何 signature 哈希,不存在 parity 风险）。

### 3.5 prompt 注册进 `PromptLocales`

`conflict.resolution` 从 `NotWired()` stub 换成真实 en/zh-CN 文案,en 逐字抄 Python `conflict_agent._SYSTEM`,zh-CN 逐字抄 `prompt_locales.py:333-344`。不动 `PromptCatalog`（它是 PromptService 的 MVP seed 目录,P0 建立的模式是 agent 直接消费 `PromptLocales`）。

### 3.6 `ConflictService` payload 解析助手升为 `internal static`

`ReadEntities` / `ReadResolutions` / `JsonElementToObject` 由 private 改为 `internal static`,agent 与 service 读同一份存储 payload,避免两份解析漂移。

### 3.7 `FakeChat` 增加 `CallMessages` 记录

测试设施增强(向后兼容):每次 `GetResponseAsync` 快照收到的消息列表,ReAct 测试据此断言第 N 轮会话内容(如工具结果注入)。

## 4. 实施

- `ConflictAgent.cs`（新,~430 行）: `PromptKey = "conflict.resolution"`、`AutoTypes = {duplicate, predicate_specialization}`、`ResolveSystemPrompt()`、`TriageAsync`、`DecideAsync`（ReAct 循环）、`Neighborhood`、`AttachRecommendation`（`{**payload, "recommendation": {resolution_id, reason, confidence}}` 保序合并）、`BuildProviderConfigAsync`（mirror `TerminologyAgent`）。
- `OnToPilotOptions.cs`: + `AgenticConflictResolution = true`、`ConflictAgentMaxSteps = 3`。
- `PromptLocales.cs`: registry + `ConflictResolutionEn`/`ConflictResolutionZhCn` 常量;类注释更新为 4 个 wired call-sites。
- `ConflictService.cs`: 3 个 payload 解析方法 → `internal static`。
- `ConflictServiceCollectionExtensions.cs`: + `AddScoped<ConflictAgent>()`。
- `InternalOperationDispatcher.cs`: `InvokeConflictDetectAsync` detect 后调 `TriageAsync`（含与 Python 行为一致性的注释）。

## 5. 验证

- **新增 13/13**: recommendation attach（含原 payload 键保留）、string confidence、skip/invalid 不动、结构冲突（cycle）不送 LLM、gate off 不调 LLM、malformed→finish 两轮恢复、get_neighborhood 工具轮（断言工具结果含 superclass/subclass/properties_out）、extraction active no-op（真实 `ExtractionJobStore` + pending job）、provider 缺失 no-op、client 构建失败 no-op、reason 截断 200、zh-CN prompt、0 轮预算。
- **全量**: 全 solution build 0 错误 0 警告;`OnToPilot.Tests` 613/613（600 旧 + 13 新）;`OnToPilot.ApiContract.Tests` 167/167。
- 前端: recommendation 出现在 `payload.recommendation` 里,前端 `ConflictPanel` 消费该字段(与 Python wire 一致,无需改动)。

## 6. 遗留 / 不在本次范围

- **auto-apply 分支**（Python 死代码,§3.1）: `AUTO_APPLY_TYPES` 为空,未移植;含 `editor.apply_edit` + TBox/ABox 双 capture + `conflict.resolve` audit 记录。
- **`conflict.duplicate_judge`**: 语义 duplicate 检测（embedding cosine + LLM judge）在 `ConflictDetection` 里仍为 deferred —— .NET 从不产生 `duplicate` 类型冲突,所以 agent 的 duplicate 分支目前只在 seed/导入数据上可达。`conflict.duplicate_judge` prompt 保持 stub。
- **`structure_agent.attach_isolated_bg`**: Python detect 端点在 conflict agent 之后还调用孤立实体附加 agent(`tbox.structure_repair` prompt),属独立缺口,另行跟踪。
- Python `settings.conflict_auto_apply_floor`(0.85) 未移植 —— 仅 auto-apply 分支使用,该分支不可达。

## 7. 参考

- [[2026-08-23-p0-captive-dep-and-a11y]] — §5 P1 缺口登记
- `backend/app/ontology/conflict_agent.py` — `_SYSTEM` / `_decide` / `_resolve` / `resolve_open_conflicts_bg`
- `backend/app/api/conflicts.py` — detect 端点 wire-up 点
- `backend/app/ontology/retrieval.py::get_neighborhood` — 工具语义
- `backend/app/config.py:116-120` — `agentic_conflict_resolution` / `conflict_auto_apply_floor` / `conflict_agent_max_steps`
- `backend/app/prompt_locales.py:333-344` — zh-CN 文案
- `src/OnToPilot/Conflicts/ConflictAgent.cs` / `src/OnToPilot.Tests/Conflicts/ConflictAgentTests.cs`
