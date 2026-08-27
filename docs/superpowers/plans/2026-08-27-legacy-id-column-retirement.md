# Phase 3: legacy_id 列完全退役 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完全删除 24 表 `legacy_id` 列、`LegacyAddressableEntity` 基类、109 个生产 read 访问点;SystemConfig singleton 改 `IsSingleton bool` + partial UNIQUE INDEX;ExportRunner artifact path 改 PublicId 命名;单 EF migration `DropLegacyIdColumn`。

**Architecture:** 一次原子 EF migration(DropColumn 24 表 + AddColumn systemconfig.is_singleton + partial UNIQUE INDEX + backfill);24 entity 改继承 `EntityBase`(Guid Id);109 reads 全 grep + 改 `.Id` / `.PublicId` / `.IsSingleton`;ExportRunner artifact 路径去 LegacyId;Runbook 移除 legacy_id bootstrap 段。

**Tech Stack:** .NET 10, EF Core 10, Npgsql 10, Postgres 16 (生产) + SQLite (dev/test), Oxigraph 0.5.8 (RDF), xUnit, docker compose

**Spec:** [docs/superpowers/specs/2026-08-27-legacy-id-column-retirement-design.md](../specs/2026-08-27-legacy-id-column-retirement-design.md)(commit `f852dfd`)

---

## Global Constraints

(Phase 3 spec 全部 binding 约束,逐字摘自 spec §2-§9):

1. **删基类,改 `EntityBase` + `IHasId`**:24 entity 全部 `:` 或 `:` `EntityBase`(仅 `Guid Id`);`LegacyAddressableEntity.cs` 整文件删除。
2. **SystemConfig singleton 改 `IsSingleton bool`**:新增 `public bool IsSingleton { get; set; }` 字段;EF partial UNIQUE INDEX `ux_systemconfig_singleton` filter `IsSingleton = TRUE`;`SettingsService.cs:114` `LegacyId = SingletonLegacyId` → `IsSingleton = true, Id = SingletonId`。
3. **ExportRunner artifact 改 PublicId 命名**:`ExportRunner.cs:97/126/154` 删除 `job.LegacyId`;路径改 `artifacts/{publicId}/...`。旧 disk artifact 保留只读。
4. **109 reads 全部 grep 改 `.Id` / `.PublicId`**:grep `\.LegacyId\b` 命中 0(基类删除后归零);`VocabularyProposalService.cs:243/300` audit log 字符串改 `proposal.Id`。
5. **单 EF migration `DropLegacyIdColumn`**:6 个 Up 操作(AddColumn is_singleton / CreateIndex partial unique / Sql backfill / DropColumn × 24);Down() WARNING 注释 + backup columns。
6. **测试**:`LegacyIdDefaultTests.cs` 删除;新增 `SystemConfigSingletonTests.cs`(~2 Facts);`PostgresSchemaTests.cs` 加 `No_business_table_has_legacy_id_column` + `systemconfig_has_unique_singleton`。
7. **Runbook**:`docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md` §0/§3.2/§3.3/§3.5 移除 legacy_id bootstrap 段 + 改写 admin seed SQL。
8. **Smoke gate**:`docker compose build isestudio` + `docker compose run --rm isestudio-migrate` Exited 0 + `\d users` 无 legacy_id + `\d systemconfig` 有 is_singleton + `curl http://127.0.0.1:8080/api/health` 200。
9. **不触动**:`IriSqlMigrator.ColumnsToRewrite` / `IriSqlVerifier.baseline` / 历史 EF migration(`20260816140916_InitialCompatibility.cs`、`20260826111221_LegacyIdDefaultZero.cs`)/ `pre-isestudio-rename` tag (`fc06a73`) / `pre-python-retirement` tag (`8c6c884`)/ Python baseline / 前端 / API endpoint。
10. **test 套 baseline**:`851 unit + 167 contract + 57 integration = 1075`。Phase 3 后 ±2 systemconfig test。

---

## Plan Index

| Task | Phase | 主题 | Commit message |
|---|---|---|---|
| 1 | A.0 | `IHasId` + `EntityBase` 创建 + `LegacyAddressableEntity.cs` 删除 | `refactor(phase3): introduce IHasId + EntityBase, delete LegacyAddressableEntity` |
| 2 | A.1 | 24 entity 类改继承 + 删除 `LegacyId` 字段 | `refactor(phase3): 24 entities inherit EntityBase + drop LegacyId field` |
| 3 | B.1 | `SystemConfigEntity.IsSingleton` + EntityConfigurations + audit log strings | `feat(phase3): SystemConfig.IsSingleton + EF partial unique index` |
| 4 | C.1 | `ExportRunner.cs` path 改写 + `SettingsService.cs` singleton check | `refactor(phase3): ExportRunner PublicId path + SettingsService singleton check` |
| 5 | C.2 | 109 reads audit:ResolutionService / ConflictService / ConflictAgent / ReleaseService / ExportService 等 | `refactor(phase3): 109 LegacyId reads → Id/PublicId/IsSingleton` |
| 6 | D.1 | EF migration `DropLegacyIdColumn`(Up 6 ops + Down WARNING) | `feat(phase3): EF migration DropLegacyIdColumn` |
| 7 | E.1 | Test cleanup + new tests(`SystemConfigSingletonTests` + `PostgresSchemaTests` 扩展) | `test(phase3): SystemConfigSingletonTests + PostgresSchema assertions` |
| 8 | F.1 | Runbook 更新:§0/§3.2/§3.3/§3.5 移除 legacy_id bootstrap | `docs(phase3): runbook removes legacy_id bootstrap` |
| 9 | G.1 | 全套件重跑 + docker smoke + final commit | `chore(phase3): final verification + smoke PASS` |

总计 9 tasks。每 task 独立可测试、可审查、可 revert。

---

## Task 1: Phase A.0 — `IHasId` 接口 + `EntityBase` 基类 + `LegacyAddressableEntity.cs` 删除

**Files:**
- Create: `src/ISEStudio/Infrastructure/Persistence/Entities/IHasId.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Entities/EntityBase.cs`
- Delete: `src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs`

**Interfaces:**
- Consumes: 无前置依赖
- Produces:
  - `IHasId` interface(Guid Id getter/setter)
  - `EntityBase` abstract class(`public Guid Id { get; set; } = Guid.NewGuid();`)
  - 旧 `LegacyAddressableEntity` 类不存在(Task 2 起 24 entity 必须 `:` 或 `:` `EntityBase`)

- [ ] **Step 1: 创建 `IHasId.cs`**

文件 `src/ISEStudio/Infrastructure/Persistence/Entities/IHasId.cs`:

```csharp
namespace ISEStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// Marker interface for entities that carry a stable Guid primary key.
/// Phase 3 introduced this contract to replace the legacy long id inheritance.
/// </summary>
public interface IHasId
{
    Guid Id { get; set; }
}
```

- [ ] **Step 2: 创建 `EntityBase.cs`**

文件 `src/ISEStudio/Infrastructure/Persistence/Entities/EntityBase.cs`:

```csharp
namespace ISEStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// Default base class for ISEStudio persistence entities. Replaces
/// LegacyAddressableEntity (Phase 3 retired). New rows get a fresh Guid
/// when constructed; EF will replace it with a server-generated default
/// if the column is configured accordingly.
/// </summary>
public abstract class EntityBase : IHasId
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
```

- [ ] **Step 3: 删除 `LegacyAddressableEntity.cs`**

```bash
git rm src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs
```

预期:文件删除;其余 codebase 此刻仍 `:` `LegacyAddressableEntity` —— 编译会失败。

- [ ] **Step 4: 验证预期编译错误(compile-fail 是 task 内有意为之)**

```bash
cd src
dotnet build ISEStudio/ISEStudio.csproj -c Release --nologo 2>&1 | tail -20
cd ..
```

预期: 大量 `error CS0246: The type or namespace name 'LegacyAddressableEntity' could not be found`。这是 Task 2 要修复的预期状态;不要修复,直接 commit Task 1。

- [ ] **Step 5: Commit**

```bash
git add src/ISEStudio/Infrastructure/Persistence/Entities/IHasId.cs \
        src/ISEStudio/Infrastructure/Persistence/Entities/EntityBase.cs
git commit -m "refactor(phase3): introduce IHasId + EntityBase, delete LegacyAddressableEntity"
```

Capture commit SHA as `<A1_SHA>`(实为 A.0 SHA)。

---

## Task 2: Phase A.1 — 24 entity 类改继承 + 删除 `LegacyId` 字段

**Files:**
- Modify: 4 entity 文件(全部继承 `LegacyAddressableEntity` 的 entity 都集中在这几个文件,Phase 2 上下文已知):
  - `src/ISEStudio/Infrastructure/Persistence/Entities/WorkspaceEntities.cs`
  - `src/ISEStudio/Infrastructure/Persistence/Entities/OntologyEntities.cs`(如有)
  - `src/ISEStudio/Infrastructure/Persistence/Entities/ChunkEntities.cs`(如有)
  - 其他包含 `:` `LegacyAddressableEntity` 的文件(`grep -rn ': LegacyAddressableEntity' src/ISEStudio/` 完整列表)

**Interfaces:**
- Consumes: `<A1_SHA>`(`IHasId` + `EntityBase`)
- Produces: 24 entity 类全部 `:` 或 `:` `EntityBase`,每个 entity 删除 `public long LegacyId { get; set; }` 字段

- [ ] **Step 1: grep 完整列出所有继承 `LegacyAddressableEntity` 的 entity**

```bash
grep -rln ': LegacyAddressableEntity' src/ISEStudio/
```

预期 4-5 个文件。每个文件里通常有 5-8 个 entity 类。记录精确列表用于 Step 2。

- [ ] **Step 2: 批量替换 entity 类声明与字段**

对每个文件:
- 所有 `class Foo : LegacyAddressableEntity` → `class Foo : EntityBase`
- 所有 `public long LegacyId { get; set; }`(在 entity 内,非 SystemConfigEntity 中的 `SingletonLegacyId` 常量)→ 整行删除
- **不要删** `SystemConfigEntity.SingletonLegacyId` 常量(Task 3 处理)

单文件 sed 模式(逐文件手动 Edit,不要 pipeline sed —— entity 文件混合多个 entity 类):
```
Edit replace_all pattern (per file):
  old_string: ": LegacyAddressableEntity"
  new_string: ": EntityBase"
Edit replace_all pattern (per entity):
  old_string: "    public long LegacyId { get; set; }\n"
  new_string: "" (delete)
```

- [ ] **Step 3: 验证 build 成功**

```bash
cd src
dotnet build ISEStudio/ISEStudio.csproj -c Release --nologo 2>&1 | tail -20
cd ..
```

预期: 0 errors。Task 1 后的 24 `CS0246` 全部消失。如果还有错,grep `LegacyId` 排查遗漏。

- [ ] **Step 4: 验证 `LegacyId` 在 entity 文件中归零**

```bash
grep -n 'LegacyId' src/ISEStudio/Infrastructure/Persistence/Entities/*.cs
```

预期: 仅 `SystemConfigEntity.SingletonLegacyId` 常量(那是 long 静态常量,Task 3 处理);其他 24 个 entity 类的 `LegacyId` 字段已删。

- [ ] **Step 5: Commit**

```bash
git add $(grep -rln ': EntityBase' src/ISEStudio/Infrastructure/Persistence/Entities/)
git commit -m "refactor(phase3): 24 entities inherit EntityBase + drop LegacyId field"
```

Capture commit SHA as `<A2_SHA>`。

---

## Task 3: Phase B.1 — `SystemConfigEntity.IsSingleton` + EntityConfigurations + audit log strings

**Files:**
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/WorkspaceEntities.cs`(`SystemConfigEntity` 类)
- Modify: `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`(24 处 `Property(x => x.LegacyId)` 整块删除 + SystemConfig 新增 `IsSingleton` 配置 + partial UNIQUE INDEX)
- Modify: `src/ISEStudio/Ontology/VocabularyProposalService.cs:243/300`(audit log strings)

**Interfaces:**
- Consumes: `<A2_SHA>`(24 entity 已无 LegacyId)
- Produces:
  - `SystemConfigEntity.IsSingleton` 字段 + `SingletonMarker` / `SingletonId` 常量替代 `SingletonLegacyId`
  - `EntityConfigurations.cs` 24 entity 删 `Property(x => x.LegacyId)`;systemconfig 加 `IsSingleton` 配置 + partial unique
  - audit log 字符串不再依赖 `proposal.LegacyId`

- [ ] **Step 1: 改 `SystemConfigEntity` 字段与常量**

在 `WorkspaceEntities.cs` 的 `SystemConfigEntity` 类:

```diff
-    public const long SingletonLegacyId = 1;
+    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");
+    public const bool SingletonMarker = true;

+    /// <summary>Phase 3: marks the singleton system config row. Enforced by
+    /// a partial UNIQUE INDEX on <c>IsSingleton = TRUE</c>.</summary>
+    public bool IsSingleton { get; set; }
```

(保留其他现有字段,包括 `DefaultExtractionConcurrency` / `LlmProviderId` / 等)

- [ ] **Step 2: 删除 `EntityConfigurations.cs` 24 处 `LegacyId` 配置**

逐个 entity 配置块删除:
```csharp
builder.Property(x => x.LegacyId)
    .HasColumnName("legacy_id")
    .IsRequired()
    .HasDefaultValue(0L);
```
(24 个 entity 都有这整块,删除即可)

```bash
grep -n 'LegacyId' src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs
```

预期: 24 处(每个 entity 1 行 `Property(x => x.LegacyId)` + 3 行 chained 调用 = 4 行 × 24 = 96 行)。逐处 Edit 删除。

- [ ] **Step 3: SystemConfig entity 加 `IsSingleton` 配置 + partial UNIQUE INDEX**

在 EntityConfigurations.cs 中 `SystemConfigEntity` 的 builder 块内(找到 `builder.Property(x => x.DefaultExtractionConcurrency)` 等 SystemConfig 专属配置位置):

```csharp
builder.Property(x => x.IsSingleton)
    .IsRequired()
    .HasDefaultValue(false);

builder.HasIndex(x => x.IsSingleton)
    .HasFilter("\"IsSingleton\" = TRUE")
    .IsUnique()
    .HasDatabaseName("ux_systemconfig_singleton");
```

- [ ] **Step 4: 改 `VocabularyProposalService.cs` audit log strings**

```bash
grep -n 'proposal.LegacyId' src/ISEStudio/Ontology/VocabularyProposalService.cs
```

预期: 2 处(line 243, 300)。Edit:
```diff
- $"Accepted terminology proposal {proposal.LegacyId} ({proposal.Action} \"{proposal.Term}\")"
+ $"Accepted terminology proposal {proposal.Id} ({proposal.Action} \"{proposal.Term}\")"

- $"Rejected terminology proposal {proposal.LegacyId} ({proposal.Action} \"{proposal.Term}\")"
+ $"Rejected terminology proposal {proposal.Id} ({proposal.Action} \"{proposal.Term}\")"
```

- [ ] **Step 5: 验证 build 成功**

```bash
cd src
dotnet build ISEStudio/ISEStudio.csproj -c Release --nologo 2>&1 | tail -20
cd ..
```

预期: 0 errors。`SettingsService.cs:114` 仍在用 `LegacyId = SingletonLegacyId`,但因为 EntityBase 没有 LegacyId,会出现 `error CS1061: 'EntityBase' does not contain a definition for 'LegacyId'`。这是 Task 4 修复的预期状态,不要在此 task 修复。

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Infrastructure/Persistence/Entities/WorkspaceEntities.cs \
        src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs \
        src/ISEStudio/Ontology/VocabularyProposalService.cs
git commit -m "feat(phase3): SystemConfig.IsSingleton + EF partial unique index"
```

Capture commit SHA as `<B1_SHA>`。

---

## Task 4: Phase C.1 — `ExportRunner.cs` path 改写 + `SettingsService.cs` singleton check

**Files:**
- Modify: `src/ISEStudio/Exports/ExportRunner.cs:97/126/154`(删除 `job.LegacyId`,artifact 路径改 `artifacts/{publicId}/...`)
- Modify: `src/ISEStudio/Settings/SettingsService.cs:114` + 其他 `s.LegacyId == SingletonLegacyId` 比较

**Interfaces:**
- Consumes: `<B1_SHA>`(`SystemConfigEntity.IsSingleton` 字段就位)
- Produces:
  - `ExportRunner.cs` 不再 read `job.LegacyId`;artifact 路径 `{publicId}/...`
  - `SettingsService.cs:114` 改 `IsSingleton = true, Id = SingletonId`;其他 `s.LegacyId == SingletonLegacyId` 改 `s.IsSingleton`

- [ ] **Step 1: grep `ExportRunner.cs` 的 `LegacyId` 用法**

```bash
grep -n 'LegacyId' src/ISEStudio/Exports/ExportRunner.cs
```

预期: ~3 处(line 97 `_artifacts.PrepareOutputDir(ks.PublicId, job.LegacyId)`;line 126 `ks.PublicId, job.LegacyId, layer, shardIndex: 0, nQuads)`;line 154 `ks.PublicId, job.LegacyId, manifest)`)。

- [ ] **Step 2: 改 `ExportRunner.cs` artifact 路径**

逐处 Edit:
```diff
- _artifacts.PrepareOutputDir(ks.PublicId, job.LegacyId);
+ _artifacts.PrepareOutputDir(ks.PublicId);
```

```diff
- ks.PublicId, job.LegacyId, layer, shardIndex: 0, nQuads
+ ks.PublicId, layer, shardIndex: 0, nQuads
```

```diff
- ks.PublicId, job.LegacyId, manifest
+ ks.PublicId, manifest
```

(若 `PrepareOutputDir` 是 `string publicId, long legacyId` 双参数 overload,需要保留兼容或改 single-arg overload。检查 `ExportArtifactStore.cs` 是否有 `(publicId)` 单参版本;若无,改 PrepareOutputDir 的方法签名接收 string publicId only,删除 long 参数。)

- [ ] **Step 3: grep `SettingsService.cs` 所有 `LegacyId`**

```bash
grep -n 'LegacyId' src/ISEStudio/Settings/SettingsService.cs
```

预期: ~3 处(`LegacyId = SystemConfigEntity.SingletonLegacyId` 在 object initializer + `s.LegacyId == SingletonLegacyId` 比较)。

- [ ] **Step 4: 改 `SettingsService.cs:114`**

```diff
- new SystemConfigEntity
- {
-     LegacyId = SystemConfigEntity.SingletonLegacyId,
-     ...
- }
+ new SystemConfigEntity
+ {
+     Id = SystemConfigEntity.SingletonId,
+     IsSingleton = true,
+     ...
+ }
```

其他 `s.LegacyId == SingletonLegacyId`:
```diff
- if (s.LegacyId == SystemConfigEntity.SingletonLegacyId) { ... }
+ if (s.IsSingleton) { ... }
```

- [ ] **Step 5: 验证 build 成功**

```bash
cd src
dotnet build ISEStudio/ISEStudio.sln -c Release --nologo 2>&1 | tail -30
cd ..
```

预期: 0 errors。如果还有 `LegacyId` 残留(grep `.LegacyId\b` 在 `src/ISEStudio/` 非 entity 文件),可能是 SettingsService / ExportRunner 之外;逐一 Edit 修复。

- [ ] **Step 6: 验证 `ExportRunner` 不再读 `job.LegacyId`**

```bash
grep -n 'job\.LegacyId\|\.LegacyId' src/ISEStudio/Exports/ExportRunner.cs
```

预期: 0 matches。

- [ ] **Step 7: Commit**

```bash
git add src/ISEStudio/Exports/ExportRunner.cs \
        src/ISEStudio/Settings/SettingsService.cs
git commit -m "refactor(phase3): ExportRunner PublicId path + SettingsService singleton check"
```

Capture commit SHA as `<C1_SHA>`。

---

## Task 5: Phase C.2 — 109 reads audit:ResolutionService / ConflictService / ConflictAgent / ReleaseService / ExportService 等

**Files:**
- Modify: per spec §4.2:
  - `src/ISEStudio/Ontology/ResolutionService.cs`(~5 reads)
  - `src/ISEStudio/Ontology/ConflictService.cs`(~4 reads)
  - `src/ISEStudio/Ontology/ConflictAgent.cs`(~3 reads if any)
  - `src/ISEStudio/Ontology/ReleaseService.cs`(~2 reads)
  - `src/ISEStudio/Exports/ExportService.cs`(~2 reads)
  - 其他 `grep -rn '\.LegacyId\b' src/ISEStudio/` 命中文件

**Interfaces:**
- Consumes: `<C1_SHA>`(ExportRunner + SettingsService done)
- Produces: `grep -rn '\.LegacyId\b' src/ISEStudio/` 返回 0 行(excl. `EntityBase` / `IHasId` 等已删文件)

- [ ] **Step 1: grep 完整列出所有剩余 `.LegacyId` 用法**

```bash
grep -rn '\.LegacyId\b' src/ISEStudio/ --include='*.cs' | grep -v 'src/ISEStudio/Infrastructure/Persistence/'
```

预期: ~25-30 处(跨 8-10 个文件)。逐文件列出 `file:line` 用于 Step 2。

- [ ] **Step 2: 逐处 audit + Edit**

每处 `.LegacyId` 改:
- `entity.LegacyId` → `entity.Id`(绝大多数,内部 Guid PK)
- `ks.LegacyId` / `row.LegacyId` → `ks.Id` / `row.Id`
- 审计/显示字符串中(proposal.LegacyId 已在 Task 3 改)→ `.Id` 或 `.PublicId` 视语义
- `.OrderBy(r => r.LegacyId)` → `.OrderBy(r => r.Id)`(Phase 3 不再用 legacy id 排序)
- `.Where(r => r.LegacyId == someVar)` → `.Where(r => r.Id == someVarGuid)`

每处 Edit:
```diff
- .OrderBy(r => r.LegacyId)
+ .OrderBy(r => r.Id)
```

- [ ] **Step 3: 验证 build 成功**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo 2>&1 | tail -30
cd ..
```

预期: 0 errors。

- [ ] **Step 4: 验证 `.LegacyId` 在 `src/ISEStudio/` 归零**

```bash
grep -rn '\.LegacyId\b' src/ISEStudio/ --include='*.cs'
```

预期: 0 matches。

- [ ] **Step 5: 跑测试 baseline check(确认 Phase 2 测试还绿)**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build 2>&1 | tail -20
cd ..
```

预期: ~851 tests passing(或少量失败因测试 seed 用 `LegacyId` —— 这些 test 在 Task 7 修复)。

- [ ] **Step 6: Commit**

```bash
git add $(grep -rln '\.LegacyId\b' src/ISEStudio/ --include='*.cs')
git commit -m "refactor(phase3): 109 LegacyId reads → Id/PublicId/IsSingleton"
```

Capture commit SHA as `<C2_SHA>`。

---

## Task 6: Phase D.1 — EF migration `DropLegacyIdColumn`

**Files:**
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/20260827HHMMSS_DropLegacyIdColumn.cs`(EF 自动生成)
- Modify: `src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs`(EF 自动更新)

**Interfaces:**
- Consumes: `<C2_SHA>`(所有 entity 无 LegacyId)
- Produces:
  - 6 个 Up() 操作:`AddColumn(systemconfig.is_singleton, bool, default: false)` + `CreateIndex(ux_systemconfig_singleton, partial unique)` + `Sql backfill` + `AlterColumn(default: false)` + `DropColumn(legacy_id × 24)`
  - Down() WARNING comment + `AddColumn<long>(legacy_id × 24, default: 0L)`(不重建 UNIQUE)

- [ ] **Step 1: 验证 build green before migration**

```bash
cd src
dotnet build ISEStudio/ISEStudio.csproj -c Release --nologo
cd ..
```

预期: 0 errors。

- [ ] **Step 2: 生成 EF migration**

```bash
cd src
dotnet ef migrations add DropLegacyIdColumn \
  --project ISEStudio/ISEStudio.csproj \
  --startup-project ISEStudio/ISEStudio.csproj \
  --context ISEStudioDbContext
cd ..
```

预期: 文件 `src/ISEStudio/Infrastructure/Persistence/Migrations/<timestamp>_DropLegacyIdColumn.cs` 创建;`ISEStudioDbContextModelSnapshot.cs` 同步。

- [ ] **Step 3: audit 生成的 migration Up()**

```bash
cat src/ISEStudio/Infrastructure/Persistence/Migrations/*_DropLegacyIdColumn.cs | head -80
```

预期内容(按顺序):
1. `migrationBuilder.AddColumn<bool>(name: "IsSingleton", table: "systemconfig", type: "boolean", nullable: false, defaultValue: false)`
2. `migrationBuilder.CreateIndex(name: "ux_systemconfig_singleton", table: "systemconfig", column: "IsSingleton", unique: true, filter: "\"IsSingleton\" = TRUE")`
3. `migrationBuilder.Sql("UPDATE systemconfig SET \"IsSingleton\" = TRUE WHERE id = (SELECT id FROM systemconfig LIMIT 1);")`
4. `migrationBuilder.AlterColumn<bool>(name: "IsSingleton", table: "systemconfig", ... defaultValue: false)`
5. `migrationBuilder.DropColumn(name: "legacy_id", table: <each of 24 tables>)` × 24

如果 EF 输出的顺序不同,手动调整;但操作不能增减。

- [ ] **Step 4: 添加 Down() WARNING**

打开 `<timestamp>_DropLegacyIdColumn.cs`,在 `Down()` 方法内第一行前添加:

```csharp
// WARNING: this Down() will fail to recreate the legacy_id column if any
// rows were inserted post-Phase-3. The column is dropped; recreating it as
// bigint NOT NULL with no DEFAULT will fail on existing data unless you
// manually backfill legacy_id first. See Phase 2 Down() WARNING for parallel
// pattern.
```

- [ ] **Step 5: 验证 24 个 DropColumn + 0 CreateTable/InsertData**

```bash
grep -c 'DropColumn(name: "legacy_id"' src/ISEStudio/Infrastructure/Persistence/Migrations/*_DropLegacyIdColumn.cs
# Expected: 24
grep -E 'CreateTable|InsertData' src/ISEStudio/Infrastructure/Persistence/Migrations/*_DropLegacyIdColumn.cs
# Expected: 0 matches
```

- [ ] **Step 6: 验证 build green after migration generation**

```bash
cd src
dotnet build ISEStudio/ISEStudio.csproj -c Release --nologo
cd ..
```

预期: 0 errors。

- [ ] **Step 7: 本地 SQLite dev DB 应用迁移**

```bash
cd src
dotnet ef database update \
  --project ISEStudio/ISEStudio.csproj \
  --startup-project ISEStudio/ISEStudio.csproj \
  --context ISEStudioDbContext
cd ..
```

预期: 迁移应用成功。

- [ ] **Step 8: 验证 SQLite schema**

```bash
sqlite3 src/ISEStudio/isestudio.db ".schema" | grep -i "legacy_id\|is_singleton"
```

预期:
- `legacy_id` 在所有表不存在
- `is_singleton` 在 systemconfig 表存在(类型 integer default 0)

- [ ] **Step 9: Commit**

```bash
git add src/ISEStudio/Infrastructure/Persistence/Migrations/*_DropLegacyIdColumn.cs \
        src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs
git commit -m "feat(phase3): EF migration DropLegacyIdColumn"
```

Capture commit SHA as `<D1_SHA>`。

---

## Task 7: Phase E.1 — Test cleanup + new tests

**Files:**
- Delete: `src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs`(Phase 2 写的,Phase 3 后不再需要)
- Create: `src/ISEStudio.Tests/Settings/SystemConfigSingletonTests.cs`(~2 Facts)
- Modify: `src/ISEStudio.IntegrationTests/Persistence/PostgresSchemaTests.cs`(加 2 个新断言)

**Interfaces:**
- Consumes: `<D1_SHA>`(migration applied)
- Produces:
  - `LegacyIdDefaultTests.cs` 删除(-4 tests)
  - `SystemConfigSingletonTests.cs` 新增(+2 tests)
  - `PostgresSchemaTests` +2 assertions

- [ ] **Step 1: 删除 `LegacyIdDefaultTests.cs`**

```bash
git rm src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs
```

- [ ] **Step 2: 创建 `SystemConfigSingletonTests.cs`**

文件 `src/ISEStudio.Tests/Settings/SystemConfigSingletonTests.cs`:

```csharp
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ISEStudio.Tests.Settings;

/// <summary>
/// Verifies Phase 3 SystemConfig singleton invariant: only one row may
/// carry <c>IsSingleton = true</c>; the DB rejects a duplicate via partial
/// UNIQUE INDEX <c>ux_systemconfig_singleton</c> (PG only — SQLite
/// ignores the filter, so these tests cover insertion success and use a
/// SkipOnSqlite for the duplicate constraint check).
/// </summary>
public sealed class SystemConfigSingletonTests
{
    [Fact]
    public async Task Create_with_IsSingleton_true_succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var sc = new SystemConfigEntity
        {
            Id = SystemConfigEntity.SingletonId,
            IsSingleton = true,
            DefaultExtractionConcurrency = 1,
        };
        db.SystemConfigs.Add(sc);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.SystemConfigs.SingleAsync();
        Assert.True(reloaded.IsSingleton);
        Assert.Equal(SystemConfigEntity.SingletonId, reloaded.Id);
    }

    [Fact(Skip = "PG-only: SQLite ignores HasFilter on HasIndex. Covered by PostgresSchemaTests.")]
    public async Task Duplicate_IsSingleton_true_fails_on_unique_index()
    {
        // Test would be:
        // 1. Insert first SystemConfig IsSingleton=true
        // 2. Insert second SystemConfig IsSingleton=true
        // 3. Expect DbUpdateException wrapping 23505
        // Skipped: SQLite allows duplicate. PG covers via integration test.
        await Task.CompletedTask;
    }
}
```

(调整 `TestDbContextFactory.Create()` 与现有 unit test 一致;调整 `db.SystemConfigs` set name 与 `EntityConfigurations` 注册名一致;若不同,grep 既有 SystemConfig tests 仿写。)

- [ ] **Step 3: 修改 `PostgresSchemaTests.cs`**

打开 `src/ISEStudio.IntegrationTests/Persistence/PostgresSchemaTests.cs`,在 `No_business_table_has_unique_legacy_id_index` 附近添加:

```csharp
[Fact]
public async Task No_business_table_has_legacy_id_column()
{
    await using var conn = new NpgsqlConnection(_connStr);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT table_name FROM information_schema.columns WHERE column_name = 'legacy_id' AND table_schema = 'public';", conn);
    await using var rdr = await cmd.ExecuteReaderAsync();
    var tables = new List<string>();
    while (await rdr.ReadAsync()) tables.Add(rdr.GetString(0));
    Assert.Empty(tables);
}

[Fact]
public async Task Systemconfig_has_unique_singleton()
{
    await using var conn = new NpgsqlConnection(_connStr);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_systemconfig_singleton';", conn);
    await using var rdr = await cmd.ExecuteReaderAsync();
    Assert.True(await rdr.ReadAsync(), "ux_systemconfig_singleton index should exist");
    var def = rdr.GetString(0);
    Assert.Contains("UNIQUE", def);
    Assert.Contains("is_singleton", def);
    Assert.Contains("WHERE", def, StringOrCase);
    Assert.Contains("TRUE", def, StringOrCase);
}
```

(若现有测试用 `StringComparison` 或其他 helpers,沿用风格。)

- [ ] **Step 4: 验证 build + 新 test passes**

```bash
cd src
dotnet build ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build \
  --filter "FullyQualifiedName~SystemConfigSingletonTests"
cd ..
```

预期: 1 passed(Skip 的 1 个 skipped),0 failed。

- [ ] **Step 5: 全套 unit tests**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build 2>&1 | tail -10
cd ..
```

预期: 849 passed(850 - 1 删除的 LegacyIdDefaultTests - 1 + 1 Skipped? 实际 848 passed + 1 skipped + 1 new passed = 850 - 4 + 2 = 848。Phase 3 后目标 ~848 unit。)

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs \
        src/ISEStudio.Tests/Settings/SystemConfigSingletonTests.cs \
        src/ISEStudio.IntegrationTests/Persistence/PostgresSchemaTests.cs
git commit -m "test(phase3): SystemConfigSingletonTests + PostgresSchema assertions"
```

Capture commit SHA as `<E1_SHA>`。

---

## Task 8: Phase F.1 — Runbook 更新

**Files:**
- Modify: `docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md`(§0 + §3.2 + §3.3 + §3.5)

**Interfaces:**
- Consumes: `<E1_SHA>`(tests done)
- Produces: Runbook 完全移除 legacy_id bootstrap 段,改写 admin seed SQL

- [ ] **Step 1: 读 Runbook §0 / §3.2 / §3.3 / §3.5**

```bash
grep -n 'legacy_id\|LegacyId\|allocator\|allocator' docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md
```

预期: ~5-7 处(§0 banner、§3.2 schema 描述、§3.3 schema 描述、§3.5 SQL INSERT 模板)。

- [ ] **Step 2: §0 banner 改写(逐字 per Phase 2 风格)**

```diff
- > **Phase 2 (2026-08-26)**: ...
+ > **Phase 3 (2026-08-27)**: legacy_id column retired entirely. New SystemConfig row marked by `is_singleton = true` (partial UNIQUE INDEX `ux_systemconfig_singleton`). Do NOT seed `legacy_id` column — column does not exist post-Phase-3.
```

- [ ] **Step 3: §3.2 schema 描述移除 legacy_id 行**

```diff
- `legacy_id      | bigint | not null | 0`  # Phase 2 default
+ `is_singleton   | boolean | not null | false`  # Phase 3 (one row true)
```

- [ ] **Step 4: §3.3 删 `LegacyIdAllocator` 引用**

```diff
- > post-Phase-2: new rows default to `legacy_id = 0` via DB DEFAULT 0
+ > post-Phase-3: legacy_id column dropped entirely. SystemConfig singleton identified by `is_singleton = true` (partial UNIQUE INDEX). All other entities use `Guid Id` as primary key.
```

- [ ] **Step 5: §3.5 SQL INSERT 模板移除 `legacy_id` 列**

```diff
- INSERT INTO users (id, "Username", "PasswordHash", "IsAdmin", "Active", "CreatedAt", legacy_id) VALUES (gen_random_uuid(), 'admin', 'x', true, true, NOW(), 1);
+ INSERT INTO users (id, "Username", "PasswordHash", "IsAdmin", "Active", "CreatedAt") VALUES (gen_random_uuid(), 'admin', 'x', true, true, NOW());
```

对 systemconfig 模板:
```diff
- INSERT INTO systemconfig (id, "DefaultExtractionConcurrency", legacy_id) VALUES (gen_random_uuid(), 1, 1);
+ INSERT INTO systemconfig (id, "DefaultExtractionConcurrency", "IsSingleton") VALUES ('00000000-0000-0000-0000-000000000001', 1, TRUE);
```

- [ ] **Step 6: 验证 Runbook 中 `legacy_id` 残留 = 0**

```bash
grep -n 'legacy_id\|LegacyId\|allocator' docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md
```

预期: 0 matches。

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md
git commit -m "docs(phase3): runbook removes legacy_id bootstrap"
```

Capture commit SHA as `<F1_SHA>`。

---

## Task 9: Phase G.1 — 全套件重跑 + docker smoke + final commit

**Files:**
- Touch: 无(verification only)

**Interfaces:**
- Consumes: `<F1_SHA>`(runbook done)
- Produces: 完整 verification 报告;无 destructive commits(纯 verify)

- [ ] **Step 1: 全套 build + tests**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo 2>&1 | tail -10
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build 2>&1 | tail -10
dotnet test ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj -c Release --nologo --no-build 2>&1 | tail -10
dotnet test ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj -c Release --nologo --no-build 2>&1 | tail -10
cd ..
```

预期: 0 errors;~848 unit + 167 contract + ~59 integration(57 + 2 new PostgresSchema) = ~1074 total green(目标数,可能因 EndpointRoleMatrixTests 已知 flake 浮动 ±1)。

- [ ] **Step 2: docker compose build isestudio(避免 Phase 2 stale image trap)**

```bash
docker compose build isestudio
```

预期: build 成功,**不要**先 build isestudio-migrate(stale image trap)。

- [ ] **Step 3: docker apply migration**

```bash
docker compose run --rm isestudio-migrate
```

预期: Exited (0);`__EFMigrationsHistory` 增加 `20260827HHMMSS_DropLegacyIdColumn` + `20260826111221_LegacyIdDefaultZero`(Phase 2 迁移已应用)。

- [ ] **Step 4: 验证 Postgres schema**

```bash
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c '\d users'
# Expected: no legacy_id column

docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c '\d systemconfig'
# Expected: is_singleton column + ux_systemconfig_singleton partial unique index
```

- [ ] **Step 5: smoke row INSERT(可选)**

```bash
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c \
  "INSERT INTO users (id, \"Username\", \"PasswordHash\", \"IsAdmin\", \"Active\", \"CreatedAt\") VALUES (gen_random_uuid(), 'phase3-smoke', 'x', false, true, NOW()) RETURNING id;"
# Expected: row inserted (no legacy_id column means no legacy_id in INSERT)
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c "DELETE FROM users WHERE \"Username\" = 'phase3-smoke';"
```

- [ ] **Step 6: docker compose up + health**

```bash
docker compose up -d isestudio
sleep 10
curl -s http://127.0.0.1:8080/api/health
docker compose logs isestudio --tail=20 | grep -iE 'error|fatal|exception'
```

预期: 200 OK;无 error/fatal/exception。

- [ ] **Step 7: 验证 Phase 3 hard constraints**

```bash
grep -rn '\.LegacyId\b' src/ISEStudio/ --include='*.cs'
# Expected: 0 matches

grep -rn 'LegacyIdAllocator\|AllocateAndPersist' src/ISEStudio/ --include='*.cs'
# Expected: 0 matches

git rev-parse pre-isestudio-rename
# Expected: fc06a73 (unchanged)

git rev-parse pre-python-retirement
# Expected: 8c6c884 (unchanged)

git log --oneline pre-isestudio-rename..HEAD | wc -l
# Expected: ~22 commits (Phase 2 13 + Phase 3 9 = 22)
```

- [ ] **Step 8: 写 memory file**

文件 `~/.claude/projects/e--GitHub-ontopilot/memory/ontopilot-phase3-complete.md`(新文件),模板参考 [[ontopilot-phase2-complete]]:

```markdown
---
name: ontopilot-phase3-complete
description: Guid PK Phase 3 完成 (2026-08-27) — legacy_id 列 + LegacyAddressableEntity 完全退役
metadata:
  type: project
---

# Guid PK Phase 3 完成 (2026-08-27)

**Why:** Phase 3 完全删除 `legacy_id` 列 + `LegacyAddressableEntity` 基类 + 109 个生产 read 访问点。SystemConfig 改 `IsSingleton bool` + partial UNIQUE INDEX。所有 entity 用 `Guid Id`(或 `PublicId` for KS)作为唯一标识。

**How to apply:** 任何后续 slice 提到 "legacy id" / "LegacyId" 视为已退役——只在 memory reference + spec doc 中提及,不在 production code 出现。SystemConfig singleton check 用 `s.IsSingleton`,非 `s.Id == SingletonId`。ExportRunner artifact 路径 `{publicId}/...`,不依赖 `job.LegacyId`。EF 改 column 必须先 grep `\.LegacyId\b` 全 src 验证(Phase 2 C1 教训)。

## Commits

[9 task commits,真实 SHA 替换占位符]

## 关联

- 上游:[[ontopilot-phase2-complete]]
- 设计:[spec](../specs/2026-08-27-legacy-id-column-retirement-design.md) + [plan](../plans/2026-08-27-legacy-id-column-retirement.md)

## 后续

- GitHub repo rename(已 unlocked by Phase 2)
- IriSqlMigrator 退役(legacy_id 不在其范围;Phase 3 不动)
- 任何新增 entity:必须 `:` 或 `:` `EntityBase`,不引入 LegacyId
```

然后追加到 `~/.claude/projects/e--GitHub-ontopilot/memory/MEMORY.md`:

```markdown
- [ontopilot-phase3-complete](ontopilot-phase3-complete.md) — Guid PK Phase 3 完成(2026-08-27):legacy_id 列 + LegacyAddressableEntity + 109 reads 完全退役;SystemConfig 改 IsSingleton;ExportRunner 改 PublicId 命名
```

- [ ] **Step 9: Final commit(memory file 不在 repo,不需要 commit)**

```bash
git status
# Expected: working tree clean (Phase 3 9 commits in place + pre-existing unrelated .claude/settings.json)
git log --oneline pre-isestudio-rename..HEAD
# Expected: ~22 commits total
```

- [ ] **Step 10: 写完成报告**

Print:
```
Phase 3 complete:
- <A0_SHA>: IHasId + EntityBase + LegacyAddressableEntity delete
- <A1_SHA>: 24 entities → EntityBase
- <B1_SHA>: SystemConfig.IsSingleton + partial UNIQUE
- <C1_SHA>: ExportRunner PublicId path + SettingsService singleton
- <C2_SHA>: 109 LegacyId reads → Id/PublicId/IsSingleton
- <D1_SHA>: EF migration DropLegacyIdColumn
- <E1_SHA>: SystemConfigSingletonTests + PostgresSchema
- <F1_SHA>: runbook removes legacy_id

Tests: unit ~848 + contract 167 + integration ~59 = ~1074
Smoke: PASS (legacy_id dropped, is_singleton added, health 200)
Spec: docs/superpowers/specs/2026-08-27-legacy-id-column-retirement-design.md
Plan: docs/superpowers/plans/2026-08-27-legacy-id-column-retirement.md (this file)
```

End of plan.

---

## Self-Review (controller inlined, post-write)

1. **Spec coverage:**
   - §2.1 Entity 层(Task 1-2)✓
   - §2.1 EF 层(Task 3)✓
   - §2.1 Service 层(Task 4-5)✓
   - §2.1 EF migration(Task 6)✓
   - §2.1 测试(Task 7)✓
   - §2.1 Runbook(Task 8)✓
   - §2.1 Smoke(Task 9)✓
   - §2.2 DO-NOT-TOUCH(Task 9 Step 7 验证)✓
2. **Placeholder scan:** 无 TBD/TODO/fill-in/details;24 entity table 列表明确让 implementer 在 Step 1 grep 自取。
3. **Type consistency:** `EntityBase` / `IHasId` / `IsSingleton` / `SingletonId` 命名跨 task 一致;`EntityConfigurations` 配置语法统一;EF migration operation 顺序固定。
4. **Risk:** Task 1 故意留下 compile-fail(24 entity 仍 `:` `LegacyAddressableEntity`),Task 2 修复;Task 3 留下 SettingsService.cs 编译失败,Task 4 修复——这是 plan 阶段 wired 好的 cascade,确保 implementer 按顺序执行而非并行。
5. **Test baseline:** Phase 3 后 ~848 unit(850 - 4 LegacyIdDefaultTests + 2 SystemConfigSingletonTests - 1 Skip 但仍 +1 unit;不计 Skip);167 contract unchanged;~59 integration(57 + 2 PostgresSchema)。