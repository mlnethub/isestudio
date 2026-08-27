# Guid 主键 Phase 3:`legacy_id` 列完全退役(2026-08-27)

## 1. 背景

Phase 1(`docs/superpowers/specs/2026-08-20-guid-primary-key-design.md`)完成 wire 主键切换。Phase 2(`docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md`)退役 `LegacyIdAllocator` 写入路径,把 `legacy_id` 列降级为只读 + default 0 + 删除 24 个 UNIQUE 索引。

Phase 3 解决 Phase 2 留下的尾巴:`legacy_id` 列本身仍然存在,24 表 `bigint NOT NULL DEFAULT 0` 占用 schema 噪音;`LegacyAddressableEntity` 基类持续存在(占 24 entity 继承链);`SettingsService.cs:114` 仍在写 `LegacyId = SingletonLegacyId`;`ExportRunner.cs` 仍用 `job.LegacyId` 命名 artifact 文件;**109 个 production `.LegacyId` 读访问点**现在大部分读到 0,语义已退化。

Phase 3 目标:完全删除 `legacy_id` 列 + 基类 + 全部相关读写,只保留 Guid Id / PublicId / IsSingleton 三种标识语义。

## 2. 范围

### 2.1 IN(本次触及)

**Entity 层:**
- `src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs`:**整文件删除**(~50 行)
- 新增 `src/ISEStudio/Infrastructure/Persistence/Entities/EntityBase.cs`: `public abstract class EntityBase : IHasId { public Guid Id { get; set; } = Guid.NewGuid(); }`
- 新增 `src/ISEStudio/Infrastructure/Persistence/Entities/IHasId.cs`: `public interface IHasId { Guid Id { get; set; } }`
- `WorkspaceEntities.cs` + 其他 4 entity 文件:24 entity 类声明 `:` 或 `:` `EntityBase`(替代 `:` `LegacyAddressableEntity`);删除 `public long LegacyId { get; set; }` 字段;`SystemConfigEntity` 加 `public bool IsSingleton { get; set; }`
- `SystemConfigEntity`: 删 `SingletonLegacyId` 常量;新增 `public static readonly bool SingletonFlag = true`(作为 `IsSingleton` 默认值约定);构造 `SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001")` 仅作为审计引用,**不**作为 DB UNIQUE 字段

**EF / Schema 层:**
- `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`:24 处 `Property(x => x.LegacyId)` 整块删除;SystemConfigEntity 新加 `Property(x => x.IsSingleton).IsRequired().HasDefaultValue(false)` + `HasIndex(x => x.IsSingleton).HasFilter("\"IsSingleton\" = TRUE").IsUnique().HasDatabaseName("ux_systemconfig_singleton")`(PG partial unique index)
- `ISEStudioDbContextModelSnapshot.cs`:24 entity 同步删除 `LegacyId` 字段

**Service 层:**
- `src/ISEStudio/Settings/SettingsService.cs:114`: `LegacyId = SystemConfigEntity.SingletonLegacyId` → `IsSingleton = true, Id = SystemConfigEntity.SingletonId`
- `src/ISEStudio/Settings/SettingsService.cs` 其他: `s.LegacyId == SingletonLegacyId` 比较 → `s.IsSingleton`
- `src/ISEStudio/Exports/ExportRunner.cs:97/126/154`: 删除 `job.LegacyId` 用法;artifact 路径改 `{publicId}/...`(D3 决策)
- **109 个 `.LegacyId` 读访问点**审计:每个改 `.Id` 或 `.PublicId`(详见 §4.2)
- `src/ISEStudio/Ontology/VocabularyProposalService.cs:243/300`: audit log 字符串 `proposal.LegacyId` → `proposal.Id`(避免显示 0)
- `src/ISEStudio/Audit/AuditLogService.cs`: Phase 2 已注入 `ISEStudioDbContext`,Phase 3 不动

**EF 迁移层:**
- 新增 `20260827HHMMSS_DropLegacyIdColumn.cs`:6 个 Up() 操作(详见 §6)
- `Down()`: WARNING comment + backup 列 / 索引 recreate(reference Phase 2 Down() WARNING 模式)

**测试层:**
- 删 `src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs`(Phase 2 写的,Phase 3 后不再需要)
- 改 `src/ISEStudio.IntegrationTests/Persistence/PostgresSchemaTests.cs`: 新断言 `No_business_table_has_legacy_id_column`(每 entity 表 `\d` 输出不含 `legacy_id`);`systemconfig_has_unique_singleton`(ux_systemconfig_singleton 索引存在 + filter)
- 新增 `src/ISEStudio.Tests/Knowledge/SystemConfigSingletonTests.cs`: `Create_with_IsSingleton_true_succeeds` / `Create_with_IsSingleton_true_twice_fails_on_unique_index`(覆盖 singleton invariant)
- 保留 Phase 2 + 修复轮的 `CreateAsync_twice_yields_distinct_graph_and_base_iris` 测试

**Runbook:**
- `docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md`:
  - §3.5 SQL INSERT 模板:删除 `legacy_id` 列相关部分(Phase 3 后列已 DROP,不可 INSERT 该列)
  - §3.2 / §3.3 schema 描述:删除 `legacy_id` 列描述 + `LegacyIdAllocator` 全部引用
  - §0 加标注:"Phase 3 后此 runbook 用于 legacy bootstrap 时不再写 `legacy_id` 列;new rows 由 `is_singleton` 列标识 SystemConfig singleton"

### 2.2 DO-NOT-TOUCH(本次不动)

- **IriSqlMigrator.ColumnsToRewrite**:`legacy_id` 本来就不在(`long` 不是 `uniqueidentifier`);Phase 3 后彻底不需要(列已 DROP)
- **IriSqlVerifier.baseline**: column 类型不变 / 不新增
- **历史 EF migration**(`20260816140916_InitialCompatibility.cs`、`20260826111221_LegacyIdDefaultZero.cs`):保留不动
- **`pre-isestudio-rename` tag** → `fc06a73`:保留
- **`pre-python-retirement` tag** → `8c6c884`:保留
- Python baseline / 历史 spec / 已 retired 文档:全部 DO-NOT-TOUCH
- 前端 wire shape:无变化(已 Phase 1 + Phase 2 wire shape fix)
- API endpoint shape:无变化(column 删除对 client 透明)

## 3. 设计决策(2026-08-27 brainstorming)

| 决策 ID | 主题 | 决策 | 理由 |
|---|---|---|---|
| D3 | ExportRunner.cs artifact 命名 | `artifacts/{publicId}/...`,删除 `job.LegacyId` 用法 | Guid 命名最干净;旧 release artifact 文件夹保留只读(老 release 仍可读);新 release 用新命名 |
| D5 | 基类退役 | 删 `LegacyAddressableEntity`,改为 `EntityBase`(Guid Id)+ `IHasId` 接口 | 24 entity 全部脱离继承;代码 metadata 不残留 legacy 措辞 |
| D6 | SystemConfig singleton 表达 | `IsSingleton bool` + partial UNIQUE INDEX(过滤 `IsSingleton = TRUE`) | Explicit,不需要 magic Guid;EF migration 加列 + 索引;第二次 insert fail on UNIQUE 覆盖 invariant |
| D7 | 历史 legacy_id 数据保留 | 直接 DropColumn,无 history table | Phase 2 后所有新行 = 0;少量历史非零数据丢失;cutover 验证只走 `isestudio-migrate` 容器 smoke |

## 4. 详细设计

### 4.1 基类与接口

```csharp
// src/ISEStudio/Infrastructure/Persistence/Entities/IHasId.cs (NEW)
public interface IHasId
{
    Guid Id { get; set; }
}

// src/ISEStudio/Infrastructure/Persistence/Entities/EntityBase.cs (NEW)
public abstract class EntityBase : IHasId
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

// LegacyAddressableEntity.cs: DELETE
// 所有 entity: `class X : LegacyAddressableEntity` → `class X : EntityBase`
// 删除 public long LegacyId 字段
```

### 4.2 109 个读访问点审计

按 Phase 2 决策,所有 `.LegacyId` 读点必须改用:
- `.Id`(内部 Guid PK,EF 默认 Id)
- `.PublicId`(外部稳定标识,KnowledgeSystemEntity 已有)

**审计目录**(compile-time grep `\.LegacyId\b`):
- `Settings/SettingsService.cs`: ~3 reads → 改 `.IsSingleton` / `.Id`
- `Knowledge/KnowledgeService.cs`:Phase 2 修复轮已修 C1 → 无遗留
- `Exports/ExportRunner.cs`: ~3 reads → 删除(LegacyId 不再用于 artifact path)
- `Exports/ExportService.cs`: ~2 reads → 改 `.Id`(job 内 GUID)
- `Ontology/VocabularyProposalService.cs`: 2 audit log strings → 改 `.Id`
- `Ontology/ConflictService.cs`: 4 reads → 改 `.Id`(CONFLICT_ASSERTIONS.LegacyId 是断言自身 Id)
- `Ontology/ConflictAgent.cs`: ~3 reads(KS graph Iri 由 Phase 2 修复轮改 PublicId,KS 自身不存在 LegacyId reads)
- `Ontology/StructureAgent.cs`: 0 reads(Phase 1 已用 KS.GraphIri)
- `Ontology/ReleaseService.cs`: ~2 reads → 改 `.Id`
- `Ontology/ResolutionService.cs`: 5 reads(`.OrderBy(LegacyId)`、`.Where(LegacyId == ...)`)→ 改 `.Id`
- `Extraction/TBoxExtractionService.cs`: 0 reads(LegacyId 仅在 prompt text 出现,无 ORM 操作)
- `Extraction/ABoxExtractionService.cs`: 0 reads
- `ConflictTests` + `OntologyTests` + `PersistenceTests`: ~6 test-side reads → 改 `.Id`(test seed 仍用 Guid)

**Forbidden post-Phase-3 pattern**: `grep -rn '\.LegacyId\b' src/` 应返回 0 行(除 `LegacyAddressableEntity` 类内残余,Phase 3 删除后归零)。

### 4.3 SystemConfig 字段变更

```csharp
// WorkspaceEntities.cs
public sealed class SystemConfigEntity : EntityBase
{
    public const long SingletonLegacyId = 1;  // DELETE
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");  // NEW
    public static readonly bool SingletonMarker = true;  // NEW (审计引用)

    public bool IsSingleton { get; set; }  // NEW
    // 其他现有字段不变
}
```

### 4.4 EF / EntityConfigurations

```csharp
// 24 entity 配置: 删除整块 Property(x => x.LegacyId).HasColumnName("legacy_id").IsRequired().HasDefaultValue(0L);
// SystemConfigEntity 配置新增:
builder.Property(x => x.IsSingleton).IsRequired().HasDefaultValue(false);
builder.HasIndex(x => x.IsSingleton)
    .HasFilter("\"IsSingleton\" = TRUE")
    .IsUnique()
    .HasDatabaseName("ux_systemconfig_singleton");
```

## 5. 测试计划

**Unit tests**(src/ISEStudio.Tests):
- `SystemConfigSingletonTests.cs` (NEW, ~2 Facts):
  - `Create_with_IsSingleton_true_succeeds`
  - `Create_with_IsSingleton_true_twice_fails_on_unique_index`(SQLite 实现行为不同,跳过或 `[Skip]` 注 PG-only)
- `KnowledgeServiceTests.cs`:`CreateAsync_twice_yields_distinct_graph_and_base_iris`(Phase 2 修复轮已加)保留
- 删 `LegacyIdDefaultTests.cs`(~120 行)
- `LegacyAddressableEntity.cs` 删除后,**任何 `using` / `IHasLegacyId` 类型引用 grep = 0**

**Integration tests**(src/ISEStudio.IntegrationTests):
- `PostgresSchemaTests.cs`:
  - 改 `No_business_table_has_legacy_id_column`(NEW):每个 entity 表 `\d` 输出不含 `legacy_id`
  - 改 `systemconfig_has_unique_singleton`(NEW):ux_systemconfig_singleton 索引存在 + filter `IsSingleton = TRUE`
  - 删 `No_business_table_has_unique_legacy_id_index`(Phase 2 写)

**Contract tests**:
- 167 contract tests 行为不变(LegacyId 不出现在 wire shape)

**Total**: 851 unit + 167 contract + 57 integration = 1075 + ~2 systemconfig = ~1077

## 6. EF 迁移

**单 EF migration `20260827HHMMSS_DropLegacyIdColumn.cs`**,Up() 包括:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    // 1. AddColumn is_singleton on systemconfig
    migrationBuilder.AddColumn<bool>(
        name: "is_singleton",
        table: "systemconfig",
        type: "boolean",
        nullable: false,
        defaultValue: false);

    // 2. CreateIndex partial unique on systemconfig(is_singleton)
    migrationBuilder.CreateIndex(
        name: "ux_systemconfig_singleton",
        table: "systemconfig",
        column: "is_singleton",
        unique: true,
        filter: "\"IsSingleton\" = TRUE");

    // 3. Backfill: if seed row exists, mark it as singleton
    migrationBuilder.Sql("UPDATE systemconfig SET \"IsSingleton\" = TRUE WHERE id = (SELECT id FROM systemconfig LIMIT 1);");

    // 4. DropColumn legacy_id on 24 tables
    foreach (var table in LegacyTables.All) // 24 table names
    {
        migrationBuilder.DropColumn(name: "legacy_id", table: table);
    }
}
```

`Down()`: WARNING comment(参考 Phase 2 Down() WARNING 模式)
```
// WARNING: this Down() will fail to recreate the legacy_id column if any
// rows were inserted post-Phase-3. The column is dropped; recreating it as
// bigint NOT NULL with no DEFAULT will fail on existing data unless you
// manually backfill legacy_id first.
```
+ 24 × `AddColumn<long>(name: "legacy_id", table: ..., type: "bigint", nullable: false, defaultValue: 0L)`(不重建 UNIQUE 索引)

## 7. Risk + Rollback

**Risk surface:**
- 109 reads + 1 write + 24 entity + 24 config + 1 EF migration + 1 export path rewrite
- DropColumn 24 表 destructive(legacy_id 数据丢失)
- Postgres partial unique index syntax(`HasFilter`)SQLite 不兼容 → Phase 3 部分 test 改 `[SkipOnSqlite]` 或仅 PG 跑

**Rollback:**
- `git revert` Phase 3 commits + EF 迁移 Down()(recovers is_singleton column;DropColumn Undo by manual ADD COLUMN;legacy_id 数据不可恢复)
- `pre-isestudio-rename` tag 不动

**Smoke gate:**
- `docker compose build isestudio && docker compose run --rm isestudio-migrate` → Exited (0)
- `docker exec pg psql -c '\d users'` → 无 legacy_id 列,systemconfig 有 is_singleton 列
- `docker exec pg psql -c '\d systemconfig'` → 包含 `ux_systemconfig_singleton` partial unique
- `curl http://127.0.0.1:8080/api/health` → 200

## 8. Decision Log

| Decision | Title | Date | Outcome | Notes |
|---|---|---|---|---|
| D1(c) | Phase 2 新 row legacy_id=0 via DB DEFAULT | 2026-08-26 | EXECUTED | 见 Phase 2 spec |
| D2 | Phase 2 保留 LegacyAddressableEntity 类名 | 2026-08-26 | ABANDONED | Phase 3 D5 取代 |
| D3 | Phase 3 ExportRunner 改 PublicId 命名 | 2026-08-27 | PROPOSED | brainstorming 决策 |
| D5' | Phase 2 删 UNIQUE 索引 | 2026-08-26 | EXECUTED | 见 Phase 2 spec |
| D5 | Phase 3 删基类 + IHasId + EntityBase | 2026-08-27 | PROPOSED | brainstorming 决策 |
| D6 | Phase 3 SystemConfig IsSingleton bool | 2026-08-27 | PROPOSED | brainstorming 决策 |
| D7 | Phase 3 直接 DropColumn 无 history | 2026-08-27 | PROPOSED | brainstorming 决策 |
| Ruling 1 | SettingsService.cs:114 public setter 保留 | 2026-08-26 | SUPERSEDED | Phase 3 改 IsSingleton |

### 8.1 Lessons(from Phase 2 + 修复轮)

- **C1 教训:** Phase 2 109 reads 审计遗漏了 1 个 IRI 派生写入点(KnowledgeService.cs:189-190)。Phase 3 必须 **逐文件 grep `\.LegacyId\b`** 而非仅 grep `LegacyIdAllocator` 残留
- **compose build trap:** Phase 2 smoke 发现 `docker compose build isestudio-migrate` 静默 no-op;Phase 3 必须先 `docker compose build isestudio` 再 `docker compose run --rm isestudio-migrate`
- **约束保留:** Phase 2 Ruling 1(public setter)Phase 3 SUPERSEDED,但 Phase 2 在执行期间是 binding

## 9. Out of scope(本 spec 不解决)

- IriSqlMigrator / IriSqlVerifier 路径(Phase 3 不动 IRI 列)
- 前端 wire shape(已稳定)
- API endpoint 改动
- SettingsService 其他字段(只动 LegacyId / IsSingleton)
- ConflictAgent.TryAutoApplyAsync resolved status 持久化(Phase 2 修复轮 carry-forward)
- ISEStudio repo rename GitHub-side(Phase 2 解锁 follow-up,非本 spec)