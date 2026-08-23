# P3-1: TerminologyService ProposalsQueued 根因修复

**状态**: 已完成（实现 + 5 单元测试 + 全量回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `ExtractionOrchestrator.RunTerminologyAsync`（扩 agent 调用 + 新 helper）+ `TerminologyResult`（加 `SchemeIri` 字段）+ `TerminologyService.SyncCore`（回填 scheme_iri）+ `OnToPilotOptions`（加 max chunks 配置）+ 2 个测试文件（+5 测试）

---

## 1. 背景

`src/OnToPilot/Extraction/TerminologyService.cs:46-48` 自承 `ProposalsQueued` 恒 0，因为 .NET 端从未触发 LLM-driven proposal stage。Python 后端（`backend/app/api/extraction.py:215-249`）在 `sync_from_ontology` 完成后调用 `terminology_agent.suggest()`，把 `len(proposals)` 写进 `result["terminology_proposals"]`。

调研发现 .NET 的所有基础组件**已经齐全**：

- `TerminologyAgent.SuggestAsync(KnowledgeSystemEntity, string schemeIri, IReadOnlyList<long> chunkIds, string? model, CancellationToken)` 完整实现（`TerminologyAgent.cs:150-268`），含 chunk loading、provider config、prompt 解析、JSON 解析、signature dedup、LegacyIdAllocator 持久化。
- `TermProposalEntity` 表 + `OnToPilotDbContext.TermProposals` 已注册。
- `PromptLocales` 已注册 `terminology.steward` key（双语，`PromptLocales.cs:82/436/474`）。
- `InternalOperationDispatcher.InvokeVocabularySuggestTermsAsync`（`InternalOperationDispatcher.cs:4088-4103`）已 wire agent 到 `POST /api/knowledge/{ksId}/vocabulary/suggest` 端点路径。

唯一缺的是 **extraction job 自动触发 agent 的 pipeline 步骤**。本切片在 `ExtractionOrchestrator.RunTerminologyAsync` 内加上这一调用，让 `ProposalsQueued` 不再恒 0。

---

## 2. 决策

### 2.1 切片范围：只修 extraction job 路径

Python `extraction.py:215` 仅在 extraction job 自动流程里调 suggest；`/vocabulary/sync`（vocabulary.py）和 RDF import 都不触发。C# 沿用同语义，只在 `ExtractionOrchestrator.RunTerminologyAsync` 加 agent 步骤；`VocabularyService.SyncAsync`、`RdfImportService` 不动。

### 2.2 scheme IRI 传播：扩 `TerminologyResult.SchemeIri`

当前 `TerminologyResult` 不含 scheme IRI（`TerminologyService.cs:20-28`）。`SyncCore` 在 `EnsureScheme` 解析到 scheme 后就丢弃，调用方拿不到。Python `sync_from_ontology` 返回 `{"scheme_iri": ...}`（`terminology_sync.py:110`）。本切片给 `TerminologyResult` 加 `string? SchemeIri`，`SyncCore` 在 `EnsureScheme` 成功后填入；调用方拿到后喂给 agent。保持 record 形状向后兼容（旧测试对 `ProposalsQueued` 顺序无依赖）。

### 2.3 agent 调用位置：`_terminology.SyncAsync` 之后、`RecordTerminologyAsync` 之前

完全对齐 Python 顺序（sync → suggest → record），保持语义同款。代码改动集中在 `ExtractionOrchestrator.RunTerminologyAsync`。

### 2.4 chunk 来源：从 db.Chunks 按 KS 拉取 LegacyId

`ExtractionJobEntity.ChunkIds` 是 `List<int>` 存 `ChunkSpan.Idx`（in-memory 索引），不是 `ChunkEntity.LegacyId`，无法直接喂给 agent。Python 端 `_terminology_rows(session, ks_id, chunk_ids)` 用 `Chunk.id.in_(chunk_ids)` 即 Chunk 表整数主键；.NET 等价做法：从 `db.Chunks` join `db.Documents` 按 `Document.KnowledgeSystemId == ksId AND Document.ParseStatus == "parsed"` 查 `ChunkEntity.LegacyId`，按 `DocumentId` + `Idx` 排序，`Take(_options.TerminologySuggestionMaxChunks)` 限制。

`ChunkEntity` 没有 `Document` navigation property（`WorkspaceEntities.cs:122-144`），所以 join 是显式的，与 `TerminologyAgent.LoadChunksAsync` 的 join 写法对齐。

**选择此路径而非改造 `ExtractionJobEntity.ChunkIds` 类型**：避免 schema 迁移 + 让 agent 在 chunk 索引变化时仍能拿到最新 chunks（Python 端也是 `_terminology_rows` 走 db.Chunks，不依赖 job.ChunkIds 存储格式）。

### 2.5 KnowledgeSystemEntity 来源：从 scoped DbContext 查

通过 `_scopes.CreateScope()` 拿到 fresh `OnToPilotDbContext`，按 `ctx.Request.KnowledgeSystemId`（Guid）查 KS row。复用 `RunAgentChainAsync`（line 516-578）已验证的 scope 模式。

### 2.6 失败隔离：包在 RunTerminologyAsync 现有 try/catch 内

`RunTerminologyAsync` 已有 `try { ... } catch { termCapture.MarkError(); }`（line 491-495）——agent 抛 `InvalidOperationException`（无 LLM provider）时仍被吞，KS 没有 provider 时 `ProposalsQueued` 仍为 0，符合"advisory"语义。Agent 内部已吞 `HttpRequestException` / `IOException` / `JsonException`（TerminologyAgent.cs:195-201, 436-441），返回 0 proposals。

### 2.7 配置开关：`OnToPilotOptions.TerminologySuggestDuringExtraction`

对齐 Python `settings.terminology_suggest_during_extraction`：默认 `true`，允许运维关闭 proposal stage（例如上线初期临时禁用 agent）。

### 2.8 测试策略

- **TerminologyServiceTests（3 段新增）**：扩 `TerminologyResult` 后回归验证 `SchemeIri` 字段被填（来自 `EnsureScheme`）；缺失 entities 时仍为 null。
- **新 orchestrator seam 测试（2 段）**：构造 `ExtractionOrchestrator`（带 verify/corpus/hierarchy=null + scopes 注入 scoped `TerminologyAgent` + 假 chat 工厂 + sqlite TestDbContext + SeedChunks），运行 TBox runner，验证 `job.TerminologyProposals == agent 返回行数` + 数据库 TermProposal 行存在。第二段覆盖"无 chunks 时短路"。
- **fixture 注册 ConflictService / ConflictAgent / StructureAgent / KnowledgeStatsService**：orchestrator 的 `RunAgentChainAsync` 在 TBox runner 内总是会被调用，所以 fixture 必须注册完整的 agent chain services，否则链上 service activation 异常会让整个 job 翻 `failed`，根本走不到 terminology 阶段。这与 `ExtractionAgentChainTests.BuildServices` 模式对齐。
- **API contract**：`ApiContract.Tests` 不锁 `proposals_queued` 值（Verify Phase 1 Q4）——无需更新契约。

---

## 3. 实施

- `src/OnToPilot/Configuration/OnToPilotOptions.cs`：加 `TerminologySuggestionMaxChunks`（默认 50，对齐 Python `terminology_suggestion_max_chunks`）+ `TerminologySuggestDuringExtraction`（默认 true，对齐 Python `terminology_suggest_during_extraction`）。
- `src/OnToPilot/Extraction/TerminologyService.cs`：`TerminologyResult` 加 `string? SchemeIri = null` 字段（向后兼容默认 null）；`SyncCore` 在 `EnsureScheme` 成功后填入；catch 分支显式填 null；注释更新（移除"ProposalsQueued 永远为 0"自承段）。
- `src/OnToPilot/Extraction/ExtractionOrchestrator.cs`：构造器加 `IOptions<OnToPilotOptions>? options = null` 尾参（null 时回退到默认 `new OnToPilotOptions()`），保证手写测试 fixture 不被破坏。`RunTerminologyAsync` 在 `SyncAsync` 之后、`RecordTerminologyAsync` 之前 gate 调 `RunTerminologyAgentAsync`：
  ```csharp
  if (_options.TerminologySuggestDuringExtraction
      && _scopes is not null
      && term.Error is null
      && !string.IsNullOrEmpty(term.SchemeIri))
  ```
  新 private helper `RunTerminologyAgentAsync(JobRunContext ctx, TerminologyResult term)`：开 scope → 查 `KnowledgeSystemEntity` → join `Chunks` + `Documents` 拉 `LegacyId` → 调 `TerminologyAgent.SuggestAsync(...)` → `term with { ProposalsQueued = proposals.Count }`。
- `src/OnToPilot.Tests/Ontology/TerminologyServiceTests.cs`：+3 测试（`Sync_sets_scheme_iri_when_default_scheme_is_seeded` / `Sync_sets_scheme_iri_when_reusing_existing_scheme` / `Sync_leaves_scheme_iri_null_when_no_entities_to_anchor`）。
- `src/OnToPilot.Tests/Extraction/TerminologyAgentOrchestrationTests.cs`：新建（2 测试：`Terminology_agent_runs_after_sync_and_queues_proposals` / `Terminology_agent_short_circuits_when_no_chunks_exist`）。fixture 复用 `ExtractionAgentChainTests` 的 seed/store/blob/chat 模式 + 注册 agent chain 全套 services。

---

## 4. 验证

- 新增 5/5：`TerminologyServiceTests` 3 段 + `TerminologyAgentOrchestrationTests` 2 段。
- **全量**: 694/694 单元（baseline 689 + 新 5）+ 167/167 ApiContract 全绿。
- **Integration**: 39/42（3 失败均为 PG `42P01: relation "@p0" does not exist` 的 `IriSqlMigratorTests`，需要 PostgreSQL 容器，与 P3-1 改动无关 — pre-existing 环境缺口）。

---

## 5. 遗留 / 不在本次范围

- **`VocabularyService.SyncAsync` 路径不加 agent**：Python `/vocabulary/sync` 不触发 suggest，.NET 对齐。
- **RDF import 后不加 agent**：Python `rdf_import.py` 不调用 terminology_agent，C# 对齐。
- **agent `_source_contains` 检查**：Python `terminology_agent._filter_to_supported_labels` 在 `_sanitize` 后过滤 source text 不含 preferred_label 的 proposal。.NET `TerminologyAgent.TryBuildProposal` 当前**不**做此 grounding check（只看 `source_chunk_ids` 是否在 `allowedChunkIds`），与 P1-5a 一致的 fail-soft 语义。如需严格对齐 Python `source_contains`，是 follow-up 切片（影响更广，建议单独立项）。
- **TelemetryTests 并行 race**（P1-5b ADR §5 沿用登记）：非本切片处理。
- **`OnToPilot.Domain` 空项目清理**：独立 P3 候选。
- **PG integration test 缺口**：`IriSqlMigratorTests` 需要 PostgreSQL container。pre-existing 环境缺口，不在本切片处理。

---

## 6. 参考

- `backend/app/ontology/extract.py:215-249` — Python `terminology_agent.suggest()` 在 extraction job 流程的调用点
- `backend/app/api/extraction.py:132-143` — Python `_terminology_rows` 按 chunk_ids 拉源 chunk
- `backend/app/ontology/terminology_agent.py:268-378` — Python `TerminologyAgent.suggest()` 全段（含 signature dedup）
- [[2026-08-23-p1-5b-corpus-hierarchy-recovery]] — 同等规模的 orchestrator seam 接入模式（`RunAgentChainAsync` scope pattern）
- [[2026-08-23-p1-3-structure-agent]] — 类似 scope 注入 scoped agent 的 seam 接入
- `src/OnToPilot/Extraction/ExtractionOrchestrator.cs:477-578` — `RunTerminologyAsync` / `RunAgentChainAsync` 实现
- `src/OnToPilot/Extraction/TerminologyAgent.cs:150-268` — `SuggestAsync` 全段实现
- `src/OnToPilot.Tests/Extraction/TerminologyAgentOrchestrationTests.cs` — 新建 P3-1 seam 测试
- `src/OnToPilot.Tests/Ontology/TerminologyServiceTests.cs` — 新增 3 段 SchemeIri 字段测试