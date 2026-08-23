# P1-5a: TBox verify 管线 wire-up（boundary critic → adjudicator → denotation）

**状态**: 已完成（实现 + 21 单元测试 + 全量回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `TBoxVerifyService.cs`（新）+ `PromptLocales.cs`（7 个 boundary prompt 双语落地）+ `ExtractionOrchestrator.cs` + `ExtractionServiceCollectionExtensions.cs` + `ExtractionDelta.cs`（TryReadObject 复用）+ 测试 harness（FakeChat + ExtractionRunApiTests）+ 1× 新测试文件

---

## 1. 背景

P1-3 ADR §6 登记的缺口：Python 的 extraction worker 在每个 chunk 上跑两段（`extract.py:1596-1597`）：

```python
onto = await _extract_for_chunk(text, model, graph_iri, use_agentic)
onto = await _verify_tbox_candidates(text, onto, model)
```

`_verify_tbox_candidates`（`extract.py:1069-1224`）是一条独立 role-critic 管线：boundary critic 对候选重判 → 有争议时 adjudicator 二次仲裁（fail-soft）→ denotation critic 按更严的 proper-name-vs-type 约定终审并做后缀恢复。.NET 侧 TBox 层提取完直接 merge，LLM 吐出的任意标签无条件进图——这是 P0 阶段就登记的 extraction 质量缺口（"7 个 boundary prompt 均为 stub"）。

## 2. 决策

### 2.1 切片边界：核心三段（P1-5a）与 corpus/hierarchy recovery（P1-5b）

Python 管线还有两个后续 pass：corpus recovery（`_recover_rejected_classes`，用 evidence_selector / corpus_recovery prompts）和 hierarchy recovery（`_recover_hierarchy_one` + `_verify_subclass_candidates`）。本切片只落 critic → adjudicator → denotation 主链；两个 recovery pass 注册为 P1-5b。prompt 目录方面 7 个 key 一次性全部落地（避免 P1-5b 再触碰 catalog），但只有前 3 个被消费。

### 2.2 决策应用：静态、无副作用、fail-closed（Python `_apply_tbox_role_decisions` 契约）

`ApplyTBoxRoleDecisions` 是纯函数：class 决策按 `skos.normalize_label`（NFKC + casefold + trim + 空白折叠，**保留标点**——与 `RoleEvidence.Normalize` 的"只取 \w+ 单词"刻意不同）为 key。class 存活条件逐条对齐 Python：

1. 决策存在且 `keep is True` —— **严格 JSON bool**（.NET `raw.ValueKind == JsonValueKind.True`；字符串 "true" 不满足 Python 的 identity 检查，同样拒绝）；
2. `role == "type"`；
3. `confidence >= settings.role_auto_accept_floor`（0.85，`OnToPilotOptions.AutoApplyFloor` 既有默认，与 Python 默认一致）；
4. `evidence_is_grounded(text, decision.evidence)`（RoleEvidence.EvidenceIsGrounded，min 4 字符 + 连续词序列匹配）；
5. `surface_is_grounded(text, label)`（词边界出现或规范化短语）；
6. `!exact_non_type || independent_type_evidence` —— exact_non_type 是 source 的 structured roles 含 literal 且不含 type；independent 要求 decision evidence 自身不是把该 label 列为 plain scalar。

rejection reason 顺序同 Python：exact-scalar 分支 → label 未落地 → decision.reason → "missing or ungrounded independent type decision"。subclass 决策按 (sub, super) norm 对为 key，需 keep + confidence + evidence grounding（无 role 检查，Python 同）。**properties 与 non-subclass axioms（disjoint/equivalent）无条件透传**——Python 的 `{**ontology, "subclass_of": ...}` spread 语义。

### 2.3 adjudicator fail-soft（Python `extract.py:1171-1175`）

只有 critic 拒了东西才调 adjudicator（disputed 非空）；adjudicator 异常被吞（除 OperationCanceledException 且已请求取消），直接进 denotation pass，候选 = 原始 classes、eligible = critic 接受集——denotation 不能复活 critic 拒绝的候选（Python 同款：adjudicator 失败时恢复通道整体失效，chunk 宁缺毋滥）。恢复的类在 denotation 之后 re-attach（`finalNorms` 去重）；critic 的 rejections 被 adjudicator 接受后不再报告（.NET 的 rejections = adjudicated.Rejections + denotated.Rejections）。

### 2.4 denotation pass（Python `_verify_class_denotations` :966-1067）

denotation critic 收到候选 + `provisionally_accepted` 标志（eligible 集）；其决策再走 `ApplyTBoxRoleDecisions`；accepted = checked ∩ eligible_norms；rejected_norms = 原始 − accepted；`RemoveRejectedClassReferences` 清掉引用被拒 label 的属性 domain/range 与 axioms；`DenotationReplacements` 恢复后缀替换——要求 source 在 rejected 集、source 决策 keep=false + role=individual、replacement 是 source 的空格分隔后缀、自身 evidence/label 落地、confidence ≥ floor。Python 的 `_role_recoveries` carry-over 分支（:1044-1048）在 .NET 流程中不可达（没有 caller 把带 recoveries 的 state 传入本 pass），未移植，doc 注明。

### 2.5 extractor evidence 缺口（已接受）

Python 把 extractor 的 `row["evidence"]` 带进 critic candidates（提示性上下文）。.NET 的 `ExtractionDeltaParser` 从未解析 evidence 字段（ClassMutation 无 evidence），因此 candidates payload 的 `extractor_evidence` 恒为空串。Python 的 evidence 只是提示、不是决策输入（critic 自引 source span），对决策结果无影响——保留现状，不在本切片扩 DTO。

### 2.6 接入形状：同一 capacity lease 内的 worker 序列

`ExtractAndVerifyAsync` 在 `RunLayerAsync` 的 chunk 循环里 extract → verify 连续执行，Python 同（同一个 `llm_concurrency` lease 内跑两段）。verify 为 null（手工构造的测试 orchestrator）时整段跳过——沿用 P1-4 `IServiceScopeFactory` 的可选 seam 模式。prompt snapshot：TBox 相位快照含 extractor prompt + 3 个被消费的 verify prompt（Python 记录 job 实际用到的每个 prompt）。

### 2.7 异常语义：verify 失败 = chunk 失败 = job failed

Python 中 `_verify_tbox_candidates` 抛异常 → worker 记录 chunk error，job 继续。.NET 的 `TBoxExtractionService.ExtractAsync` 既有约定是异常 → merger 不跑 → capture revert → job failed（fail-closed）。verify 沿袭该约定（同一个 try 域内），比 Python 更保守——verify 是质量闸门，静默丢弃不如显式失败；与既有 B6b 行为一致。

### 2.8 测试 harness：真 DI 路径必须给 verify 供料

`ExtractionRunApiTests` 走真实 `Program.cs` DI，verify 现在真实运行。FakeChat 新增 `VerifySourceText`（含 ValidTBoxDelta 全部标签与证据 span 的固定文本）、`VerifyCriticAcceptAll` / `VerifyDenotationAcceptAll`（keep 全部候选的决策载荷）与 `EnqueueVerifyAcceptAll()`（critic + denotation 两条回复）。受影响的两个 store 断言测试改用该序列 + blob 文本换成 `VerifySourceText`（grounding 检查通过）；其余 run 测试只断言 job 行/状态码，critic 拿到 "{}" 全拒不破坏断言（fail-closed 语义恰好成立）。

## 3. 实施

- `PromptLocales.cs`：7 个 key（`tbox.boundary.critic` / `tbox.boundary.adjudicator` / `tbox.denotation.critic` / `tbox.boundary.evidence_selector` / `tbox.boundary.corpus_recovery` / `tbox.hierarchy.critic` / `tbox.hierarchy.recovery`）从 `NotWired()` 改为真实双语（en/zh-CN，Python 逐字移植，来源注释）。
- `TBoxVerifyService.cs`（新，~650 行）：`VerifyAsync` 管线（无 candidates 早退零 LLM 调用）+ `ApplyTBoxRoleDecisions` / `RemoveRejectedClassReferences` / `DenotationReplacements` / `LabelNorm` 四个 internal static 决策函数 + `CallAsync`（`Llm.TBoxVerify.{Critic|Adjudicator|Denotation}` activity）+ snake_case wire DTO。critic 载荷无 extractor evidence（§2.5）。
- `ExtractionDelta.cs`：`TryReadObject` private → internal static（verify 复用 prose/fenced JSON 提取）。
- `ExtractionOrchestrator.cs`：`TBoxVerifyService? verify = null` 构造参数（在 `scopes` 前，两个可选）+ `ExtractAndVerifyAsync`（TBox-only 与 combined 两个 runner 共用）+ `BuildTBoxPromptSnapshot`（verify wired 时含 3 个 critic prompt）。
- `ExtractionServiceCollectionExtensions.cs`：`AddSingleton<TBoxVerifyService>()`。
- 测试：`TBoxVerifyServiceTests.cs`（新，21 测试）+ `FakeChat` 3 常量 1 helper + `ExtractionRunApiTests` 2 个入队序列 + 共享 blob 文本 + `ExtractionAgentChainTests` 构造参数命名化（`verify: null, scopes: scopes`）。

## 4. 验证

- 新增 21/21：决策函数纯测试（严格 bool / reason 优先级 / grounding / floor / exact-scalar / subclass / 透传 axioms / 引用清理 / 后缀规则），管线测试（早退零调用 / 全接受 / adjudicator 恢复 / adjudicator 异常 fail-soft / denotation 拒绝清理引用 / 后缀恢复 / 空 payload 全拒），orchestrator 端到端（真 StoreWrapper + SQLite + blob，extract → critic → denotation 3 次调用、3 类入库、snapshot 含 3 个 verify prompt）。
- **全量**: `OnToPilot.Tests` 全绿（含修复后的 ExtractionRunApiTests 真 DI verify 路径）；`OnToPilot.ApiContract.Tests` 全绿。

## 5. 遗留 / 不在本次范围

- **P1-5b corpus recovery**（`_recover_rejected_classes` + `_prepare_corpus_evidence` + `_apply_corpus_evidence_selections` + `_apply_corpus_role_decisions`；prompts `tbox.boundary.evidence_selector` / `tbox.boundary.corpus_recovery` 已落地待消费）：对 boundary/denotation 双拒的类做语料级证据恢复，两阶段接入（boundary 阶段 + denotation 后）。
- **P1-5b hierarchy recovery**（`_recover_hierarchy_one` + `_verify_subclass_candidates`；prompts `tbox.hierarchy.critic` / `tbox.hierarchy.recovery` 已落地待消费）：对 subclass 决策全拒时的父类提取 + 层级补全。
- **extractor evidence 扩 DTO**（§2.5）：若后续要严格对齐 Python critic 载荷可补 `ClassMutation.Evidence`。
- **chunk-error 语义差异**（§2.7）：.NET fail-closed（job failed）vs Python chunk-error-continue；如产品要求对齐需改 `RunLayerAsync` 的 per-chunk 容错。

## 6. 参考

- [[2026-08-23-p1-4-extraction-agent-chain]] — 前一切片（§6 登记本缺口）
- [[2026-08-23-p0-prompt-localization]] — PromptLocales 消费模式（ResolveSystemPrompt 契约）
- `backend/app/ontology/extract.py:966-1224` — Python verify 管线全段
- `src/OnToPilot/Extraction/TBoxVerifyService.cs` / `src/OnToPilot.Tests/Extraction/TBoxVerifyServiceTests.cs`
