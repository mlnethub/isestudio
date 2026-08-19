# Block 8 — Vocabulary wire-up (28 dispatcher arms → real impl)

**Date**: 2026-08-19
**Branch**: `dotnet`
**Status**: Design (awaiting approval)
**Previous block**: Block 6b — ExtractionOrchestrator wire-up (commits `d532e96` / `f7b7ad3` / `116997d` / `f2df9c7`)

## Context

Block 6b 把 `extraction.run*` 接进 `ExtractionOrchestrator.Start*Async`,
重写了 extraction 的写入面(Block 7a-7c 也已把 ontology 编辑 +
ABox CRUD + validate + reconciliation 完整真实化)。但是
`InternalOperationDispatcher.cs:194-209` 的 16 个 vocabulary arm 仍是
placeholder:

```csharp
"vocabulary.get" => Task.FromResult<object?>(EmptyVocabularyResponse()),
"vocabulary.list_concepts" => Task.FromResult<object?>(EmptyListResponse()),
"vocabulary.create_concept" => Task.FromResult<object?>(EmptyConcept()),
... (共 16 个 internal arms)
```

加上 `external.vocabulary.*` (4 arms, lines 283-286) + `published.vocabulary.*`
(4 arms, lines 297-300) + `published.release.vocabulary.*` (4 arms,
lines 309-312),一共 **28 个 placeholder arms**。

**目标**: 28 个 arms 接真,前端 SKOS tab 不再是 no-op。
3 个新 service(VocabularyService / VocabularyProposalService /
TerminologyAgent)+ 1 个 DI 扩展 + 6-10 个 HTTP contract tests。

## 已查证的关键事实

| 项 | 现状 |
|---|---|
| `SkosManager.cs` (920 行) | 已实现 `BuildView / CreateScheme / UpdateScheme / DeleteScheme / GetScheme / GetConcept / CreateConcept / UpdateConcept / DeleteConcept / ListConcepts / Resolve / MappedAliases`,所有 wire shape 完整(snake_case via JSON 序列化) |
| `TerminologyService.SyncAsync` | 已存在(extract 路径用),确定性 sync TBox→SKOS,返回 `TerminologyResult(terms_added, terms_mapped, ...)` |
| `TermProposalEntity` (320 行) | 已存在 `OnToPilotDbContext.TermProposals`,EF wired,`Payload` / `Evidence` / `SourceChunkIds` 是 `JsonDocument?` 列 |
| `VocabularyController` 16 endpoints | 已存在,全部连到 dispatcher arm |
| `ExternalApiController` + `PublishedController` | 已存在,12 个 vocabulary endpoints |
| `KnowledgeSystemAccessService` (B7c) | `GetEffectiveRoleAsync` 解析 admin + KS owner → Owner;viewer user 自动到 Viewer |
| `RejectIfExtractionActiveAsync` (B5) | 已存在,409 envelope `{detail: {error}}` |
| `WriteAuditAsync` (B6a/B7c) | service-level audit,EF entity 含 Added/Removed byte[] diff |
| `FakeChatClientFactory.Default` (B6b) | process-wide singleton + `UseClient`/`Reset` hooks,propose test 直接用 |
| `TerminologyAgent` (LLM-driven propose) | **缺失**,需新建 |

## Python `vocabulary.py` (489 行) 操作映射

| HTTP | role | Python 调 | .NET 现有/缺失 |
|---|---|---|---|
| `GET /vocabulary` | reader | `skos.build_view(graph)` | `SkosManager.BuildView` ✓ |
| `GET /vocabulary/schemes` | reader | `build_view` 投影 | 同上 ✓ |
| `POST /vocabulary/schemes` | writer | `skos.create_scheme` + audit | `SkosManager.CreateScheme` ✓, audit diff ✗ |
| `PATCH /vocabulary/schemes` | writer | `skos.update_scheme` + audit | `SkosManager.UpdateScheme` ✓, audit ✗ |
| `DELETE /vocabulary/schemes` | writer | `skos.delete_scheme` + audit | `SkosManager.DeleteScheme` ✓, audit ✗ |
| `GET /vocabulary/concepts` | reader | `skos.list_concepts(filters)` | `SkosManager.ListConcepts` ✓ |
| `POST /vocabulary/concepts` | writer | `skos.create_concept` + audit | `SkosManager.CreateConcept` ✓, audit ✗ |
| `PATCH /vocabulary/concepts` | writer | `skos.update_concept` + audit | `SkosManager.UpdateConcept` ✓, audit ✗ |
| `DELETE /vocabulary/concepts` | writer | `skos.delete_concept` + audit | `SkosManager.DeleteConcept` ✓, audit ✗ |
| `GET /vocabulary/resolve` | reader | `skos.resolve(q, lang)` | `SkosManager.Resolve` ✓ |
| `GET /vocabulary/export` | reader | `store.serialize_graph(fmt)` | `StoreWrapper` 支持 ✓ |
| `POST /vocabulary/sync` | writer | `terminology_sync.sync_from_ontology(ks)` | `TerminologyService.SyncAsync` ✓, audit ✗ |
| `POST /vocabulary/suggest` | writer | `terminology_agent.suggest(ks, scheme, chunks, model)` | **TerminologyAgent 缺失** ✗ |
| `GET /vocabulary/proposals` | reader | SQL on `TermProposal` | **VocabularyProposalService.ListAsync 缺失** ✗ |
| `POST /vocabulary/proposals/{id}/accept` | writer | `_apply_proposal` + audit + `TermProposal.status=accepted` | **VocabularyProposalService.AcceptAsync 缺失** ✗ |
| `POST /vocabulary/proposals/{id}/reject` | writer | `TermProposal.status=rejected` + audit | **VocabularyProposalService.RejectAsync 缺失** ✗ |

`external.vocabulary.*` (4) + `published.vocabulary.*` (4) +
`published.release.vocabulary.*` (4) 都是 **只读** 投影,直接调对应 read
service(scheme/concept list/resolve/export)。

## 关键设计决定

### D1: Wire DTO 形状 — 复用 `SkosView` / `SkosConceptView` 等 record

`SkosManager` 已经返回 `SkosView` / `SkosConceptView` / `SkosSchemeView` /
`SkosMatch` / `SkosConceptPage` 等 record,字段名已 PascalCase。
Dispatcher 的 wire 序列化要 snake_case,所以 2 个选项:

**(a)** 在 record 上加 `[JsonPropertyName]` attribute(snake_case 字段名),
    然后 SkosManager 返回值可直接 wire
**(b)** 新增 `VocabularyWire` 转换 helper(`ToWire(SkosView)` 等)

选 (a) 因为 record 已经是稳定形状,加 attribute 不影响业务逻辑。
`ExtractionJobOut` (B6b) 已经用同样模式 (`id` / `kind` / `status`)。
审查 risk: 现有测试断言 PascalCase 的会被 snake_case 化,需要 grep
确认现有 lower-level test 没有 strict string-compare PascalCase
(`grep "Assert.Equal.*SkosView\.\|Assert.Equal.*\.Title"` 在测试目录)。

### D2: Service 包装模式 — VocabularyService + VocabularyProposalService + TerminologyAgent

3 个新 service:

- **`VocabularyService`** (Scoped) — 包 SkosManager 方法 + extraction guard
  (`RejectIfExtractionActiveAsync`) + Reader/Writer role gate (走
  `KnowledgeSystemAccessService.GetEffectiveRoleAsync`) + audit (走
  `WriteAuditAsync`,pre/post diff dump 同 B6a `OntologyService`)。
  方法签名模板:
  ```csharp
  public async Task<SkosView> GetVocabularyAsync(KsContext ks, Actor actor, ct);
  public async Task<SkosSchemeView> CreateSchemeAsync(KsContext ks, SkosSchemeData data, Actor actor, ct);
  public async Task<SkosConceptView> CreateConceptAsync(KsContext ks, string schemeIri, SkosConceptData data, Actor actor, ct);
  // ... 16 个方法
  ```

- **`VocabularyProposalService`** (Scoped) — list/accept/reject
  `TermProposalEntity`。AcceptAsync 调
  `SkosManager.CreateConcept` / `UpdateConcept` + audit + TermProposal
  row update。RejectAsync 只改 TermProposal row。同 B7c
  `ValidationDecisionService` 模式。

- **`TerminologyAgent`** (Scoped) — LLM-driven `SuggestAsync(ks, schemeIri,
  chunkIds, model, ct)`。依赖 `IChatClientFactory` +
  `PromptSnapshotService` + `TerminologyService` + `OnToPilotDbContext`。
  返回 `IReadOnlyList<TermProposalEntity>`。B6b 已 wired
  `FakeChatClientFactory.Default`,propose test 走同样模式。

**Typed resolver pattern**(同 B6b `ResolveExtractionOrchestrator`):
dispatcher 加 `ResolveVocabularyService()` /
`ResolveVocabularyProposalService()` / `ResolveTerminologyAgent()`。

### D3: Role gate — 直接走 service

写 endpoint (create/update/delete scheme+concept, sync, suggest,
accept/reject) 用 `Writer` role;读 endpoint (list/get/resolve/export,
list_proposals) 用 `Reader` role。Service 内部调
`RequireRoleAsync(KsRole.Writer|Reader, ks)` (同 `KnowledgeService`
模式)。**与 B6b deferred Editor gate 同步,这次直接加,不 defer**。

### D4: 409 envelope — 复用 `RejectIfExtractionActiveAsync`

`CreateScheme / UpdateScheme / DeleteScheme / CreateConcept / UpdateConcept
 / DeleteConcept / Sync / Suggest / Accept / Reject` 在 mutating 前调
`RejectIfExtractionActiveAsync(ksId)`,返回 `Task<GraphWriteConflictException?>`
(null 表示 OK)。Service 捕获并抛出 `GraphWriteConflictException`,
`FastApiErrorMiddleware` (line 59) 已 wire 409 envelope。

### D5: Audit `Added/Removed` — pre/post dump diff(同 B6a)

Mutating 服务方法走 capture + pre/post dump:
```csharp
var preBytes = _store.DumpNQuads(ks.VocabularyGraph);
await SkosManager.CreateScheme(...);
var postBytes = _store.DumpNQuads(ks.VocabularyGraph);
var (added, removed) = DiffNQuads(preBytes, postBytes);
await WriteAuditAsync(db, ksId, "vocabulary.create_scheme", actor, summary,
    detail: {scheme_iri}, added, removed, ct);
```
`Reject` 不 dump quads(只改 TermProposal row)。
`AcceptProposal` dump quads(SkosManager.CreateConcept/UpdateConcept 写图)。

### D6: External/Published 简化 — 只读 12 个 arms smoke test

`external.vocabulary.*` (5: concepts/resolve/schemes/export + 1) +
`published.vocabulary.*` (4: concepts/resolve/schemes/export) +
`published.release.vocabulary.*` (3) 全部是只读,且都基于已有
read service(`VocabularyService.GetVocabulary / ListConcepts /
ListSchemes / Resolve / Export`)。不需要 audit / write gate。

Dispatcher arm 直接调对应 read service(走 `RequireRoleAsync(Reader)`)。
HTTP test 只做 1-2 个 smoke test (token scope + body shape 跟 internal
一致),不重复 detailed CRUD。

### D7: `FakeChat` 扩 `EnqueueTerminologyProposal()`

B6b `FakeChat` 已有 `EnqueueValidDelta` / `EnqueueValidABoxDelta` /
`EnqueueValidDeltas`。propose test 需一个 LLM 返回 N 个 SKOS concept
JSON 的 fixture。加 `EnqueueTerminologyProposal(int count = 3)` 方法,
内部 enqueue 写死的 JSON(`{"proposals": [{"term": "...", "scheme_iri":
"...", "pref_label": "...", ...}]}`)。

### D8: `TerminologyAgent` 不写 `capture`/`diff`/audit

LLM-driven propose 只插 `TermProposalEntity` rows(status=pending),
**不直接动 RDF graph**(由后续 `AcceptProposal` 决定写不写)。
Python `terminology_agent.suggest` 同样只写 proposal rows,不写图。

## 文件改动清单

### 新增 (5)

1. **`src/OnToPilot/Ontology/VocabularyService.cs`** — Scoped service,
   16 个方法 (CRUD + read + sync)。每个 mutating 方法走 extraction
   guard + role gate + audit diff dump。
2. **`src/OnToPilot/Ontology/VocabularyProposalService.cs`** — Scoped
   service, list/accept/reject `TermProposalEntity`。
3. **`src/OnToPilot/Extraction/TerminologyAgent.cs`** — Scoped service,
   `SuggestAsync(ks, schemeIri, chunkIds, model, ct)` 经
   `IChatClientFactory.Create()` + `PromptSnapshotService`。
4. **`src/OnToPilot/Ontology/VocabularyServiceCollectionExtensions.cs`** —
   `AddVocabularyServices()` 注册 3 个 service。
5. **`src/OnToPilot.Tests/Ontology/VocabularyApiTests.cs`** — 8-10 个
   HTTP-level contract test 覆盖 16 个 internal endpoint。
6. **`src/OnToPilot.Tests/Ontology/VocabularyProposalApiTests.cs`** —
   1-2 个 accept/reject test。

### 修改 (4)

7. **`src/OnToPilot/Program.cs`** — `builder.Services.AddVocabularyServices();`
   在 `AddExtractionServices()` 之后。
8. **`src/OnToPilot/Integration/InternalOperationDispatcher.cs`** —
   28 个 arms 替换 + 28 个 typed helper(16 internal + 12 external/published)。
9. **`src/OnToPilot.Tests/Extraction/FakeChat.cs`** — 加
   `EnqueueTerminologyProposal(int count = 3)`。
10. **`src/OnToPilot/Ontology/SkosView.cs`** (或 inline 在 VocabularyService) —
    `SkosView` / `SkosConceptView` / `SkosSchemeView` 等 record 上加
    `[JsonPropertyName]` attribute 锁 snake_case wire (D1)。

## 8-10 个 HTTP contract tests (internal)

| # | Test | 覆盖路径 | 关键断言 |
|---|---|---|---|
| 1 | `Get_vocabulary_returns_skos_view_with_schemes_and_concepts` | `GET /vocabulary` | 200 + SkosView 含 schemes/concepts/stats |
| 2 | `List_concepts_with_filters_returns_paginated_page` | `GET /vocabulary/concepts?q=&status=&limit=10` | 200 + SkosConceptPage 含 items/total |
| 3 | `Create_concept_writes_to_vocabulary_graph_and_audit` | `POST /vocabulary/concepts` | 200 + vocab graph 含 Concept + prefLabel + audit row |
| 4 | `Update_concept_replaces_labels_and_writes_audit` | `PATCH /vocabulary/concepts` | 200 + new label 在 graph + audit |
| 5 | `Delete_concept_removes_concept_from_graph_and_audit` | `DELETE /vocabulary/concepts` | 200 + Concept 不在 graph + audit |
| 6 | `Create_scheme_with_extraction_active_returns_409` | `POST /vocabulary/schemes` | 409 + `{detail: {error}}` envelope |
| 7 | `Sync_runs_TerminologyService_and_audits_added_concepts` | `POST /vocabulary/sync` | 200 + sync result {terms_added, terms_mapped} + audit |
| 8 | `Suggest_with_fake_chat_creates_pending_proposals` | `POST /vocabulary/suggest` | 200 + proposal rows (status=pending) + FakeChat 装好 LLM |
| 9 | `Accept_proposal_applies_payload_and_writes_audit` | `POST /vocabulary/proposals/{id}/accept` | 200 + concept created in graph + proposal status=accepted |
| 10 | `Reject_proposal_marks_status_rejected_and_writes_audit` | `POST /vocabulary/proposals/{id}/reject` | 200 + proposal status=rejected + audit |

外部/published 2 个 smoke test:
- `External_vocabulary_concepts_with_reader_scope_returns_view`
- `Published_vocabulary_export_returns_turtle_string`

## 关键文件路径速查

| 用途 | 路径 |
|---|---|
| VocabularyService (新增) | `src/OnToPilot/Ontology/VocabularyService.cs` |
| VocabularyProposalService (新增) | `src/OnToPilot/Ontology/VocabularyProposalService.cs` |
| TerminologyAgent (新增) | `src/OnToPilot/Extraction/TerminologyAgent.cs` |
| DI 注册扩展 (新增) | `src/OnToPilot/Ontology/VocabularyServiceCollectionExtensions.cs` |
| HTTP contract tests (新增) | `src/OnToPilot.Tests/Ontology/{Vocabulary,VocabularyProposal}ApiTests.cs` |
| Program DI (改) | `src/OnToPilot/Program.cs` |
| Dispatcher 28 arms (改) | `src/OnToPilot/Integration/InternalOperationDispatcher.cs:194-209, 283-312` |
| FakeChat 扩 (改) | `src/OnToPilot.Tests/Extraction/FakeChat.cs` |
| SkosView wire shape (改) | `src/OnToPilot/Ontology/SkosManager.cs:82-138` |

## 复用现有代码

- **Service 模板**: 照抄 B7c `ABoxService.cs` 的 `RequireRoleAsync` /
  `WriteAuditAsync` / `DiffNQuads` 模式。
- **ProposalService 模板**: 照抄 B7c `ValidationDecisionService.cs` 的
  upsert by `(ks_id, signature)` + `ResolvedBy` = actor display name。
- **Dispatcher helper 模板**: 照抄 B6b `InvokeExtractionAsync` 私有 helper
  模式。
- **Audit helper 模板**: 照抄 `KnowledgeService.WriteAuditAsync` +
  B6a `OntologyService` 的 pre/post diff dump。
- **Role gate 模板**: 照抄 `KnowledgeService.RequireRoleAsync` 的
  `KSRole.Viewer / Editor / Owner` 检查。
- **LLM wiring 模板**: B6b `FakeChatClientFactory.Default` +
  `IChatClientFactory.Create(config)` + `PromptSnapshotService`。
- **Test factory 模板**: 照抄 B6b `AuthTestWebApplicationFactory` +
  `ExtractionTestCollection`。

## 实现步骤

1. **扩 `FakeChat`** 加 `EnqueueTerminologyProposal(int count = 3)` (D7)
2. **写 `VocabularyService.cs`** (16 个方法, Scoped, Role gate +
   extraction guard + audit diff)
3. **写 `VocabularyProposalService.cs`** (list/accept/reject, Scoped)
4. **写 `TerminologyAgent.cs`** (LLM propose via `IChatClientFactory`)
5. **写 `VocabularyServiceCollectionExtensions.cs`** (`AddVocabularyServices()`)
6. **改 `SkosManager.cs`** record 加 `[JsonPropertyName]` (D1)
7. **改 `Program.cs`** 加 `AddVocabularyServices()`
8. **改 `InternalOperationDispatcher.cs`** 28 arms + typed resolvers
   + `InvokeVocabularyXxxAsync` helpers
9. **写 `VocabularyApiTests.cs`** (10 个 HTTP tests)
10. **写 `VocabularyProposalApiTests.cs`** (1 个 accept/reject test)
11. **编译** `dotnet build -c Release` 0 warning 0 error
12. **跑新 tests** `dotnet test --filter VocabularyApi` 期望 11/11
13. **跑全量回归** `dotnet test` 期望 324 + 11 = 335 passing
    (1 pre-existing fail 同 B6b: `is_admin` 命名 bug, Block 11 修)
14. **Commit + memory + 报告**

## 验证

### 单元 + 集成层 (必须全绿)

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release        # 0 warning 0 error
dotnet test  src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~VocabularyApiTests"             # 11 passed
dotnet test  src/OnToPilot.Tests/OnToPilot.Tests.csproj        # 335/336 pass
```

### 容器层 (用户执行)

```bash
docker compose build backend
docker compose up -d --no-deps --force-recreate backend
```

### 浏览器手测清单 (用户执行)

1. 登录 admin → 任一 KS → Terminology tab
2. Add concept "Animal" → 应在 vocab graph 出现 + audit row
3. Edit prefLabel → graph 更新 + audit row
4. Delete concept → graph 移除 + audit row
5. Add scheme "Animals v1" → 应在 schemes list 出现
6. Edit/Delete scheme → 同上
8. Export vocab → 下载 turtle 文件
9. Sync → TBox 已有 class 应自动 mint SKOS concept
10. Suggest (LLM) → proposals 列表出现新条目
11. Accept proposal → concept 写入 graph + proposal 状态 accepted
12. Reject proposal → proposal 状态 rejected + audit row

## 不在本设计范围 (留给后续 block)

- **Block 9** — Resolution (EntityResolution status lifecycle +
  `documents.contribution.individual_count`)
- **Block 10** — Releases
- **Block 11** — Auth/Tokens/McpTokens (修 `is_admin` 命名 bug)
- **Block 12** — Settings/Prompts/History/RdfImport/External
- **B6b deferred items**: `/extract*` Editor role gate +
  `JsonException → 400` mapping (用户已 adjudicated "接受现状 + 排期后续",
  后续 block 补)

## 风险与回退

- **风险 1**: `SkosView` record 加 `[JsonPropertyName]` 破坏现有
  PascalCase 测试 → 实施前 grep `Assert.Equal.*SkosView\.|Assert.Equal.*\.Title`
  在测试目录,如果有 strict string-compare 需要先改。
- **风险 2**: `VocabularyService` extraction guard 抛
  `GraphWriteConflictException` 与 `FastApiErrorMiddleware` (line 59) 集成
  → B5/B6b 已 wire 409 envelope,直接复用。
- **风险 3**: `TerminologyAgent` LLM propose 写 TermProposal rows
  涉及 EF + Oxigraph 同事务边界。Python 实现是分两次写
  (`session.add(proposal)` 不开 RDF 图事务)。.NET 同样: insert
  TermProposalEntity 不需要 SKOS 图写,只在 Accept 时才动 RDF。
- **风险 4**: `RejectIfExtractionActiveAsync` 与
  `RunWithExtractionGuardAsync` 重复 — VocabularyService 写
  endpoint 用前者(单独抛 `GraphWriteConflictException`),不 wrap
  `RunWithExtractionGuardAsync`。后者是 dispatcher 级别的 mutex 锁,
  给 extraction.run* 用。Vocabulary 不需要。
- **回退**: 拆 5 个 commit (扩 FakeChat / VocabularyService /
  VocabularyProposalService / TerminologyAgent / Dispatcher wire-up +
  tests),任何子步失败可单步 revert。

## 设计选择 Summary

| 决策点 | 选 | 不选 | 理由 |
|---|---|---|---|
| Wire DTO 形状 | `[JsonPropertyName]` on `SkosView` 等 record | 新 wire record | record 稳定,加 attribute 不动业务逻辑 |
| Service 包装 | `VocabularyService` + `VocabularyProposalService` + `TerminologyAgent` | Dispatcher 直接调 SkosManager | 集中 role gate + extraction guard + audit,易测易维护 |
| Role gate | `RequireRoleAsync(Writer|Reader)` on service | Controller-level `[Authorize]` | 与 B7c ABoxService 一致,policy 集中 |
| 409 envelope | `RejectIfExtractionActiveAsync` (B5) | 新写 mutex | 已存在,B5/B6b wire 过 |
| Audit diff | pre/post `DumpNQuads` (B6a) | 用 `QuadChangeCapture.Diff` | B6a 已选这条路,B7c 验证 OK |
| External/Published | 只读 12 arms 直接调 read service | 复制 internal 16 arms | 避免重复,token scope 是 controller 责任 |
| LLM propose | `TerminologyAgent` (新) + FakeChat fixture | Skip propose,留 placeholder | 用户选完整 Block 8,propose 必须做 |
| TerminologyAgent 事务边界 | 只写 TermProposal rows (不写图) | 开 RDF 图事务 | Python 同样,Accept 才动图 |