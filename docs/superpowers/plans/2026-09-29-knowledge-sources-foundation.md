# 知识摄入 Source 基础阶段 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付可独立验收的 Source 数据基础：KS 默认 folder Source、按 Source 归属的手工上传，以及与解析快照分开的原始文件版本。

**Architecture:** 在现有 .NET/EF Core/PostgreSQL 服务中增加 Source 与 DocumentFileVersion，不引入 Rust 运行时。保持 `(KnowledgeSystemId, Sha256)` 去重和独立虚拟 Folder；文件版本按原始文件 SHA 排序，现有 DocumentVersionEntity 继续表示解析后文本/chunk 快照。

阶段 1 的 Document `SourceId`/`ExternalKey` 是主归属字段，不承担跨来源身份；阶段 3 同步计划新增独立 `SourceDocumentBindingEntity` 作为 `(SourceId, ExternalKey)` 的权威绑定，支持多个来源共享同一 SHA 文档。本阶段仅手工上传且不生成绑定。

**Tech Stack:** .NET 10、ASP.NET Core、EF Core/Npgsql、PostgreSQL 16、xUnit、Testcontainers PostgreSQL。

## Global Constraints

- 设计依据：`docs/superpowers/specs/2026-09-29-knowledge-ingestion-sources-design.md`；执行前核对该文件的当前版本。
- Source 与文档虚拟 Folder 独立；手动上传只能归属 `folder` Source，`ExternalKey = NULL`。
- 保留 KS 级 `(KnowledgeSystemId, Sha256)` 唯一约束；不复制 Blob，不跨 Source 创建重复文档行。
- 删除 Source 时文档 `SourceId = NULL`，文档、Blob、Folder、图谱和溯源不删除。
- `DocumentVersionEntity.ContentSha256` 是解析文本哈希；原始文件 SHA 存在新建的 `DocumentFileVersionEntity` 中。
- 本阶段不实现同步、SourceSyncRun、定时调度、外部连接器、token、前端 Source 管理或 statements push；它们分别需要后续独立计划，不能据本阶段通过宣称整份 Spec 已交付。
- 现有 Spec 是未跟踪文件；仅提交本阶段实际修改的路径，不执行 `git add .`。

## 阶段划分与文件职责

本计划只交付可用的 `folder` Source + 文件版本（阶段 1）。后续另写独立计划：阶段 2 为 Source CRUD/授权、AES-256-GCM 与 API token；阶段 3 为持久同步调度、运行历史及缺失对账；阶段 4 为 `url/rss/custom/github_issues/jira_issues/s3/gcs/webdav/notion/api/statements` 连接器和安全；阶段 5 为来源管理 UI、上传 Source 选择器、端到端验收。`azure_blob` 保留模型标识，但本轮不实现 Azure SDK adapter、不开放创建或同步，也不计入端到端验收。每个后续阶段应有自己的可运行验证门槛，不得把未实现的 kind 当成可用选项。

本阶段的所有者：

- `src/ISEStudio/Infrastructure/Persistence/Entities/SourceEntities.cs`：Source 实体及默认选择字段。
- `src/ISEStudio/Infrastructure/Persistence/Entities/DocumentFileVersionEntities.cs`：原始文件版本与解析快照关联实体。
- `src/ISEStudio/Infrastructure/Persistence/Entities/WorkspaceEntities.cs`：Document 的可空 SourceId、ExternalKey、MissingSince 与非空 IsManualUpload；Folder 原样保留。
- `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs` 与 `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`：DbSet、外键、索引和两种 provider 的约束。
- `src/ISEStudio/Infrastructure/Persistence/Migrations/`：EF 迁移和 ModelSnapshot；既有 KS 和文档的回填。
- `src/ISEStudio/Knowledge/KnowledgeService.cs`：新建 KS 时创建默认 folder Source。
- `src/ISEStudio/Documents/DocumentService.cs` 与 `src/ISEStudio/Controllers/DocumentsController.cs`：可选 SourceId 的手工上传与原始文件版本写入；移动 Folder 不改变 SourceId。
- `src/ISEStudio/Documents/DocumentVersionStore.cs`：解析快照成功时关联原始文件版本，保留既有纯文本哈希去重。
- `src/ISEStudio.IntegrationTests/Ingestion/` 与 `src/ISEStudio.Tests/Documents/DocumentApiTests.cs`：迁移、版本、上传与 API 回归测试。

---

### Task 1: Source 实体、约束与既有 KS 回填

**Files:**

- Create: `src/ISEStudio/Infrastructure/Persistence/Entities/SourceEntities.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/WorkspaceEntities.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`
- Create (EF generates): `src/ISEStudio/Infrastructure/Persistence/Migrations/` 下 `AddKnowledgeSources` 迁移 `.cs` 与 `.Designer.cs`
- Modify (EF generates): `src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs`
- Test: `src/ISEStudio.IntegrationTests/Ingestion/KnowledgeSourceSchemaTests.cs`

**Interfaces:**

- Produces `SourceEntity { Id, KnowledgeSystemId, Kind, Name, Config, Icon, CreatedAt }` and `DbSet<SourceEntity> Sources`.
- Produces nullable `DocumentEntity.SourceId`, `ExternalKey`, `MissingSince` and non-null `IsManualUpload` (migrated and manually uploaded documents true, sync-created documents false); keeps `DocumentEntity.Folder` unchanged.
- `(SourceId, ExternalKey)` is unique only when both values are non-null; Source FK uses `SetNull`, KS FK uses `Restrict`.
- The default selection order is `CreatedAt`, then `Id`; no `IsDefault` column.

- [x] **Step 1: Write failing PostgreSQL schema test.** Add a test using `PostgresGraphFixture` (same fixture style as `DocumentVersionStoreTests.cs`):

```csharp
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.IntegrationTests.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ISEStudio.IntegrationTests.Ingestion;

public sealed class KnowledgeSourceSchemaTests : IClassFixture<PostgresGraphFixture>
{
    private readonly PostgresGraphFixture _fixture;
    public KnowledgeSourceSchemaTests(PostgresGraphFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Source_can_be_removed_without_deleting_document_or_folder()
    {
        await using var services = _fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var source = new SourceEntity
        {
            KnowledgeSystemId = _fixture.KnowledgeSystemId,
            Kind = "folder",
            Name = "Manual",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Sources.Add(source);
        var document = await db.Documents.SingleAsync(d => d.KnowledgeSystemId == _fixture.KnowledgeSystemId);
        document.SourceId = source.Id;
        await db.SaveChangesAsync();
        var documentId = document.Id;
        db.Sources.Remove(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        document = await db.Documents.SingleAsync(d => d.Id == documentId);
        Assert.Null(document.SourceId);
        Assert.Null(document.ExternalKey);
        Assert.Equal("/", document.Folder);
    }
}
```

- [x] **Step 2: Run red check.** `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~KnowledgeSourceSchemaTests`；预期编译失败：`ISEStudioDbContext` 尚无 `Sources`，`DocumentEntity` 尚无 `SourceId`。
- [x] **Step 3: Introduce domain and mapping.** 新建实体（`Id` 继承 `EntityBase`）并映射到 `source`；不要公开明文凭据：

```csharp
public sealed class SourceEntity : EntityBase
{
    public Guid KnowledgeSystemId { get; set; }
    public string Kind { get; set; } = "folder";
    public string Name { get; set; } = string.Empty;
    public string Config { get; set; } = "{}";
    public string? Icon { get; set; }
    public int? SyncIntervalMinutes { get; set; }
    public string? SyncCron { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string LastSyncStatus { get; set; } = "never";
    public string? LastSyncError { get; set; }
    public int LastSyncAdded { get; set; }
    public string? IngestTokenCiphertext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

在 `DocumentEntity` 新增 `Guid? SourceId`, `string? ExternalKey`, `DateTimeOffset? MissingSince`, `bool IsManualUpload`（旧数据迁移回填 true，手动上传时 true，同步新建时 false）。DbContext 加 `DbSet<SourceEntity> Sources`。Source 映射 `source` 表，显式将 `Id/KnowledgeSystemId/Kind/Name/Config/CreatedAt` 映射为 `id/knowledge_system_id/kind/name/config/created_at`，其余 Source 属性也使用 snake_case；`Config` 映射 PostgreSQL `jsonb`（SQLite 保持 text），KS 外键 Restrict，`Kind`/`LastSyncStatus` 使用数据库 CHECK 限定 Spec 状态；本阶段仅接受 `folder` 作为可创建 kind，其余 kind 的完整 config 校验交给阶段 2/4。Document 映射时明确 `source_id`、`external_key`、`is_manual_upload` 列名、`SetNull` 外键及 `HasIndex(d => new { d.SourceId, d.ExternalKey }).IsUnique().HasFilter("external_key IS NOT NULL AND source_id IS NOT NULL")`；不得删除现有 `(KnowledgeSystemId, Sha256)` 唯一索引。

- [x] **Step 4: Generate migration and add backfill.** 在仓库根目录运行：

```powershell
dotnet ef migrations add AddKnowledgeSources --project src/ISEStudio/ISEStudio.csproj --startup-project src/ISEStudio/ISEStudio.csproj --output-dir Infrastructure/Persistence/Migrations
```

在迁移 `Up` 建表与新增外键之后，仅当 `ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"` 时执行 PostgreSQL 回填 SQL；SQLite 用于新 KS 的 HTTP 测试，没有需要转换的既有 KS 数据。迁移生成后先查看建表 SQL 的列名，并以实际名称修改下列 SQL 中的引用：

```sql
INSERT INTO source (id, knowledge_system_id, kind, name, config, last_sync_status, last_sync_added, created_at)
SELECT gen_random_uuid(), k.id, 'folder', 'Default', '{}'::jsonb, 'never', 0, k."CreatedAt"
FROM knowledgesystem k
WHERE NOT EXISTS (SELECT 1 FROM source s WHERE s.knowledge_system_id = k.id);

UPDATE document d SET source_id = s.id
FROM source s
WHERE d."KnowledgeSystemId" = s.knowledge_system_id
  AND s.kind = 'folder' AND d.source_id IS NULL;
```

不要将无 KS 的孤立文档关联到任意 Source；所有既有文档（包括孤立文档）的 `is_manual_upload` 迁移默认值为 true，新同步文档必须显式写 false。原生 SQL 不会使用 `SourceEntity` 的 C# 属性初始值；若迁移新增其他非空且无数据库默认值的列，也须在上述 `INSERT` 中显式赋值。迁移无需旧版 Source 明文凭据转换（此时仓库尚无旧版 Source）。为验证回填，在迁移集成测试里先调用 `MigrateAsync("20260908125753_ReleaseDraftPartialUnique")`，插入一个 KS、一个属 KS 文档及一个无 KS 文档，然后运行 `MigrateAsync()`；确认默认 Source 只关联属 KS 文档，Folder/SHA 未变化；再执行一次 `MigrateAsync()`，确认不会重复建 Source。此测试单独使用 `PostgreSqlBuilder`，不能复用已迁移完成的 `PostgresGraphFixture`。

- [x] **Step 5: Add migration regression test.** `KnowledgeSourceSchemaTests.cs` 用独立 `PostgreSqlBuilder` 实例，不用已播种的 fixture；先迁移到 `20260908125753_ReleaseDraftPartialUnique`，用 `ExecuteSqlRawAsync` 向 `knowledgesystem` 插入 `(id, "PublicId", "Name", "GraphIri", "BaseIri", "CreatedAt", "UpdatedAt")`，向 `document` 插入两条记录：均使用旧表列 `"KnowledgeSystemId"`，属 KS 文档填该 KS 的 id，孤立文档填 NULL；两条记录均提供 `(id, "Sha256", "OriginalFilename", "Folder", "Ext", "SizeBytes", "StoragePath", "UploadedAt")`（完整非空字段参照 `PostgresGraphFixture.SeedWorkspaceRowsAsync`），再 `MigrateAsync()`；断言默认 `Source` 的 `Kind == "folder"`、`LastSyncStatus == "never"`、`LastSyncAdded == 0`；属 KS 文档与孤立文档的 `IsManualUpload` 均为 true、原 `Folder`/SHA 均不变，仅属 KS 文档的 `SourceId` 等于该默认 Source，孤立文档的 `SourceId == null`；第二次 `MigrateAsync()` 后仍只有一个默认 Source。运行 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~KnowledgeSourceSchemaTests`，预期通过。
- [x] **Step 6: Verify unique external key and nullable manual key.** 在 fixture 测试新建两个独立文档（SHA 不同）并设置同一 `SourceId`，均为 `ExternalKey = null`；`SaveChangesAsync` 成功。第一个改为 `"same"` 并保存，第二个改为 `"same"` 时断言 `await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())`；不修改 KS+SHA 约束。运行 Step 5 命令，预期这两个测试及删除外键测试通过。
- [x] **Step 7: Commit only reviewed paths.** `git add src/ISEStudio/Infrastructure/Persistence/Entities/SourceEntities.cs src/ISEStudio/Infrastructure/Persistence/Entities/WorkspaceEntities.cs src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs src/ISEStudio/Infrastructure/Persistence/Migrations src/ISEStudio.IntegrationTests/Ingestion/KnowledgeSourceSchemaTests.cs`; `git commit -m "feat(sources): persist default source and document identity"`.

### Task 2: 新建 KS 与手工上传的默认 Source

**Files:**

- Modify: `src/ISEStudio/Knowledge/KnowledgeService.cs:151`
- Modify: `src/ISEStudio/Documents/DocumentService.cs:174`
- Modify: `src/ISEStudio/Controllers/DocumentsController.cs:62`
- Modify: `src/ISEStudio.Application/Documents/DocumentDtos.cs`
- Test: `src/ISEStudio.Tests/Documents/DocumentApiTests.cs`

**Interfaces:**

- Consumes `ISEStudioDbContext.Sources`, `DocumentEntity.SourceId` from Task 1.
- Changes `DocumentService.UploadAsync(Guid ksId, Stream content, string fileName, string? mime, long sizeBytes, string folder, Actor actor, CancellationToken ct, Guid? sourceId = null)`; adding the optional parameter last keeps existing internal call sites compiling.
- HTTP `POST /api/knowledge/{id}/documents/upload` accepts optional multipart `source_id`; no change to existing `folder` parameter or dedup response.
- `DocumentOut` exposes nullable `SourceId` as `source_id` on upload, list, detail and move responses; it describes the document's primary attribution, not every Source binding.
- The default Source is the earliest surviving `folder` Source ordered by `(CreatedAt, Id)`; if none exists, Editor upload creates one atomically; invalid, foreign-KS or non-folder source gets 400 (no Blob write).

- [x] **Step 1: Add failing HTTP tests.** Reuse `SeedAdminAndClientAsync`, `CreateKsAsync`, `UploadBytesAsync` helpers in `DocumentApiTests.cs`; add the following tests inside that existing class (add `using Microsoft.EntityFrameworkCore; using ISEStudio.Infrastructure.Persistence; using ISEStudio.Infrastructure.Persistence.Entities;`):

```csharp
[Fact]
public async Task New_knowledge_system_gets_one_folder_source_and_upload_uses_it()
{
  await using var app = new AuthTestWebApplicationFactory();
  var (client, _) = await SeedAdminAndClientAsync(app);
  var ksId = await CreateKsAsync(client, "default-source");
  using var scope = app.Services.CreateScope();
  var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
  var source = await db.Sources.SingleAsync(s => s.KnowledgeSystemId == ksId);
  Assert.Equal("folder", source.Kind);
  var result = await UploadBytesAsync(client, ksId, "note.txt", "hello"u8.ToArray(), folder: "/manual");
  Assert.Equal(HttpStatusCode.OK, result.StatusCode);
  var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.KnowledgeSystemId == ksId);
  Assert.Equal(source.Id, doc.SourceId);
  Assert.Null(doc.ExternalKey);
  Assert.True(doc.IsManualUpload);
  Assert.Equal("/manual", doc.Folder);
}

[Fact]
public async Task Same_bytes_uploaded_into_other_folder_source_preserve_first_owner()
{
  await using var app = new AuthTestWebApplicationFactory();
  var (client, _) = await SeedAdminAndClientAsync(app);
  var ksId = await CreateKsAsync(client, "two-folder-sources");
  using var scope = app.Services.CreateScope();
  var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
  var first = await db.Sources.SingleAsync(s => s.KnowledgeSystemId == ksId);
  db.Sources.Add(new SourceEntity
  {
    KnowledgeSystemId = ksId, Kind = "folder", Name = "Another", CreatedAt = DateTimeOffset.UtcNow
  });
  await db.SaveChangesAsync();
  var second = await db.Sources.SingleAsync(s => s.KnowledgeSystemId == ksId && s.Name == "Another");
  using var upload = new MultipartFormDataContent();
  upload.Add(new ByteArrayContent("same"u8.ToArray()), "file", "same.txt");
  upload.Add(new StringContent(second.Id.ToString()), "source_id");
  upload.Add(new StringContent("/first"), "folder");
  Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/knowledge/{ksId}/documents/upload", upload)).StatusCode);
  var duplicate = await UploadBytesAsync(client, ksId, "copy.txt", "same"u8.ToArray(), folder: "/second");
  Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
  var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.KnowledgeSystemId == ksId);
  Assert.Equal(second.Id, doc.SourceId);
  Assert.NotEqual(first.Id, doc.SourceId);
  Assert.Equal("/second", doc.Folder); // existing dedup moves Folder, not Source ownership
}
```

- [x] 在 `DocumentApiTests.cs` 增加 HTTP 响应回归用例：上传到第二个 folder Source 后，上传响应、文档列表与详情的 `source_id` 均为该 SourceId；把文档从 `/manual` 移动到 `/archive` 后，移动响应及重新获取的列表/详情仍返回同一 `source_id`，而 `folder` 已更新。相同 SHA 再上传到另一 folder Source 时，返回的仍是原文档及原 `source_id`；在测试数据库中预置一条属该 KS、但 `SourceId == null` 的文档，断言其列表/详情 `source_id` 为 null（无 KS 的旧孤立文档由 Task 1 迁移测试覆盖）。

- [x] 在同一 `DocumentApiTests.cs` 增加 Viewer 上传授权回归用例：为已有 KS 创建仅具 Viewer 权限的登录客户端，分别不传 `source_id` 和传该 KS 有效 `folder` SourceId 向 `/api/knowledge/{ksId}/documents/upload` 发起 multipart 请求；均断言 403、文档和文件版本数量不变且不写 Blob。Editor 使用同一 SourceId 上传仍成功，避免将合法来源误判为不可用。

- [x] **Step 2: Verify red.** `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests`；新测试应失败（新建 KS 无 Source；上传忽略 source_id），现有测试仍可通过。
- [x] **Step 3: Create default on KS creation.** `KnowledgeService.CreateAsync` 在现有 `GraphIri`/`BaseIri` 第二次 `SaveChangesAsync` 前追加 Source；为避免两个 SaveChanges 中间泄漏半成品 KS，用数据库事务包住两个 SaveChanges，提交后才做 `ProjectAsync`：

```csharp
_db.Sources.Add(new SourceEntity
{
  KnowledgeSystemId = ks.Id,
  Kind = "folder",
  Name = "Default",
  CreatedAt = ks.CreatedAt
});
await _db.SaveChangesAsync(ct).ConfigureAwait(false);
```

- [x] **Step 4: Validate source before consuming upload stream.** Controller 加 `[FromForm] Guid? source_id` 并把它作为 `sourceId` 传给 `DocumentService.UploadAsync`。在 `DocumentOut` 增加可空 `Guid? SourceId`，由 `DocumentService.Project` 统一从文档主归属投影到上传、列表、详情和移动响应；不把它当作跨 Source 绑定列表。Service 的 `_blobs.PutAsync` 之前查询 Source：

```csharp
var chosenSource = sourceId is { } requested
  ? await _db.Sources.SingleOrDefaultAsync(s => s.Id == requested && s.KnowledgeSystemId == ks.Id && s.Kind == "folder", ct)
  : await _db.Sources.Where(s => s.KnowledgeSystemId == ks.Id && s.Kind == "folder")
    .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).FirstOrDefaultAsync(ct);
if (sourceId.HasValue && chosenSource is null)
  throw new InvalidOperationException("source_id must be a folder source in this knowledge system.");
if (chosenSource is null)
{
  chosenSource = new SourceEntity { KnowledgeSystemId = ks.Id, Kind = "folder", Name = "Default", CreatedAt = _clock.GetUtcNow() };
  _db.Sources.Add(chosenSource);
  await _db.SaveChangesAsync(ct).ConfigureAwait(false);
}
```

在新 `DocumentEntity` 初始值中设置 `SourceId = chosenSource.Id`, `ExternalKey = null`, `IsManualUpload = true`；命中现有 SHA 时将 `IsManualUpload` 设为 true 并清空文档级 `MissingSince`，仅调整 `Folder`，不重指向 `chosenSource`，不修改 `ExternalKey`/已有绑定。在多请求同时首次上传的场景，用 KS 行锁/事务串行创建默认来源，避免两个默认项；在 PostgreSQL 测试中对两条并发上传检查只生成一个默认项。

- [x] **Step 5: Test invalid sources.** 在 `DocumentApiTests.cs` 新增下面的参数化测试；每种拒绝路径均不创建文档，非法 Source 在写 Blob 之前被拒绝。

```csharp
[Theory]
[InlineData("missing")]
[InlineData("foreign")]
[InlineData("url")]
public async Task Upload_rejects_source_outside_current_folder_scope(string kind)
{
  await using var app = new AuthTestWebApplicationFactory();
  var (client, _) = await SeedAdminAndClientAsync(app);
  var ksId = await CreateKsAsync(client, "invalid-source");
  var otherKs = await CreateKsAsync(client, "other-source");
  using var scope = app.Services.CreateScope();
  var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
  var sourceId = Guid.NewGuid();
  if (kind != "missing")
  {
    db.Sources.Add(new SourceEntity
    {
      Id = sourceId, KnowledgeSystemId = kind == "foreign" ? otherKs : ksId,
      Kind = kind == "url" ? "url" : "folder", Name = "Foreign or pull",
      CreatedAt = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync();
  }
  using var upload = new MultipartFormDataContent();
  upload.Add(new ByteArrayContent("reject"u8.ToArray()), "file", "reject.txt");
  upload.Add(new StringContent(sourceId.ToString()), "source_id");
  var response = await client.PostAsync($"/api/knowledge/{ksId}/documents/upload", upload);
  Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
  Assert.False(await db.Documents.AnyAsync(d => d.KnowledgeSystemId == ksId));
}
```

运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests`，预期新旧上传用例均通过。

- [x] **Step 6: Commit.** `git add src/ISEStudio/Knowledge/KnowledgeService.cs src/ISEStudio/Documents/DocumentService.cs src/ISEStudio/Controllers/DocumentsController.cs src/ISEStudio.Application/Documents/DocumentDtos.cs src/ISEStudio.Tests/Documents/DocumentApiTests.cs`; `git commit -m "feat(sources): assign manual uploads to folder sources"`.

### Task 3: 原始文件版本与解析快照保持分离

**Files:**

- Create: `src/ISEStudio/Infrastructure/Persistence/Entities/DocumentFileVersionEntities.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`
- Create (EF generates): `src/ISEStudio/Infrastructure/Persistence/Migrations/` 下 `AddDocumentFileVersions` 迁移 `.cs` 与 `.Designer.cs`
- Modify (EF generates): `src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs`
- Modify: `src/ISEStudio/Documents/DocumentService.cs`
- Modify: `src/ISEStudio/Documents/DocumentVersionStore.cs`
- Modify: `src/ISEStudio/Documents/PlainTextIngestionService.cs`
- Modify: `src/ISEStudio/Documents/DocumentIngestionJobProcessor.cs`
- Modify: `src/ISEStudio/Documents/ParserExtractionJobHandler.cs` and the existing durable parse-job enqueue path
- Modify: `src/ISEStudio.Application/Documents/DocumentVersionDtos.cs`
- Test: `src/ISEStudio.IntegrationTests/Ingestion/DocumentVersionStoreTests.cs`
- Test: `src/ISEStudio.IntegrationTests/Ingestion/DocumentBlobConcurrencyTests.cs` (PostgreSQL Testcontainers)
- Test: `src/ISEStudio.Tests/Documents/DocumentApiTests.cs`

**Interfaces:**

- Produces `DbSet<DocumentFileVersionEntity> DocumentFileVersions` and `DbSet<DocumentFileVersionSnapshotEntity> DocumentFileVersionSnapshots` (join unique by pair of IDs).
- `DocumentFileVersionEntity` has `DocumentId`, `Version`, `Sha256`, `SizeBytes`, `DateTimeOffset? DocTime`, `CreatedAt`; `(DocumentId, Version)` unique. `DocumentFileVersionSnapshotEntity` has `DocumentFileVersionId` and `DocumentVersionId`, both FKs, unique together; neither changes `DocumentVersionEntity.ContentSha256` semantics.
- Task 2's manual upload inserts initial file version with SHA from `IBlobStore.PutAsync`, but still does not auto-parse. On a parsed text snapshot, link the file version read for the raw SHA used by that parse, not an arbitrary newer current version.

- [x] **Step 1: Add failing raw-version tests.** In `DocumentApiTests.cs`, after `Upload_text_then_list_returns_it`, add a test using the class's existing auth/KS/upload helpers:

```csharp
[Fact]
public async Task Upload_writes_one_raw_version_without_creating_parsed_snapshot()
{
  await using var app = new AuthTestWebApplicationFactory();
  var (client, _) = await SeedAdminAndClientAsync(app);
  var ksId = await CreateKsAsync(client, "file-version");
  Assert.Equal(HttpStatusCode.OK,
    (await UploadBytesAsync(client, ksId, "a.txt", "abc"u8.ToArray(), folder: "/")).StatusCode);
  using var scope = app.Services.CreateScope();
  var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
  var doc = await db.Documents.SingleAsync(d => d.KnowledgeSystemId == ksId);
  var version = await db.DocumentFileVersions.SingleAsync(v => v.DocumentId == doc.Id);
  Assert.Equal(1, version.Version);
  Assert.Equal(doc.Sha256, version.Sha256);
  Assert.Equal(3, version.SizeBytes);
  Assert.Null(version.DocTime);
  Assert.Empty(await db.DocumentVersions.Where(v => v.DocumentId == doc.Id).ToListAsync());
}
```

- [x] **Step 2: Red check.** `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~Upload_writes_one_raw_version_without_creating_parsed_snapshot`；预期编译失败，因为 `DocumentFileVersions` 尚不存在。
- [x] **Step 3: Add two entities and migration.** 在 `DocumentFileVersionEntities.cs` 增加下面两个实体（均继承 `EntityBase`）：

```csharp
public sealed class DocumentFileVersionEntity : EntityBase
{
  public Guid DocumentId { get; set; }
  public int Version { get; set; }
  public string Sha256 { get; set; } = string.Empty;
  public long SizeBytes { get; set; }
  public DateTimeOffset? DocTime { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DocumentFileVersionSnapshotEntity : EntityBase
{
  public Guid DocumentFileVersionId { get; set; }
  public Guid DocumentVersionId { get; set; }
}
```

EF mapping：两个表均 FK Restrict 到其对应版本，join 以两个 ID 组合唯一；按 `DocumentId, Version` 唯一，`Version > 0`, `SizeBytes >= 0`。不要把现有 `document_version` 的解析文本 SHA 改为原始文件 SHA。运行 `dotnet ef migrations add AddDocumentFileVersions --project src/ISEStudio/ISEStudio.csproj --startup-project src/ISEStudio/ISEStudio.csproj --output-dir Infrastructure/Persistence/Migrations`。迁移仅当 `ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"` 时从当前 `document` 元数据创建版本 1；SQLite 新建数据库由上传路径写初始版本：

```sql
INSERT INTO document_file_version (id, document_id, version, sha256, size_bytes, created_at)
SELECT gen_random_uuid(), d.id, 1, d."Sha256", d."SizeBytes", d."UploadedAt"
FROM document d WHERE d."Sha256" <> '';
```

注意以 EF 生成的实际列名修正 SQL 标识符；不凭 `document_version.content_sha256` 猜测旧文件的 SHA，旧解析快照不回填 join。

- [x] **Step 4: Insert initial version at upload time.** 在 `DocumentService.UploadAsync` 中仅新文档分支，把 `doc` 与 `new DocumentFileVersionEntity { DocumentId = doc.Id, Version = 1, Sha256 = doc.Sha256, SizeBytes = doc.SizeBytes, CreatedAt = _clock.GetUtcNow() }` 同时加入 DbContext，用一个事务包住文档、版本与现有审计持久化；命中 KS/SHA 时保持原有 Folder 更新并不追加版本。手工上传与同步阶段共用按原始 SHA 的跨进程互斥约定：流先暂存并求 SHA（不将整个文件放内存）；完成默认 Source 的选择或创建后，在上传事务中先锁定并复核所选 folder Source（若已删除，显式选择返回 400，默认选择重新选取），再查找并按 ID 锁定可能命中的现存 Document，重读 SHA 后才经专用连接取得 PostgreSQL 会话级 SHA advisory lock 并写 Blob。若取得锁后发现新的现存 Document，释放 SHA 锁、回滚并按 Source、Document、SHA 的顺序重试；唯一键冲突同样回滚重试，不得持 SHA 锁等待 Document 行锁或唯一键冲突事务。持 SHA 锁直至 Blob 写入及文档/版本事务提交或回滚，在 `finally` 中释放并关闭专用连接；锁键碰撞只允许额外串行，不得导致错误共享。SQLite 测试使用等价的同进程串行化，不以其证明跨进程安全。别自动调用 `ParseAsync`。
- [x] **Step 5: Verify green.** 重跑 Step 2 命令；预期通过。再跑 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DocumentVersionStoreTests`；预期既有解析文本哈希/并发去重测试继续通过。
- [x] **Step 6: Write failing linkage test.** 在 `DocumentVersionStoreTests.cs` 新增下列测试（`using ISEStudio.Infrastructure.Persistence.Entities;`）：

```csharp
[Fact]
public async Task Two_raw_versions_can_share_one_parsed_snapshot()
{
  await using var services = _fixture.BuildServices();
  await using var scope = services.CreateAsyncScope();
  var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
  var doc = await db.Documents.SingleAsync(d => d.KnowledgeSystemId == _fixture.KnowledgeSystemId);
  var first = new DocumentFileVersionEntity { DocumentId = doc.Id, Version = 1,
    Sha256 = new string('1', 64), SizeBytes = 10, CreatedAt = DateTimeOffset.UtcNow };
  var second = new DocumentFileVersionEntity { DocumentId = doc.Id, Version = 2,
    Sha256 = new string('2', 64), SizeBytes = 11, CreatedAt = DateTimeOffset.UtcNow };
  db.DocumentFileVersions.AddRange(first, second);
  await db.SaveChangesAsync();
  var store = new DocumentVersionStore(db);
  var input = new DocumentVersionInput(_fixture.KnowledgeSystemId, doc.Id, new string('f', 64),
    [new DocumentVersionChunkInput(0, "same", 0, 4, 1)], first.Id);
  var initial = await store.RecordAsync(input, CancellationToken.None);
  var repeated = await store.RecordAsync(input with { FileVersionId = second.Id }, CancellationToken.None);
  Assert.Equal(initial.Id, repeated.Id);
  Assert.Equal(2, await db.DocumentFileVersionSnapshots.CountAsync(link => link.DocumentVersionId == initial.Id));
  await store.RecordAsync(input, CancellationToken.None);
  Assert.Equal(2, await db.DocumentFileVersionSnapshots.CountAsync(link => link.DocumentVersionId == initial.Id));
}
```

再增加延迟解析回归测试：按 A→B→A 写入同一文档的三个原始版本，保留第一版 A 入队时的文件版本 ID，等第三版成为当前版本后才执行第一版任务；断言解析快照关联第一版而非第三版。另在当前版本为 A 时执行第二版 B 的任务，断言从 B 的文件版本 SHA 读取 Blob、快照关联 B、任务可完成且当前 A 的解析状态/元数据不变；模拟 B 的 Blob 不存在或解析失败时只将 B 任务标记失败，不能将当前 A 文档标记为 failed。运行 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Two_raw_versions_can_share_one_parsed_snapshot` 及新增任务测试过滤器；预期先因 `DocumentVersionInput` 或任务载荷尚无 `FileVersionId` 而失败。

- [x] **Step 7: Implement linkage and rerun.** 在 `DocumentVersionInput` 最后增加 `Guid? FileVersionId = null`；`PlainTextIngestionService.IngestAsync` 最后增加可选 `Guid? fileVersionId = null` 并传入 input；所有由原始文件触发的持久解析任务在入队时固定 `DocumentFileVersionId` 和 SHA，更新任务载荷读取器及 `DocumentIngestionJob` 传递该 ID。`DocumentIngestionJobProcessor` 校验该版本属于目标文档、版本 SHA 与任务 SHA 相同且 Blob 字节哈希相符，从文件版本 SHA 读取 Blob 并将该确切版本 ID 传入 ingestion；不能以 `document.Sha256` 是否等于任务 SHA 决定旧版本任务成败，也不得按 `(document.Id, SHA)` 选最新版本。处理完成或失败时在事务内锁定并重读 Document，按该文档最高 Version 的文件版本 ID 判定任务是否仍为当前版本（不能仅比较 SHA，A→B→A 的第一版也不是当前版）；仅当前版本的任务可在同一事务中写文档级 `ParseStatus`、`ParseError`、解析器元数据及 chunk 计数，过期任务只更新自身状态及对应解析快照/关联，不得覆盖当前文档状态。既有无版本 ID 的历史任务仅在同一文档/SHA 恰有唯一文件版本时建立关联，多版本歧义时解析可完成但不建立错误 join，并记录非机密诊断；纯文本任务不带文件版本。`DocumentVersionStore.RecordAsync` 在所有返回分支校验 FileVersionId 的 DocumentId 与 input.DocumentId 一致，并在同一个事务内插入 join（PostgreSQL `ON CONFLICT (document_file_version_id, document_version_id) DO NOTHING`，SQLite 使用 EF 先查存在再写），新快照写入同样使用该事务；若 `_db.Database.CurrentTransaction` 已存在，复用它且不独立提交或回滚，否则由 Store 开启并拥有事务。现有快照快速返回分支若有 FileVersionId 也必须参与上述事务写 join。增加 Store 在外层事务内调用的测试：外层回滚后新快照和 join 均不存在，且不得因嵌套 `BeginTransactionAsync` 报错；重跑 Step 6 的两个命令和完整 `DocumentVersionStoreTests`，预期通过。
- [x] **Step 8: Link manual parsing too.** `DocumentService.ParseDocumentAsync` 开启并拥有同一 DbContext 的事务，在事务内锁定并重读 Document 后，按该文档最高 `Version`（不得只按 `(doc.Id, doc.Sha256)`）确定当前 `DocumentFileVersionId`，校验版本 SHA 与当前 `doc.Sha256` 一致，再读取对应 Blob 并解析；在现有 `ApplyChunksAsync` 后将固定的版本 ID 传给 `PlainTextIngestionService.IngestAsync(ks.Id, doc.Id, parsed.Text, ct, fileVersionId)`，Store 复用当前事务，chunks、快照/join、文档解析状态及审计在提交前保持原子性，不另开嵌套事务。失败时先回滚并清理变更跟踪，再在新事务中按当前文件版本 ID 复核后决定是否写文档级 failed；若解析期间当前版本改变，不能把旧版本快照或解析状态写成当前结果，须回滚并重新选择当前版本。同一文本重复手工解析时复用已有解析快照及 join；保留现有 `ChunkEntity`/ParseResponse/审计行为；解析失败时原始文件版本仍在。增加 `DocumentApiTests.cs` 测试：上传、调用 `POST /api/knowledge/{ksId}/documents/{docId}/parse`、断言该 doc 的 `DocumentVersionEntity` 一条、join 一条、`DocumentVersionChunkEntity` 非空，重复解析两者计数不变。另在 `DocumentVersionStoreTests.cs` 配置可解析真实字节的 PostgreSQL 测试服务：沿用 `PostgresGraphFixture` 的数据库，但为本测试显式注册 `DocumentService` 所需的授权/时钟/解析及任务服务，并为所有测试 scope 注入同一个隔离目录下的 `LocalCasBlobStore`（现有 `BuildServices` 每次调用会生成不同目录）；准备同一文档 A→B→A 三个文件版本及 A/B 两份真实 Blob（两个 A 版本引用同一 SHA），将文档当前 SHA 设为第三版 A，以有效 Editor 身份调用 `DocumentService.ParseAsync` 而非只调用 `DocumentVersionStore.RecordAsync`。断言只关联第三版、重复解析不增加错误 join，测试后清理隔离目录。运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests` 与 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DocumentVersionStoreTests`，预期通过。
- [x] **Step 9: Protect blob references on document deletion.** 在 `DocumentApiTests.cs` 复用已有 `Upload_same_bytes_to_two_ks_creates_two_rows`，增加断言：删除第一个 KS 的文档后，第二个 KS 对同一 SHA 的文档仍可解析；删除前后 `DocumentFileVersions` 对各自 DocumentId 的数量分别为 1/0 和 1/1。另用 `Upload_text_then_list_returns_it` 的手动解析路径构造快照，删除解析过的文档应返回成功且不影响另一 KS 的快照。`DocumentService.DeleteAsync` 在事务内先锁定并重读待删 Document 行，收集其所有原始 SHA 后，按稳定顺序经专用连接取得相同的 PostgreSQL 会话级 SHA advisory lock；不得先锁 SHA 再等待 Document。随后依次删除该文档的 file-version/snapshot join、文件版本、`DocumentVersionChunkEntity`、`DocumentVersionEntity`，再按现有路径删除当前 `ChunkEntity` 及其溯源引用和文档；提交后仍持 SHA 锁重新查询 `Documents.Sha256` 和 `DocumentFileVersions.Sha256`，两者都没有引用时才调用 Blob Remove，在 `finally` 中释放锁并关闭专用连接。不得用已提交前的快照或未持锁的查询作删除依据。在 `DocumentBlobConcurrencyTests.cs` 使用两个独立 PostgreSQL 连接与可控屏障测试上传命中现存文档、模拟遵循 Document→SHA 顺序的并发改写与删除交错时无死锁；另一上传尚未提交同 SHA 引用时，删除/回收不得移除其 Blob。真实同步改写的交叉测试留给同步阶段。执行 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DocumentBlobConcurrencyTests` 与 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests`，预期通过；SQLite 用例不替代 PostgreSQL 并发验证。
- [x] **Step 10: Verify and commit.** `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DocumentVersionStoreTests`; 运行 Step 6 的延迟任务测试；`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DocumentBlobConcurrencyTests`; `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests`; `dotnet build src/ISEStudio.sln --no-restore`；期望均返回退出码 0。仅暂存本任务明确的实体、迁移、解析服务、任务载荷读写、测试文件后提交 `feat(ingestion): retain raw file versions alongside parsed snapshots`。

## Self-Review

- Spec 覆盖：Task 1 覆盖独立 Source 与 Folder、来源外键、KS SHA 唯一约束及既有数据回填；Task 2 覆盖新 KS 默认 folder Source 和手工上传来源选择；Task 3 覆盖原始文件版本、现有解析快照去重与多对多关联、删除时 Blob 引用安全。
- 尚未覆盖：Source CRUD/授权/凭据与 token、SourceSyncRun/调度/幂等更新/缺失对账、除 `azure_blob` 外的外部 kind 连接器与 SSRF 限制、来源管理 UI/上传选择器和完整端到端验收；这些要求分别进入上文阶段 2-5 的独立计划。`azure_blob` 的 Azure SDK 集成已明确延期，不属于本轮验收。本基础阶段通过不代表 Spec 全部验收。
- 类型契约：`SourceEntity.Id` 来自 `EntityBase`；`DocumentVersionInput.FileVersionId` 为可空 Guid；文件版本 `Sha256` 表示原始字节，解析快照 `ContentSha256` 表示解析后文本；两个 SHA 不互换。
- 实施前核验：检查工作区中现有未跟踪 Spec/计划和任何用户改动；每个 task 先执行红灯测试，再写生产代码、运行绿灯测试，提交时只包含该 task 的文件。
