# P1-5b: TBox corpus + hierarchy recovery wire-up

**状态**: 已完成（实现 + 23 单元测试 + 全量回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `CorpusRecoveryService.cs`（新） + `HierarchyRecoveryService.cs`（新） + `IExtractionMerger` / `ExtractionMerger` / `FakeMerger`（扩展 `RejectedClasses` / `RecoveredClasses` 字段 + `MergeTBox(...verify)` 签名） + `ExtractionOrchestrator`（新增 `ChunkVerifyOutcome` record + `RunCorpusRecoveryAsync` / `RunHierarchyRecoveryAsync` 接入） + `ExtractionServiceCollectionExtensions`（DI 注入） + 3 个新测试文件

---

## 1. 背景

P1-5a ADR §5 登记的两个 pass：

```python
# extract.py:1629-1708（在 verify 之后、ABox 之前）
recovered = _recover_rejected_classes(rejected_candidates, ...)
_recover_hierarchy_one(chunk_text, existing_labels, ...)
```

- **corpus recovery**（`_recover_rejected_classes` :1225-1385）：job-level 第二轮扫描，聚合每个 chunk 的 `RejectedClass` 列表 → 跨 chunk 采证（`evenly_sampled` 8 段 × `label_evidence_windows` 2 段）→ 双 LLM（`evidence_selector` 选最佳段落 + `corpus_recovery` 终审）。每个 chunk 的局部 critic 缺乏上下文，corpus 视角下能救回一些合理类型。fail-closed 五段（structured non-type + 显式 individual + XSD datatype + keep=true + role=type + confidence≥0.85 + evidence/label grounding）。
- **hierarchy recovery**（`_recover_hierarchy_one` :1413-1528 + `_verify_subclass_candidates` :1388-1410）：per-chunk 第二轮扫描，用已合并的 vocabulary 让 LLM 提议 explicit parents + 缺失中间类。每条新类走 `_verify_tbox_candidates`（同 critic 主链）；每条新边走 hierarchy critic（`apply_subclass_decisions`）。`allowed_norms` 集严格——只信任已 admitted 的端点。

7 个 boundary prompt 已在 P1-5a 落地，本切片消费最后 4 个（`tbox.boundary.evidence_selector` / `tbox.boundary.corpus_recovery` / `tbox.hierarchy.critic` / `tbox.hierarchy.recovery`）。

## 2. 决策

### 2.1 切片边界：完整 P1-5b（corpus + hierarchy 双 pass）

两段都属于 P1-5b，且都依赖 verify 主链已就绪（corpus 拒集 = verify 拒集；hierarchy 验新类也走 `_verify_tbox_candidates`）。同步落比拆两个子切片更省 prompt catalog 来回成本。

### 2.2 拒/恢 集传播载体：扩 `ExtractionMergeResult`（拒/恢 字段 + `MergeTBox(...verify)` 尾参）

Python 的 rejected/recovered 是 verify 主链内部传递；.NET 要让 corpus recovery 看到 per-chunk 的拒集，最干净的做法是让 `IExtractionMerger.MergeTBox` 接收 `TBoxVerifyResult?`，把 `verify?.Rejections` / `verify?.Recoveries` 写进 `ExtractionMergeResult.RejectedClasses` / `.RecoveredClasses`。runner 的 `onChunk` 回调顺势收集 `ChunkVerifyOutcome(idx, text, verify.Rejections)` 列表给两个 recovery pass 消费。ABox 路径不传 verify（拒/恢 集合恒为空）。

### 2.3 corpus recovery 形状（Python `_recover_rejected_classes`）

`CorpusRecoveryService.RecoverAsync(chat, perChunk, existingClassNorms, ct)`：
- `BuildCandidates`：跨 chunks 聚合 + XSD datatype 排除（`Vocabulary.CanonicalDatatypeName`）+ 表面 grounding（`RoleEvidence.SurfaceIsGrounded`）+ 单 chunk 内去重 → `Dictionary<string, CorpusCandidate>`。
- `PrepareCorpusEvidence`：每个候选采 8 段（`EvenlySampled`），每段拉 2 个 `LabelEvidenceWindows`（radius=320）→ 编号 `pN` passages。
- 双 LLM 段（fail-soft）：evidence_selector 异常 → fallback to diverse passages；corpus_recovery 异常 → 该候选 skip。
- `ApplyCorpusRoleDecisions`：五段 fail-closed 决策（structured non-type + 显式 individual + XSD datatype + keep=true + role=type + floor + 支持 occurrence 的 label/evidence 同时 grounding）。`HasIndependentTypeEvidence` 检查 structured-signal 反向独立证据（与 P1-5a `ApplyTBoxRoleDecisions` 同款）。

### 2.4 hierarchy recovery 形状（Python `_recover_hierarchy_one`）

`HierarchyRecoveryService.RecoverAsync(chat, text, allowedLabels, ct)`：
- 单 LLM 段提议 `classes` + `subclass_of`（注意 `subclass_pair` 字段别名 `sub`/`child`/`subclass` + `super`/`parent`/`superclass`，.NET `SubclassField` 静态 helper 三档 fallback）。
- 新类必须 surface + evidence grounding；新边端点必须全部在 `canonical ∪ newCanonical`，`sub == sup` 自环丢弃。
- 过滤候选类到 `used_accepted_new_norms`（只保留作为某条边 super 端的新类，杜绝 dangling class）。
- 新类走 `TBoxVerifyService.VerifyAsync`（同 critic 主链）→ admitted 入 `allowed_norms`。
- 边走 `VerifySubclassCandidatesAsync`（hierarchy critic 二次判 + `ApplySubclassDecisions`）→ only-admitted-norms 决策。
- 仅 admitted 边引用的新类落地。

### 2.5 接入形状：TBox capture 完成后、ABox 之前的独立 capture

两个 pass 都开新的 `StoreWrapper.CaptureAsync(TBoxGraph, ...)`，不沿用 TBox phase 的 capture（已 committed）。corpus 是 job-level 单次 merge；hierarchy 是 per-chunk 串行（每次拿 capacity lease + `ExistingClassLabels` → `SurfaceIsGrounded` → top 400）。best-effort try/catch 吞所有非 `OperationCanceledException`——Python 的 `logger.warning` 而非 `raise`，对齐。

### 2.6 combined runner 顺序：TBox → agent chain → recovery → ABox

Python `extract.py:1629-1708`：TBox 完成后先 conflicts sync → conflict agent → structure agent → 再跑 corpus + hierarchy → 才进 ABox。agents 可能 merge / attach 类改变 recovery prompt 看到的 vocabulary；recovery 产物必须先于 ABox（ABox chunks type against 新类）。.NET 沿用同序。

### 2.7 prompt snapshot 扩 4 key

`BuildTBoxPromptSnapshot`：verify wired 时加 3 个；corpus wired 时加 `tbox.boundary.evidence_selector` + `tbox.boundary.corpus_recovery`；hierarchy wired 时加 `tbox.hierarchy.critic` + `tbox.hierarchy.recovery`。Python 记录 job 实际用到的每个 prompt，沿用同模式。

### 2.8 测试 harness

- 决策函数纯测试：`ApplyCorpusRoleDecisions` 7 段（accept grounded / 拒 string "true" / 拒 floor 下 / 拒 individual role / 拒 显式 individual 声明 / 拒 ungrounded evidence）+ `BuildCandidates` 5 段（在图中跳过 / XSD 跳过 / 未落地跳过 / 跨 chunks 聚合 / chunk 内去重）+ `ApplyCorpusEvidenceSelections` 4 段（信任 passage_id / limit 上限 / 空 payload fallback / 未知 id fallback）。
- `ApplySubclassDecisions` 7 段（accept grounded / 拒 string "true" / 拒 floor 下 / 拒 ungrounded / 拒 allowed 外 / 字段别名 / 自环通过 grounding 拒）。
- orchestrator seam：构造 verify + corpus + hierarchy 三件套 + verify-accept-all + 空 hierarchy recovery reply → job completed，4 次 LLM 调用（extract + critic + denotation + hierarchy recovery），snapshot 含 4 个 recovery prompt key。corpus recovery 在 verify-accept-all 路径下 `BuildCandidates` 为空，零 LLM 调用。

## 3. 实施

- `CorpusRecoveryService.cs`（新，~480 行）：常量 `EvidenceSelectorKey` / `CorpusRecoveryKey` + `RecoverAsync` + 4 个 internal static 决策函数（`BuildCandidates` / `PrepareCorpusEvidence` / `ApplyCorpusEvidenceSelections` / `ApplyCorpusRoleDecisions`）+ `EvenlySampled<T>` / `LabelEvidenceWindows` private helpers（Python `str.rsplit` 用 `IndexOf` 替代，保留 last segment） + `CallAsync`（`Llm.TBoxCorpus.{EvidenceSelector|CorpusRecovery}` activity）+ wire DTO（`CorpusRecoveryChunk` / `CorpusCandidate` / `CorpusOccurrence` / `PreparedCorpusCandidate` / `PreparedPassage` / `RecoveredCorpusClass` / `AcceptedCorpusClass` / `CorpusRecoveryResult`）。
- `HierarchyRecoveryService.cs`（新，~370 行）：常量 `HierarchyCriticKey` / `HierarchyRecoveryKey` + `RecoverAsync` + `VerifySubclassCandidatesAsync` private + `ApplySubclassDecisions` static internal + `SubclassField` 三档字段别名 fallback + wire DTO（`ProposedClass` / `ProposedEdge` / `RecoveredEdge` / `HierarchyRecoveryResult`）。
- `IExtractionMerger.cs`：`MergeTBox` 签名 `MergeTBox(KsContext ks, TBoxDelta delta, TBoxVerifyResult? verify)`（verify 尾参，向后兼容旧调用）。`ExtractionMergeResult` 加 `RejectedClasses` / `RecoveredClasses` 字段。
- `ExtractionMerger.cs`：接收 verify，从 verify 拷到 result。ABox 路径不传 verify，拒/恢 字段恒为 `Array.Empty<RejectedClass>()`。
- `FakeMerger.cs`：转发新签名。
- `ExtractionOrchestrator.cs`：构造参数 `corpus` / `hierarchy`（可空 seam，沿用 `_verify` 模式）+ `ChunkVerifyOutcome` internal record + `VerifiedTBox` 内部 record（包装 delta + verify）+ `ExtractAndVerifyAsync` 返回 `VerifiedTBox` + `RunLayerAsync` 加 `Func<int, object, ValueTask>? onChunk` 回调 + 两个 runner（TBoxOnly / Combined）传 `onChunk` 收集 `perChunk` + `RunCorpusRecoveryAsync` / `RunHierarchyRecoveryAsync` 私有方法 + `MergeCorpusRecoveredAsync` / `MergeHierarchyRecoveredAsync` 在 TBox graph capture 内 merge + `BuildTBoxPromptSnapshot` 扩 4 key。ABox `RunLayerAsync` 调用补 `onChunk: null`。
- `ExtractionServiceCollectionExtensions.cs`：`AddSingleton<CorpusRecoveryService>()` + `AddSingleton<HierarchyRecoveryService>()`。
- 测试：3 个新测试文件（22 decision-helper + 1 orchestrator seam = 23）。

## 4. 验证

- 新增 23/23：`CorpusRecoveryServiceTests` 16 段（`BuildCandidates` 5 + `ApplyCorpusEvidenceSelections` 4 + `ApplyCorpusRoleDecisions` 7）+ `HierarchyRecoveryServiceTests` 7 段（`ApplySubclassDecisions` 全谱）+ `CorpusHierarchyRecoveryIntegrationTests` 1 段（orchestrator seam 端到端）。
- **全量**: 680/680 单元 + 167/167 ApiContract 全绿。

## 5. 遗留 / 不在本次范围

- **extractor evidence 扩 DTO**：P1-5a §5 沿用，与本切片无关。
- **chunk-error 语义差异**：P1-5a §5 沿用。两个 recovery pass 走的是 best-effort try/catch（即使 Python 同款），与 verify 主链的 fail-closed 不同。
- **hierarchy recovery 中间类的 `_verify_tbox_candidates` 走完整 critic**：P1-5a 已落，新类没有独立 prompt，复用同主链。Python 同。
- **TelemetryTests 与 background LLM 任务的并行 race**：xUnit 默认并行 + 既有 TelemetryTests 不在 collection，偶发 `Assert.Single` 失败（背景 LLM activity 漏到 listener）。re-run 即恢复。视为既有 race（任何 background-LLM 测试都易触发），不在本切片处理。

## 6. 参考

- [[2026-08-23-p1-5-tbox-verify]] — 前一切片（§5 登记本缺口 + 7 个 prompt 双语落地）
- [[2026-08-23-p1-4-extraction-agent-chain]] — combined runner 顺序参考
- [[2026-08-23-p0-prompt-localization]] — PromptLocales 消费模式
- `backend/app/ontology/extract.py:1225-1528` — Python 两个 recovery pass 全段
- `src/OnToPilot/Extraction/CorpusRecoveryService.cs` / `src/OnToPilot/Extraction/HierarchyRecoveryService.cs`
- `src/OnToPilot.Tests/Extraction/CorpusRecoveryServiceTests.cs` / `src/OnToPilot.Tests/Extraction/HierarchyRecoveryServiceTests.cs` / `src/OnToPilot.Tests/Extraction/CorpusHierarchyRecoveryIntegrationTests.cs`