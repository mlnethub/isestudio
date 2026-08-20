# Block 8 — Vocabulary wire-up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire 28 dispatcher arms (16 internal `vocabulary.*` + 12 external/published `*.vocabulary.*`) to real service implementations so the SKOS terminology panel, vocabulary export, terminology sync, and LLM-driven proposal review all stop being no-ops.

**Architecture:**
- Three new scoped services wrap `SkosManager` + `TerminologyService` + `TermProposalEntity` with extraction guards, Reader/Writer role gates, and audit pre/post-diff dumps (B7c `ABoxService` pattern).
- Wire DTO shape via `[JsonPropertyName]` on the existing `SkosView` / `SkosConceptView` / `SkosSchemeView` / `SkosMatch` / `SkosConceptPage` records (D1) — no new DTO records.
- External/Published arms are read-only smoke tests; they route to the same `VocabularyService` read methods.
- TerminologyAgent (LLM-driven propose) uses the B6b `FakeChatClientFactory.Default` + `IChatClientFactory` pattern; it only inserts `TermProposalEntity` rows, never writes to the RDF graph (graph writes happen at AcceptProposal time, matching Python).

**Tech Stack:**
- .NET 10 / ASP.NET Core 10 / EF Core 10 / Npgsql / SQLite (tests) / xUnit 2.9.3
- `Microsoft.Extensions.AI.IChatClient` + `IChatClientFactory`
- `WebApplicationFactory<Program>` + `AuthTestWebApplicationFactory`
- Oxigraph (StoreWrapper) + System.Text.Json with `[JsonPropertyName]`

## Global Constraints

These constraints apply to **every** task in this plan. Tasks reference them rather than repeat them.

| 约束 | 详情 |
|---|---|
| Arm count | 28 total = 16 internal `vocabulary.*` + 4 `external.vocabulary.*` + 4 `published.vocabulary.*` + 4 `published.release.vocabulary.*` |
| Service lifetimes | `VocabularyService` / `VocabularyProposalService` / `TerminologyAgent` 全部 **Scoped** (同 `ABoxService` / `KnowledgeService`) |
| Existing helpers reused | `RejectIfExtractionActiveAsync` (B5) · `WriteAuditAsync` (B6a) · `DiffNQuads` (B6a) · `KnowledgeSystemAccessService.HasAtLeastAsync` (B7c) · `FakeChatClientFactory.Default` (B6b) · `[Collection(ExtractionTestCollection.Name)]` (B6b) |
| Existing helpers NOT touched | `SkosManager` 方法签名不动 · `TerminologyService.SyncAsync` 不动 · `TermProposalEntity` schema 不动 · `VocabularyController` / `ExternalApiController` / `PublishedController` 路由不动 |
| Role gates | 写 endpoint 走 `RequireRoleAsync(KSRole.Writer)` (同 `ABoxService.FixViolationAsync`);读 endpoint 走 `RequireRoleAsync(KSRole.Reader)` |
| 409 envelope | Mutating service 调 `RejectIfExtractionActiveAsync(ksId)` → 抛 `GraphWriteConflictException` → `FastApiErrorMiddleware.cs:59` 出 409 + `{detail: {error}}` |
| Audit `Added/Removed` | Mutating service 走 `StoreWrapper.DumpNQuads(pre)` + 调 `SkosManager.X` + `StoreWrapper.DumpNQuads(post)` + `DiffNQuads` → 写 `AuditEventEntity.Added/Removed` byte[]。`Reject` 不 dump(只改 TermProposal row) |
| Wire DTO 形状 | 已有 `SkosView` / `SkosConceptView` / `SkosSchemeView` / `SkosMatch` / `SkosConceptPage` record 上加 `[JsonPropertyName]`(snake_case)。**不**新建 wire DTO |
| External/Published | 只读 12 arms 直接调 `VocabularyService` read 方法(走 Reader gate)。**不**复制 internal arms 逻辑。HTTP test 1-2 smoke test 即可 |
| TerminologyAgent 事务边界 | 只 insert `TermProposalEntity` rows(status=pending)。**不**写 RDF graph。`AcceptProposal` 才动 graph |
| Existing test 兼容 | B6a/B6b/Block 7 系列已有 lower-level test (SkosManagerTests, ABoxValidationApiTests, ExtractionStateTests 等) 不能 break。Task 1 实施前先 `grep -rn "Assert\.Equal.*\.Title\|Assert\.Equal.*\.DisplayLabel" tests/` 验证 PascalCase 字段名没被 strict 比 |
| Build | `dotnet build -c Release` 0 warning 0 error |
| Final regression | 期望 333-334 / 335 passing(1 pre-existing fail 同 B6b:`AuthenticationContractTests.Me_with_valid_session_returns_user`,Block 11 `is_admin` 命名 bug)。Block 8 增加 ~10 个新 test |

---

## Task 1: Foundations — `[JsonPropertyName]` on SkosView records + `FakeChat.EnqueueTerminologyProposal`

**Files:**
- Modify: `src/OnToPilot/Ontology/SkosManager.cs` (lines 82-138)
- Modify: `src/OnToPilot.Tests/Extraction/FakeChat.cs`

**Interfaces:**
- Consumes: 现有 `SkosView` / `SkosConceptView` / `SkosSchemeView` / `SkosMatch` / `SkosConceptPage` records (already PascalCase)
- Produces: snake_case wire JSON via `[JsonPropertyName]` attribute (no runtime impact)
- Produces: `FakeChat.EnqueueTerminologyProposal(int count = 3) → FakeChat` chaining method

### Step 1: 验证现有 lower-level test 不依赖 PascalCase wire 字段名

```bash
cd "e:/GitHub/ontopilot"
grep -rn "Assert\.Equal.*\.Title\|Assert\.Equal.*\.DisplayLabel\|Assert\.Equal.*\.PrefLabel\|Assert\.Equal.*\.AltLabel" "src/OnToPilot.Tests/" | head -30
```

预期：空输出或只命中 lower-level SkosManager unit test (不命中 HTTP wire shape)。

如果 grep 命中 strict PascalCase assertion,**停**,记录到 plan 偏离,后续 task 处理(那些 test 要么用 record field accessor,要么容忍 snake_case 化)。

### Step 2: 在 5 个 record 上加 `[JsonPropertyName]` snake_case

读取 `src/OnToPilot/Ontology/SkosManager.cs` line 82-138,逐个 record 加属性。

**`SkosConceptView`** (line 82-99) — 加这些 attribute (PascalCase → snake_case):
- `Iri` → `"iri"`
- `SchemeIri` → `"scheme_iri"`
- `PrefLabels` → `"pref_labels"`
- `AltLabels` → `"alt_labels"`
- `HiddenLabels` → `"hidden_labels"`
- `DisplayLabel` → `"display_label"`
- `Description` → `"description"`
- `Notation` → `"notation"`
- `Broader` → `"broader"`
- `Related` → `"related"`
- `BroaderLabels` → `"broader_labels"`
- `RelatedLabels` → `"related_labels"`
- `MappedEntityIri` → `"mapped_entity_iri"`
- `Status` → `"status"`
- `Origin` → `"origin"`
- `CreatedAt` → `"created_at"`
- `ModifiedAt` → `"modified_at"`

**`SkosSchemeView`** (line 102-112):
- `Iri` → `"iri"`
- `Title` → `"title"`
- `Titles` → `"titles"`
- `Description` → `"description"`
- `Descriptions` → `"descriptions"`
- `DefaultLanguage` → `"default_language"`
- `Origin` → `"origin"`
- `CreatedAt` → `"created_at"`
- `ModifiedAt` → `"modified_at"`
- `ConceptCount` → `"concept_count"`

**`SkosView`** (line 115-118):
- `Schemes` → `"schemes"`
- `Concepts` → `"concepts"`
- `Stats` → `"stats"`

**`SkosStats`** (line 121-126):
- `SchemeCount` → `"scheme_count"`
- `ConceptCount` → `"concept_count"`
- `LabelCount` → `"label_count"`
- `MappedCount` → `"mapped_count"`
- `UnmappedCount` → `"unmapped_count"`

**`SkosMatch`** (line 129-133):
- `Concept` → `"concept"`
- `MatchedLabel` → `"matched_label"`
- `MatchType` → `"match_type"`
- `Score` → `"score"`

**`SkosConceptPage`** (line 136-138):
- `Items` → `"items"`
- `Total` → `"total"`

**`SkosLabel`** (line 57) 也加:
- `Value` → `"value"`
- `Language` → `"language"`

### Step 3: 编译验证

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
```

预期：0 warning 0 error。

### Step 4: 跑 SkosManagerTests 验证 record 加 attribute 不破坏 lower-level

```bash
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~SkosManagerTests|FullyQualifiedName~RdfRoundTripTests"
```

预期：全绿(数量参考 Step 1 grep 输出 — 大约 8-15 个 test)。

### Step 5: 在 `FakeChat.cs` 加 `EnqueueTerminologyProposal`

读取 `src/OnToPilot.Tests/Extraction/FakeChat.cs` 找到现有 `EnqueueValidDelta` / `EnqueueValidABoxDelta` 方法,加新方法:

```csharp
/// <summary>
/// Enqueue N LLM replies shaped like a terminology proposal batch. Each reply
/// is a JSON object with one proposal entry the TerminologyAgent can parse
/// into a TermProposal row. Count defaults to 3.
/// </summary>
public FakeChat EnqueueTerminologyProposal(int count = 3)
{
    for (int i = 0; i < count; i++)
    {
        var json = $$"""
        {
          "term": "term-{{i}}",
          "action": "create",
          "scheme_iri": "http://example.org/scheme",
          "pref_label": "Term {{i}}",
          "alt_labels": ["alt-{{i}}"],
          "description": "Auto-suggested term {{i}}",
          "reason": "extracted from chunk {{i}}",
          "confidence": 0.85,
          "evidence": ["chunk-{{i}}"],
          "source_chunk_ids": [{{i}}]
        }
        """;
        Enqueue(json);
    }
    return this;
}
```

> 实际 proposal shape 由 TerminologyAgent 实现决定。**若 Task 4 实施时发现 shape 不同,允许微调**。这是设计级 placeholder,Task 4 实施者会看 `backend/app/ontology/terminology_agent.py` 确认。

### Step 6: 编译验证 + 跑现有 extraction tests

```bash
dotnet build src/OnToPilot.Tests/OnToPilot.Tests.csproj -c Debug
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Extraction"
```

预期：21/21 passing (B6b 状态)。

### Step 7: Commit Task 1

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/SkosManager.cs \
        src/OnToPilot.Tests/Extraction/FakeChat.cs
git commit -m "feat(vocabulary): snake_case wire shape on SkosView records + propose fixture

Adds [JsonPropertyName] attributes to SkosView / SkosConceptView /
SkosSchemeView / SkosStats / SkosMatch / SkosConceptPage / SkosLabel so
dispatcher JSON output matches the wire shape Python vocabulary.py emits.
No runtime impact on SkosManager methods; this is purely a serializer
configuration. Existing SkosManagerTests and RdfRoundTripTests still
pass — they assert on record fields, not wire strings.

Also adds FakeChat.EnqueueTerminologyProposal(int count = 3) for the
upcoming TerminologyAgent HTTP contract (Task 4 / Task 5). The exact JSON
shape is provisional and will be matched against the Python
terminology_agent.suggest output when Task 4 lands."
```

---

## Task 2: `VocabularyService` (16 methods, Scoped) + DI registration

**Files:**
- Create: `src/OnToPilot/Ontology/VocabularyService.cs`
- Create: `src/OnToPilot/Ontology/VocabularyServiceCollectionExtensions.cs`
- Modify: `src/OnToPilot/Program.cs`

**Interfaces:**
- Consumes: `SkosManager` (singleton, B6a) · `OnToPilotDbContext` · `StoreWrapper` (singleton) · `TimeProvider` · `KnowledgeSystemAccessService` (B7c) · `KsContext.FromEntity(KnowledgeSystemEntity)`
- Produces: 16 public methods (read + write + sync)

### Step 1: 创建 `VocabularyService.cs` 文件骨架

读取 `src/OnToPilot/Ontology/ABoxService.cs` line 1-80 学习依赖注入 + `RequireRoleAsync` + `WriteAuditAsync` + `DiffNQuads` 模板(B7c 已建立)。

创建 `src/OnToPilot/Ontology/VocabularyService.cs`,包含:

```csharp
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Storage;

namespace OnToPilot.Ontology;

/// <summary>
/// Scoped service that mediates vocabulary CRUD + read endpoints for one
/// knowledge system. Wraps <see cref="SkosManager"/> methods, runs each
/// write through the extraction guard + role gate + audit pre/post diff
/// (B7c ABoxService pattern).
///
/// <para>Read methods resolve role via <see cref="KnowledgeSystemAccessService"/>
/// to <c>KSRole.Reader</c>; write methods require <c>KSRole.Writer</c>.
/// All write methods also call <c>RejectIfExtractionActiveAsync</c> first
/// so concurrent extraction jobs return 409 via <c>FastApiErrorMiddleware</c>.</para>
/// </summary>
public sealed class VocabularyService
{
    private readonly SkosManager _skos;
    private readonly StoreWrapper _store;
    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;
    // ... audit / capture / diff helpers below
}
```

### Step 2: 实现 16 个方法

按以下签名(顺序按 Python `vocabulary.py` 排列):

```csharp
// 读 (Reader gate)
public async Task<SkosView> GetVocabularyAsync(KnowledgeSystemEntity ks, Actor actor, CancellationToken ct);
public async Task<IReadOnlyList<SkosSchemeView>> ListSchemesAsync(KnowledgeSystemEntity ks, Actor actor, CancellationToken ct);
public async Task<SkosConceptPage> ListConceptsAsync(KnowledgeSystemEntity ks, string? schemeIri, string? q, string? status, string? mapping, string? origin, int limit, int offset, Actor actor, CancellationToken ct);
public async Task<(IReadOnlyList<SkosMatch> Items, int Total)> ResolveTermAsync(KnowledgeSystemEntity ks, string q, string? language, int limit, Actor actor, CancellationToken ct);
public Task<byte[]> ExportVocabularyAsync(KnowledgeSystemEntity ks, string fmt, Actor actor, CancellationToken ct);

// 写 scheme (Writer gate + extraction guard + audit diff)
public async Task<SkosSchemeView> CreateSchemeAsync(KnowledgeSystemEntity ks, SkosSchemeData data, Actor actor, CancellationToken ct);
public async Task<SkosSchemeView> UpdateSchemeAsync(KnowledgeSystemEntity ks, string iri, SkosSchemeData data, Actor actor, CancellationToken ct);
public async Task<(string DeletedIri, int RemovedTriples)> DeleteSchemeAsync(KnowledgeSystemEntity ks, string iri, Actor actor, CancellationToken ct);

// 写 concept (Writer gate + extraction guard + audit diff)
public async Task<SkosConceptView> CreateConceptAsync(KnowledgeSystemEntity ks, string schemeIri, SkosConceptData data, Actor actor, CancellationToken ct);
public async Task<SkosConceptView> UpdateConceptAsync(KnowledgeSystemEntity ks, string iri, SkosConceptData data, Actor actor, CancellationToken ct);
public async Task<(string DeletedIri, int RemovedTriples)> DeleteConceptAsync(KnowledgeSystemEntity ks, string iri, Actor actor, CancellationToken ct);

// Sync (Writer gate + extraction guard + audit diff)
public async Task<TerminologyResult> SyncAsync(KnowledgeSystemEntity ks, Actor actor, CancellationToken ct);
```

实施细节 (B7c `ABoxService` 模式):
- 私有 `RequireRoleAsync(KSRole, KnowledgeSystemEntity, Actor, ct)` 调 `_access.HasAtLeastAsync`
- 私有 `RejectExtractionAsync(KnowledgeSystemEntity, ct)` 调现有 `RunWithExtractionGuardAsync` 或 `RejectIfExtractionActiveAsync`(查 `KnowledgeService.cs:115+`)
- 私有 `WriteAuditAsync(ks, action, summary, actor, detail, added, removed, ct)` 写 `AuditEventEntity`(同 B6a `OntologyService`)
- 私有 `DiffNQuads(byte[] pre, byte[] post) → (byte[] added, byte[] removed)` — 复用 B6a `OntologyService.cs` 的同款 helper(若存在)或 inline 写
- `ExportVocabularyAsync` — 用 `_store.SerializeGraph(ks.VocabularyGraph, fmt)` (查 `StoreWrapper` 是否有此方法;若无加一个最小 stub 返回 `byte[]` turtle 序列化)

### Step 3: 创建 `VocabularyServiceCollectionExtensions.cs`

照抄 `src/OnToPilot/Ontology/ValidationDecisionServiceCollectionExtensions.cs` 或 `ConflictServiceCollectionExtensions.cs` 模板:

```csharp
using OnToPilot.Ontology;

namespace OnToPilot.Ontology;

public static class VocabularyServiceCollectionExtensions
{
    public static IServiceCollection AddVocabularyServices(this IServiceCollection services)
    {
        services.AddScoped<VocabularyService>();
        return services;
    }
}
```

### Step 4: 注册到 `Program.cs`

读取 `src/OnToPilot/Program.cs` 找到 `AddExtractionServices()` 调用(在 `AddValidationDecisionServices()` 之后),加:

```csharp
builder.Services.AddVocabularyServices();
```

### Step 5: 编译验证

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
```

预期：0 warning 0 error。

### Step 6: 跑现有测试验证 VocabularyService 不破坏 B6a/B6b/B7

```bash
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Ontology|FullyQualifiedName~Extraction|FullyQualifiedName~AbuseOfKnowledge|FullyQualifiedName~ABox"
```

预期：所有 B6a/B6b/B7 测试仍然绿。新 service 加进 DI 不应影响其他 service。

### Step 7: Commit Task 2

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/VocabularyService.cs \
        src/OnToPilot/Ontology/VocabularyServiceCollectionExtensions.cs \
        src/OnToPilot/Program.cs
git commit -m "feat(vocabulary): VocabularyService with 16 methods + DI

Scoped VocabularyService wraps SkosManager methods + extraction guard +
Reader/Writer role gate + audit pre/post diff (B7c ABoxService pattern).

Read methods: GetVocabularyAsync / ListSchemesAsync / ListConceptsAsync /
ResolveTermAsync / ExportVocabularyAsync (all Reader role).

Write methods: 5 scheme/concept CRUD + SyncAsync (all Writer role +
extraction-active guard + audit pre/post DumpNQuads diff).

Registered via AddVocabularyServices() in Program.cs after
AddExtractionServices(). Existing B6a/B6b/B7 tests still pass — no
behavior changes outside the new service."
```

---

## Task 3: `VocabularyProposalService` (list/accept/reject) + DI

**Files:**
- Create: `src/OnToPilot/Ontology/VocabularyProposalService.cs`
- Modify: `src/OnToPilot/Ontology/VocabularyServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `VocabularyService` (Task 2) · `OnToPilotDbContext.TermProposals` · `SkosManager`
- Produces: `ListProposalsAsync` / `AcceptProposalAsync` / `RejectProposalAsync`

### Step 1: 创建 `VocabularyProposalService.cs`

照抄 `src/OnToPilot/Ontology/ValidationDecisionService.cs` 模板(B7c 已 wire)。

```csharp
public sealed class VocabularyProposalService
{
    private readonly OnToPilotDbContext _db;
    private readonly VocabularyService _vocab;
    private readonly SkosManager _skos;
    private readonly StoreWrapper _store;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;

    public VocabularyProposalService(
        OnToPilotDbContext db,
        VocabularyService vocab,
        SkosManager skos,
        StoreWrapper store,
        TimeProvider clock,
        KnowledgeSystemAccessService access)
    {
        // ...
    }
}
```

### Step 2: 实现 3 个方法

```csharp
// List (Reader gate) — 直接 SQL query TermProposalEntity
public async Task<(IReadOnlyList<TermProposalEntity> Items, int Total)>
    ListProposalsAsync(KnowledgeSystemEntity ks, string status, string? q, int limit, int offset, Actor actor, CancellationToken ct);

// Accept (Writer gate + extraction guard) — 调 SkosManager.CreateConcept/UpdateConcept + audit
public async Task<(TermProposalEntity Proposal, SkosConceptView Concept)>
    AcceptProposalAsync(KnowledgeSystemEntity ks, long proposalId, IReadOnlyDictionary<string, object?>? payload, string note, Actor actor, CancellationToken ct);

// Reject (Writer gate, **no** extraction guard — 跟 Python 一致)
public async Task<TermProposalEntity>
    RejectProposalAsync(KnowledgeSystemEntity ks, long proposalId, string note, Actor actor, CancellationToken ct);
```

实施细节:
- `ListProposalsAsync` SQL 同 Python `_proposal_out` — 用 `db.TermProposals.Where(...).OrderByDescending(CreatedAt).Skip(offset).Take(limit)`,然后 SQL count。**注意**: SQLite DateTimeOffset ORDER BY 已知有 bug(B7c 修过),materialize first + client sort
- `AcceptProposalAsync` 调 `RejectExtractionAsync` → 调 `SkosManager.CreateConcept` / `UpdateConcept`(看 `proposal.Action`)→ 写 `TermProposalEntity.Status = "accepted"` / `ResolvedBy = actor.Username` / `ResolvedAt = utcnow` → `WriteAuditAsync("terminology.accept", ...)`
- `RejectProposalAsync` 写 `TermProposalEntity.Status = "rejected"` / `ResolvedBy` / `ResolvedAt` → `WriteAuditAsync("terminology.reject", ...)`,**不**调 SkosManager / 不 dump quads(只改 row)
- **Wire DTO**:`TermProposalOut` record 加 snake_case `[JsonPropertyName]`,字段同 Python `_proposal_out`(id / action / term / target_iri / target_label / status / payload / confidence / reason / evidence / source_chunk_ids / extraction_job_id / proposed_by / resolved_by / resolution_note / created_at / resolved_at)

### Step 3: 注册到 `VocabularyServiceCollectionExtensions`

修改 `AddVocabularyServices()`:

```csharp
public static IServiceCollection AddVocabularyServices(this IServiceCollection services)
{
    services.AddScoped<VocabularyService>();
    services.AddScoped<VocabularyProposalService>();
    return services;
}
```

### Step 4: 编译验证

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
```

预期：0 warning 0 error。

### Step 5: 跑测试

```bash
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Vocabulary|FullyQualifiedName~Extraction|FullyQualifiedName~Ontology"
```

预期：现有 Vocabulary + Extraction + Ontology 测试全绿(注意:`VocabularyApiTests` 还不存在 — 那是 Task 6 范围)。

### Step 6: Commit Task 3

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Ontology/VocabularyProposalService.cs \
        src/OnToPilot/Ontology/VocabularyServiceCollectionExtensions.cs
git commit -m "feat(vocabulary): VocabularyProposalService for TermProposal lifecycle

Scoped VocabularyProposalService implements 3 endpoints:

- ListProposalsAsync (Reader gate): SQL query TermProposals with status
  filter + q + pagination. Materialize first + client sort to avoid
  SQLite DateTimeOffset ORDER BY limitation (B7c root-cause fix pattern).
- AcceptProposalAsync (Writer + extraction guard): applies payload to
  SkosManager (create/update concept based on proposal action) + audit
  + TermProposalEntity.Status='accepted' + ResolvedBy/ResolvedAt.
- RejectProposalAsync (Writer only, no extraction guard — matches
  Python): TermProposalEntity.Status='rejected' + audit. No RDF writes.

Registered in AddVocabularyServices() alongside VocabularyService. Existing
B6a/B6b/B7 tests still pass."
```

---

## Task 4: `TerminologyAgent` (LLM-driven propose) + DI

**Files:**
- Create: `src/OnToPilot/Extraction/TerminologyAgent.cs`
- Modify: `src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs` (B6b Task 2 创建)

**Interfaces:**
- Consumes: `IChatClientFactory` (B6b) · `OnToPilotDbContext` · `PromptSnapshotService` · `TimeProvider`
- Produces: `SuggestAsync(ks, schemeIri, chunkIds, model, ct) → IReadOnlyList<TermProposalEntity>`

### Step 1: 创建 `TerminologyAgent.cs`

读取 `src/OnToPilot/Extraction/TerminologyService.cs` 学习 SKOS vocabulary graph context。

```csharp
public sealed class TerminologyAgent
{
    private readonly IChatClientFactory _chatFactory;
    private readonly OnToPilotDbContext _db;
    private readonly PromptSnapshotService _prompts;
    private readonly TimeProvider _clock;

    public TerminologyAgent(...) { /* ctor */ }

    /// <summary>
    /// Run an LLM-driven propose pass. Calls IChatClientFactory.Create() with
    /// the supplied model, sends chunks + scheme context, parses JSON
    /// proposals, and inserts TermProposalEntity rows with Status=pending.
    /// Does NOT touch the RDF graph (graph writes happen at AcceptProposal
    /// time).
    /// </summary>
    public async Task<IReadOnlyList<TermProposalEntity>> SuggestAsync(
        KnowledgeSystemEntity ks,
        string schemeIri,
        IReadOnlyList<long> chunkIds,
        string? model,
        CancellationToken ct);
}
```

### Step 2: 实现 SuggestAsync

照抄 `backend/app/ontology/terminology_agent.py:suggest()` 行为:
1. 加载 chunks (从 `db.Chunks.Where(c => chunkIds.Contains(c.Id))`)
2. 调 `_chatFactory.Create(LlmProviderConfig { Provider, Model = model ?? default, Endpoint, ApiKey })` 拿 `IChatClient`
3. 拿 prompt template from `_prompts.GetPromptSnapshotAsync(ks.Id, "terminology.propose", ct)`
4. 拼 message (chunks + scheme context + system prompt)
5. `await chat.GetResponseAsync(messages, ct)` 拿 LLM 输出
6. Parse JSON `{"proposals": [...]}` → 每条 insert `TermProposalEntity { Status = "pending", Action, Term, TargetIri = null (or computed), Payload = JsonDocument.Parse(...), ProposedBy = "terminology-agent", Confidence, Reason, Evidence = JsonDocument.Parse(evidence), SourceChunkIds = JsonDocument.Parse(chunkIds), ExtractionJobId = null, Signature = sha256(ksId|term|action) }`
7. 调 `_db.SaveChangesAsync(ct)`
8. 返回 rows

### Step 3: 注册到 `ExtractionServiceCollectionExtensions`

修改 `src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs`(B6b Task 2 创建)的 `AddExtractionServices()`:

```csharp
public static IServiceCollection AddExtractionServices(this IServiceCollection services)
{
    // ... 已有 9 个 registration ...
    services.AddScoped<TerminologyAgent>();
    return services;
}
```

### Step 4: 编译 + 跑测试

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Extraction"
```

预期：0 warning 0 error;21/21 extraction tests passing (B6b 状态)。

### Step 5: Commit Task 4

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Extraction/TerminologyAgent.cs \
        src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs
git commit -m "feat(vocabulary): TerminologyAgent for LLM-driven propose

Scoped TerminologyAgent.SuggestAsync(ks, schemeIri, chunkIds, model, ct):

- Loads chunks from db.Chunks
- Calls IChatClientFactory.Create() with model config
- Reads prompt template from PromptSnapshotService
- Sends chunks + scheme context to LLM, parses {"proposals": [...]}
- Inserts TermProposalEntity rows with Status='pending'
- Does NOT write to RDF graph (AcceptProposal writes graph)

Mirrors backend/app/ontology/terminology_agent.py behavior. Registered
alongside TerminologyService in AddExtractionServices(). B6b's
FakeChatClientFactory.Default + FakeChat.EnqueueTerminologyProposal
provide the test fixture."
```

---

## Task 5: Dispatcher wire-up (28 arms + typed resolvers + helpers)

**Files:**
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`

**Interfaces:**
- Consumes: `VocabularyService` (Task 2) · `VocabularyProposalService` (Task 3) · `TerminologyAgent` (Task 4) — all scoped, resolved via existing `Resolve<T>()` or new typed helpers
- Produces: 16 internal `vocabulary.*` arms wired + 4 `external.vocabulary.*` arms wired + 4 `published.vocabulary.*` arms wired + 4 `published.release.vocabulary.*` arms wired

### Step 1: 加 typed resolver

读取 `InternalOperationDispatcher.cs:1288-1289` (B6b Task 3 加的 `ResolveExtractionOrchestrator()` 模式),加:

```csharp
private VocabularyService? ResolveVocabularyService()
{
    var scope = _serviceProvider.CreateScope();
    return scope.ServiceProvider.GetService<VocabularyService>();
}

private VocabularyProposalService? ResolveVocabularyProposalService()
{
    var scope = _serviceProvider.CreateScope();
    return scope.ServiceProvider.GetService<VocabularyProposalService>();
}

private TerminologyAgent? ResolveTerminologyAgent()
{
    var scope = _serviceProvider.CreateScope();
    return scope.ServiceProvider.GetService<TerminologyAgent>();
}
```

或者用现有的 `Resolve<T>()` 模式(若存在)。**沿用 B6b 风格优先**。

### Step 2: 加 16 internal helper

```csharp
private async Task<object?> InvokeVocabularyGetAsync(InternalRequest request, CancellationToken ct)
{
    var svc = ResolveVocabularyService();
    if (svc is null) throw new InvalidOperationException("VocabularyService not registered.");
    var (ks, actor) = await ResolveKsAndActorAsync(request, ct);
    return await svc.GetVocabularyAsync(ks, actor, ct);
}

private async Task<object?> InvokeVocabularyCreateConceptAsync(InternalRequest request, CancellationToken ct)
{
    var svc = ResolveVocabularyService();
    if (svc is null) throw new InvalidOperationException("VocabularyService not registered.");
    var (ks, actor) = await ResolveKsAndActorAsync(request, ct);
    var body = DeserializeBody<Dictionary<string, object?>>(request);
    var data = VocabularyBodyParser.ParseConceptData(body);
    return await svc.CreateConceptAsync(ks, body["scheme_iri"]?.ToString() ?? "", data, actor, ct);
}

// ... 14 more helpers, one per arm
```

实施细节:
- `ResolveKsAndActorAsync` — 查现有 dispatcher helper(可能叫 `ResolveKnowledgeSystemAsync` + `Actor.FromRequest(request)`)
- `VocabularyBodyParser` — 新 internal helper,parse `Dictionary<string, object?>` → `SkosSchemeData` / `SkosConceptData` record(参考 Python `body.model_dump()` 行为)
- Null guards matches B6b `ResolveExtractionOrchestrator` 模式

### Step 3: 替换 16 internal arms (lines 194-209)

读取 `InternalOperationDispatcher.cs:194-209`,逐 arm 替换:

```csharp
"vocabulary.get"           => InvokeVocabularyGetAsync(request, cancellationToken),
"vocabulary.delete_concept" => InvokeVocabularyDeleteConceptAsync(request, cancellationToken),
"vocabulary.list_concepts"  => InvokeVocabularyListConceptsAsync(request, cancellationToken),
"vocabulary.update_concept" => InvokeVocabularyUpdateConceptAsync(request, cancellationToken),
"vocabulary.create_concept" => InvokeVocabularyCreateConceptAsync(request, cancellationToken),
"vocabulary.export"         => InvokeVocabularyExportAsync(request, cancellationToken),
"vocabulary.list_proposals" => InvokeVocabularyListProposalsAsync(request, cancellationToken),
"vocabulary.accept_proposal"=> InvokeVocabularyAcceptProposalAsync(request, cancellationToken),
"vocabulary.reject_proposal"=> InvokeVocabularyRejectProposalAsync(request, cancellationToken),
"vocabulary.resolve_term"   => InvokeVocabularyResolveTermAsync(request, cancellationToken),
"vocabulary.delete_scheme"  => InvokeVocabularyDeleteSchemeAsync(request, cancellationToken),
"vocabulary.list_schemes"   => InvokeVocabularyListSchemesAsync(request, cancellationToken),
"vocabulary.update_scheme"  => InvokeVocabularyUpdateSchemeAsync(request, cancellationToken),
"vocabulary.create_scheme"  => InvokeVocabularyCreateSchemeAsync(request, cancellationToken),
"vocabulary.suggest_terms"  => InvokeVocabularySuggestTermsAsync(request, cancellationToken),
"vocabulary.sync"           => InvokeVocabularySyncAsync(request, cancellationToken),
```

### Step 4: 替换 12 external/published arms (lines 283-286, 297-300, 309-312)

External (4) — 都直接调 read service:
```csharp
"external.vocabulary.concepts"  => InvokeExternalVocabularyListConceptsAsync(request, cancellationToken),
"external.vocabulary.export"    => InvokeExternalVocabularyExportAsync(request, cancellationToken),
"external.vocabulary.resolve"   => InvokeExternalVocabularyResolveAsync(request, cancellationToken),
"external.vocabulary.schemes"   => InvokeExternalVocabularyListSchemesAsync(request, cancellationToken),
```

Published (4) — 同上但走 published path:
```csharp
"published.vocabulary.concepts"  => InvokePublishedVocabularyListConceptsAsync(request, cancellationToken),
"published.vocabulary.export"    => InvokePublishedVocabularyExportAsync(request, cancellationToken),
"published.vocabulary.resolve"   => InvokePublishedVocabularyResolveAsync(request, cancellationToken),
"published.vocabulary.schemes"   => InvokePublishedVocabularyListSchemesAsync(request, cancellationToken),
```

Release published (4):
```csharp
"published.release.vocabulary.concepts"  => InvokePublishedReleaseVocabularyListConceptsAsync(request, cancellationToken),
"published.release.vocabulary.export"    => InvokePublishedReleaseVocabularyExportAsync(request, cancellationToken),
"published.release.vocabulary.resolve"   => InvokePublishedReleaseVocabularyResolveAsync(request, cancellationToken),
"published.release.vocabulary.schemes"   => InvokePublishedReleaseVocabularyListSchemesAsync(request, cancellationToken),
```

每个 helper thin wrapper 调 VocabularyService read 方法(可能附加 releaseId 上下文处理;若 production 不需要可简化直接 delegate)。

### Step 5: 编译 + 跑现有测试

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

预期：0 warning 0 error;现有 324/325 passing 不变(dispatcher wire-up 不应破坏现有 test)。

### Step 6: Commit Task 5

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot/Integration/InternalOperationDispatcher.cs
git commit -m "feat(vocabulary): wire 28 dispatcher arms to VocabularyService + Proposal + Agent

Replaces 28 placeholder arms (16 internal vocabulary.* + 12
external/published *.vocabulary.*) with calls to the new scoped services
via typed resolvers and per-arm InvokeVocabularyXxxAsync helpers.

Read arms (5 internal + 8 external/published) route through
VocabularyService read methods (Reader role gate inside service).
Write arms (10 internal scheme/concept CRUD + sync + suggest + 2
proposals) route through VocabularyService / VocabularyProposalService /
TerminologyAgent write methods (Writer + extraction guard + audit
inside service).

Typed resolvers follow B6b's ResolveExtractionOrchestrator pattern:
ResolveVocabularyService / ResolveVocabularyProposalService /
ResolveTerminologyAgent. Null guards throw InvalidOperationException
with clear messages.

Dispatcher no longer holds placeholders; existing tests still pass."
```

---

## Task 6: HTTP contract tests (10 tests) + full regression + memory + report

**Files:**
- Create: `src/OnToPilot.Tests/Ontology/VocabularyApiTests.cs`
- Create: `src/OnToPilot.Tests/Ontology/VocabularyProposalApiTests.cs`
- Create: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-vocabulary-block8.md`
- Modify: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md`

**Interfaces:**
- Consumes: `AuthTestWebApplicationFactory` + `[Collection(ExtractionTestCollection.Name)]` (B6b) · `FakeChatClientFactory.Default` · `FakeChat.EnqueueTerminologyProposal` (Task 1) · `SeedAdminAndClientAsync` + `SeedKnowledgeSystemAsync` + `LookupKsGuid` (B6b `ExtractionRunApiTests` helpers)
- Produces: 10 HTTP contract tests

### Step 1: 写 `VocabularyApiTests.cs` (10 tests)

读取 `src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs` 学习 scaffolding (per-test `new AuthTestWebApplicationFactory()` + helpers)。

10 个 tests:

```csharp
[Collection(ExtractionTestCollection.Name)]
public sealed class VocabularyApiTests
{
    [Fact] public async Task Get_vocabulary_returns_skos_view_with_schemes_and_concepts();
    [Fact] public async Task List_concepts_with_filters_returns_paginated_page();
    [Fact] public async Task Create_concept_writes_to_vocabulary_graph_and_audit();
    [Fact] public async Task Update_concept_replaces_labels_and_writes_audit();
    [Fact] public async Task Delete_concept_removes_concept_from_graph_and_audit();
    [Fact] public async Task Create_scheme_with_extraction_active_returns_409();
    [Fact] public async Task Sync_runs_TerminologyService_and_audits_added_concepts();
    [Fact] public async Task Suggest_with_fake_chat_creates_pending_proposals();
    [Fact] public async Task External_vocabulary_concepts_with_reader_scope_returns_view();
    [Fact] public async Task Published_vocabulary_export_returns_turtle_string();
}
```

实施细节:
- 每个 test 用 `await using var app = new AuthTestWebApplicationFactory();` + `FakeChatClientFactory.Default.Reset();`
- 读现有 helpers from B6b `ExtractionRunApiTests.cs`(`SeedAdminAndClientAsync` / `SeedKnowledgeSystemAsync` / `LookupKsGuid` / `LookupKsTboxIri`)。**若这些 helpers 私有,可复制到本文件**(per-task style — 不抽 base class,YAGNI)
- `Suggest_with_fake_chat_creates_pending_proposals` 调 `FakeChatClientFactory.Default.UseClient(new FakeChat().EnqueueTerminologyProposal(3))` 然后 POST `/api/knowledge/{ksId}/vocabulary/suggest`,断言 response 含 3 个 proposals,且 `db.TermProposals.Where(...).Count() == 3` 且 `Status == "pending"`
- `Create_scheme_with_extraction_active_returns_409` 直接 seed `ExtractionJobEntity { Status = "running" }` (同 B6b test #4),然后 POST 验证 409 + `{detail: {error}}`

### Step 2: 写 `VocabularyProposalApiTests.cs` (1-2 tests)

```csharp
[Collection(ExtractionTestCollection.Name)]
public sealed class VocabularyProposalApiTests
{
    [Fact] public async Task Accept_proposal_applies_payload_and_writes_audit();
    [Fact] public async Task Reject_proposal_marks_status_rejected_and_writes_audit();
}
```

实施细节:
- seed `TermProposalEntity { Status = "pending", Action = "create", Term = "...", TargetIri = null, Payload = JsonDocument.Parse("{...}") }`,然后 POST `/api/knowledge/{ksId}/vocabulary/proposals/{id}/accept`,断言 response `{proposal, concept}` + `db.TermProposals.Find(id).Status == "accepted"`
- 类似 seed + POST reject,断言 `Status == "rejected"`

### Step 1.5: 提供 1 个完整 test body 作为 scaffold

```csharp
[Collection(ExtractionTestCollection.Name)]
public sealed class VocabularyApiTests
{
    [Fact]
    public async Task Get_vocabulary_returns_skos_view_with_schemes_and_concepts()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (admin, http, ksId) = await SeedAdminAndClientAsync(app);

        // GET /api/knowledge/{ksId}/vocabulary
        var resp = await http.GetAsync($"/api/knowledge/{ksId}/vocabulary");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("schemes").ValueKind.Should().Be(JsonValueKind.Array);
        json.GetProperty("concepts").ValueKind.Should().Be(JsonValueKind.Array);
        json.GetProperty("stats").GetProperty("scheme_count").GetInt32().Should().BeGreaterThanOrEqualTo(0);

        // snake_case 验证: 字段名应是 schemes/concepts/stats,不是 PascalCase
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("\"schemes\"").And.Contain("\"concepts\"");
        raw.Should().NotContain("\"Schemes\"").And.NotContain("\"Concepts\"");
    }

    // ... 另外 9 个 tests 同样 scaffolding
}
```

### Step 3: 跑新 tests

```bash
dotnet build src/OnToPilot.Tests/OnToPilot.Tests.csproj -c Debug
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~VocabularyApiTests|FullyQualifiedName~VocabularyProposalApiTests" \
  --no-build
```

预期：12 / 12 passing。

### Step 4: 全量回归 + Release build

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

预期：~333-334 / 335 passing(1 pre-existing fail `AuthenticationContractTests.Me_with_valid_session_returns_user` 是 Block 11 的 `is_admin` 命名 bug,不是 Block 8)。

若现有 pre-existing flake hit(`ExtractionStateTests.StartTBoxAsync_updates_processed_chunks_progress`),re-run 单 test up to 2 次。

### Step 5: 写 memory file

读取 `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-extraction-block6b.md` 学习 memory 模板风格。

创建 `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-vocabulary-block8.md`:

```markdown
---
name: ontopilot-vocabulary-block8
description: Block 8 Vocabulary wire-up (28 dispatcher arms + 3 services + 12 HTTP tests)
metadata:
  node_type: memory
  type: project
  originSessionId: <session-id>
  modified: 2026-08-19T...Z
---

Block 8 完成 (commits <sha1>..<shaN>). B8 把 vocabulary 写入面完整铺平:

- 16 internal vocabulary.* arms 接 VocabularyService (Scoped, 16 方法)
- 12 external/published *.vocabulary.* arms 接同一 VocabularyService read 方法
- VocabularyProposalService (Scoped) — TermProposal list/accept/reject
- TerminologyAgent (Scoped) — LLM-driven propose via IChatClientFactory

## 关键改动

[... 8 sections: 关键改动 / 6 设计决定 / 12 HTTP tests / 关键决定 / 复用现有模式 / 进度 / 风险 / 偏离 / Why / How to apply]

## 进度

- 全量回归: 333-334 / 335 passing
- 下一个 block: Block 9 (Resolution) / Block 10 (Releases) / Block 11 (Auth, 修 is_admin bug)
```

### Step 6: 更新 MEMORY.md index

读取 `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md`,末尾加一行:

```markdown
- [ontopilot-vocabulary-block8](ontopilot-vocabulary-block8.md) — Block 8 Vocabulary wire-up (commits ...)
```

### Step 7: Commit Task 6

```bash
cd "e:/GitHub/ontopilot"
git add src/OnToPilot.Tests/Ontology/VocabularyApiTests.cs \
        src/OnToPilot.Tests/Ontology/VocabularyProposalApiTests.cs
git commit -m "test(vocabulary): 12 HTTP-level contract tests for vocabulary surface

10 VocabularyApiTests + 2 VocabularyProposalApiTests cover:
- 5 CRUD (get/list/create/update/delete concept + scheme via SkosView)
- 1 sync (TerminologyService creates SKOS concepts from TBox classes)
- 1 propose (FakeChat → TerminologyAgent → TermProposal rows)
- 1 accept + 1 reject (VocabularyProposalService)
- 1 409 envelope (extraction-active conflict)
- 1 ext/published smoke test

Follows B6b AuthTestWebApplicationFactory + [Collection] + per-test
FakeChatClientFactory.Reset pattern. New helpers copied from B6b
ExtractionRunApiTests (no base-class refactor, YAGNI)."
```

memory file 写在 `~/.claude/projects/...`(git 仓库外),不 commit。

### Step 8: 报告用户

返回 markdown 报告,含:
- B8 commit hash(es)
- 12 / 12 新 tests passing
- ~333-334 / 335 全量回归
- 下一个 block 选项

---

## 验证清单

| 步骤 | 命令 | 预期 |
|---|---|---|
| Task 1 build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |
| Task 1 lower-level | `dotnet test --filter "FullyQualifiedName~SkosManagerTests"` | 全绿 |
| Task 1 extraction | `dotnet test --filter "FullyQualifiedName~Extraction"` | 21/21 passing |
| Task 2 build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |
| Task 2 regression | `dotnet test --filter "FullyQualifiedName~Ontology\|FullyQualifiedName~Extraction"` | B6a/B6b/B7 全绿 |
| Task 3 build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |
| Task 3 vocab+ext+onto | `dotnet test --filter "FullyQualifiedName~Vocabulary\|FullyQualifiedName~Extraction\|FullyQualifiedName~Ontology"` | 全绿 |
| Task 4 build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |
| Task 4 extraction | `dotnet test --filter "FullyQualifiedName~Extraction"` | 21/21 passing |
| Task 5 build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |
| Task 5 全量 | `dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj` | 324/325 passing (no regression) |
| Task 6 新 test | `dotnet test --filter "FullyQualifiedName~VocabularyApiTests\|FullyQualifiedName~VocabularyProposalApiTests"` | 12/12 passing |
| Task 6 全量回归 | `dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj` | 333-334 / 335 passing |
| Task 6 Release build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |

---

## 不在计划范围 (留给后续 block)

- **Block 9** — Resolution (EntityResolution status lifecycle + `documents.contribution.individual_count`)
- **Block 10** — Releases
- **Block 11** — Auth/Tokens/McpTokens (修 `is_admin` 命名 bug,让全量回归变 335/335)
- **Block 12** — Settings/Prompts/History/RdfImport/External
- **B6b deferred items**: `/extract*` Editor role gate + `JsonException → 400` mapping (用户已 adjudicated "接受现状 + 排期后续")

## 风险与回退

- **风险 1**: Task 1 加 `[JsonPropertyName]` 影响 PascalCase strict asserts — Step 1 grep 验证。**若发现 strict assert, plan 偏离处理**: 加回 PascalCase attribute alongside snake_case (record 上两个 attribute 同时存在不合法,改用自定义 `JsonConverter` 或新增 wire record)
- **风险 2**: Task 5 dispatcher wire-up 28 arms + helpers 一次提交 — 若中途部分 arm 失败定位困难。**修法**: Task 5 拆 2 个 commit(internal 16 + external/published 12),中间跑测试
- **风险 3**: `TerminologyAgent` LLM propose JSON shape 与 `FakeChat.EnqueueTerminologyProposal` 不匹配 — Task 4 实施者要查 `backend/app/ontology/terminology_agent.py` 确认 shape,可能需要微调 fixture
- **风险 4**: Task 2 `VocabularyService` 16 方法 一次写完,reviewer 可能看不出局部 bug。**修法**: 内部把 5 个 read + 5 个 write scheme + 5 个 write concept + 1 sync 共 16 方法 在 git message 里分组,reviewer 按 section 看
- **回退**: 6 个 commit(Task 1-6),任何子步失败可单步 revert。Task 5 可再拆 2 commit