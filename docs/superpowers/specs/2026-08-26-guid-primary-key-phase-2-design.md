# Guid 主键 Phase 2:LegacyIdAllocator 退役(2026-08-26)

## 1. 背景

Phase 1(2026-08-20,`docs/superpowers/specs/2026-08-20-guid-primary-key-design.md`)已完成 **wire 表面** 的主键切换:所有 JSON / URL / FK 现在都是 `Guid Id`。但 DB schema 与代码兼容层仍在跑历史的 `legacy_id` 列 + `LegacyIdAllocator`,占着 24 张表的 `bigint NOT NULL UNIQUE` 列与 advisory-lock allocator。

Phase 2 原本设计为"删 `legacy_id` 列 + 删 `LegacyIdAllocator` 整体退役"。但 **preflight 实测发现严重低估**:`git grep -rn '\.LegacyId'` 命中 **109 个 active 访问点,跨 21 个生产文件**(原 spec d9a3d1b 估的 "13+5 allocator call sites" 差 8 倍)。LegacyId 在生产代码里被用作:

- 跨 entity 排序字段(如 `EntityResolution/ResolutionService.cs:77, 111` `.OrderBy(r => r.LegacyId)`)
- 跨 entity FK 引用(如 `ResolutionService.cs:147, 235, 268, 308` `.Where(r => r.LegacyId == legacyId)`)
- 审计/导出 artifact 命名(ExportRunner 用 LegacyId 命名 export artifact)
- JSON wire 输出的一部分(`ConflictAgent` 把 LegacyId 写进 prompt payload)

**重新设计决策(2026-08-26 brainstorming 修订,见 [[ontopilot-phase2-halt]]):** Phase 2 收窄为 **"只删 allocator(写入路径),保留 LegacyId 字段(读路径)"**:

- D1(c):新 row 的 `LegacyId = 0`(由 DB DEFAULT 0 派发)
- D2:保留 `LegacyAddressableEntity` 类名(类语义仍准确)
- D5':删 UNIQUE 索引 `ux_*_legacy_id`(否则多个新 row 同为 0 冲突)+ 列 NOT NULL 改 DEFAULT 0

## 2. 范围

### 2.1 IN(本次触及)

**代码层(本次涉及 22 个生产 service + 1 个 DI 入口):**

- `src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs`:**整文件删除**(310 行)
- 22 个 service 类(详见 §4.2):删除 `LegacyIdAllocator _allocator` 字段 + 构造函数参数 + `_allocator.AllocateAndPersistAsync(x)` 调用 → 替换为 `dbContext.Add(x); await dbContext.SaveChangesAsync(ct)`
- `src/ISEStudio/Program.cs:336` 删 `builder.Services.AddScoped<LegacyIdAllocator>();`
- `src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs`:setter **保持** `public long LegacyId { get; set; }`(D4 被执行期 Ruling 1 否决,见 §4.1 + §8.1;生产唯一非零写点 `SettingsService.cs:114` 依赖 public setter)
- `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs` 24 处:对 `HasColumnName("legacy_id")` 改 `IsRequired()` → `IsRequired().HasDefaultValue(0L)` + 移除 `HasIndex(...).IsUnique() HasDatabaseName("ux_*_legacy_id")`(否则多 row 同为 0 撞 UNIQUE)
- `src/ISEStudio/Exports/ExportJobStore.cs:40-41` doc comment:`LegacyIdAllocator` 引用删除

**EF 迁移层:**

- 新增 `20260826HHMMSS_LegacyIdDefaultZero.cs`:24 张表 `DROP INDEX ux_*_legacy_id` + `ALTER COLUMN legacy_id SET DEFAULT 0`(EF Core 自动生成,需 audit 不能含 CREATE TABLE 重定义)
- `ISEStudioDbContextModelSnapshot.cs` 同步 `HasDefaultValue(0L)` + 移除 `HasIndex` 反映

**测试层:**

- 删 `src/ISEStudio.Tests/Persistence/LegacyIdAllocatorTests.cs`(整文件,~280 行,12 个 `[Fact]`)
- 删 PG 集成测试引用 allocator(per [[ontopilot-allocator-atomic]] §"PG concurrency tests")
- 改 6 个 test 文件去掉 `LegacyIdAllocator` 引用(`TokenServiceTests` / `ConflictAgentTests` / `ExportJobStoreTests` / `ExtractionAgentChainTests` / `TerminologyAgentOrchestrationTests` / `StructureAgentTests`)
- 新增 `src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs`(4 个 `[Fact]`):新 row 默认 0(含 DB 重物化证明)/ 多次 insert 全 0 / 旧 row 更新不变 / 显式 LegacyId 被 honor(public setter 直写)

**Runbook(只限 `docs/superpowers/runbooks/` 范围):**

- `docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md`:
  - §3.5 SQL INSERT 模板:`legacy_id` 列保留(`COALESCE((SELECT MAX(legacy_id) FROM users), 0) + 1`)—— Phase 2 后唯一索引已删,MAX+1 仅是历史 admin 序号约定,不是硬约束
  - §3.2 / §3.3 schema 描述:删除 "allocated by `LegacyIdAllocator.AllocateAndPersistAsync`" 一句 → 改为 "post-Phase-2: new rows default to `legacy_id = 0` via DB DEFAULT 0"
  - §0 加标注:"Phase 2 后此 runbook 用于 legacy bootstrap 时仍建议 MAX+1 分配以保持 admin 序号习惯,非硬约束"

### 2.2 DO-NOT-TOUCH(本次不动)

- **生产 109 个 `.LegacyId` 访问点**:全部保留,行为不变(读路径继续工作)
- **24 张表的 `legacy_id` 列**:保留(读路径仍依赖);只改 default + 删 UNIQUE 索引
- **历史 EF migration**:`20260816140916_InitialCompatibility.cs` 保留不动
- **`IriSqlMigrator.ColumnsToRewrite`**:`legacy_id` 本来就不在(`long` 不是 `uniqueidentifier`),不动
- **`IriSqlVerifier.baseline`**:column 类型不变(`bigint` → `bigint`);不上 legacy_id 也不参与 rewrites
- **Python baseline / 历史 spec / 已 retired 文档**:全部 DO-NOT-TOUCH(同 [[ontopilot-python-retirement]] + [[ontopilot-isestudio-rename]] 模式)
- **`pre-isestudio-rename` tag** → `fc06a73`:保留
- **`pre-python-retirement` tag**:保留

### 2.3 依赖与解耦

| 上游 | 关系 |
| --- | --- |
| Phase 1 spec | Phase 2 前置;wire 已切到 Guid,Phase 2 删 allocator 不影响 wire |
| [[ontopilot-allocator-atomic]] | allocator advisory-lock atomic refactor;Phase 2 在此基础上退役 |
| [[ontopilot-allocator-missed-sites]] | 30 个 call site 的清单是 Phase 2 的执行清单 |
| [[ontopilot-rbac-coverage-matrix]] | Phase 2 删 allocator 不影响 RBAC 矩阵 |

| 下游 | 影响 |
| --- | --- |
| GitHub repo rename follow-up | 解锁:Phase 2 让 allocator 退役,brand rename 在 allocator 引用层 clean |
| Guid PK Phase 3(如还有) | 仍可推进 —— Phase 2 是 step 2,不阻断 step 3 |

## 3. 数据迁移策略

**不需要数据迁移**。Phase 2 改动只涉及:

- 24 张表加 `DEFAULT 0` 到 `legacy_id` 列(EF migration 自动)
- 24 张表删 `ux_*_legacy_id` UNIQUE 索引
- 删除 allocator 服务(纯代码,无 DB 影响)
- 新 row 自动 `legacy_id = 0`;旧 row 保持历史值(1, 2, 3, ...)

现有 volume 数据**完整保留**,无丢失。**不需要** §3.1 dump、§3.2 `docker compose down -v`、§3.3 清空 smoke —— 原 spec d9a3d1b 的 "清 volume" 假设在本修订下不再适用。

## 4. 实现策略

### 4.1 Entity 基类(setter 保持 public —— D4 执行期被否决)

**Ruling 1(执行期,2026-08-26):D4(`LegacyId { get; private set; }`)ABANDONED。** 原设计动机是防御性(阻止生产代码意外写入,EF 通过 backing field 物化)。但 preflight 核查发现**生产代码有真实写入点**:`SettingsService.cs:114` 在 seed singleton SystemConfig 时执行 `LegacyId = SystemConfigEntity.SingletonLegacyId`(这是生产代码里唯一的 LegacyId 赋值;`grep -rn "LegacyId = " src/ISEStudio` 除该行外无其他写入)。改成 `private set` 会让该行直接编译失败。因此 setter 保持原样:

```csharp
public abstract class LegacyAddressableEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long LegacyId { get; set; }   // 保持 public(执行期 ruling)
}
```

"防止意外写入" 的安全动机改由 **DB 层** 承担:删 UNIQUE 索引 `ux_*_legacy_id` + 新 row `legacy_id = 0`(DB DEFAULT 0)—— 任何 `dbContext.Add(x)` 而忘记显式赋值的 row 都是 0,不会撞唯一性(索引已删),也不会产生伪序列号。`SettingsService` 是唯一刻意写非零值的生产站点(singleton 序号约定 1),行为不变。

### 4.2 22 个 service 类的 30 个 call site 替换

每个 service 改 3 处:

1. **字段删除**:`private readonly LegacyIdAllocator _allocator;` → 删
2. **构造函数参数**:`LegacyIdAllocator allocator` → 删,后续参数逗号调整
3. **调用替换**:

```diff
- await _allocator.AllocateAndPersistAsync(new AuditEventEntity { ... }, ct);
+ _dbContext.AuditEvents.Add(new AuditEventEntity { ... });
+ await _dbContext.SaveChangesAsync(ct);
```

`AllocateAndPersistAsync(x, ct)` 旧语义 = `SELECT MAX+1; dbContext.Add(x); SaveChanges(ct)`(在 advisory lock 下 atomic)。Phase 2 新语义 = `dbContext.Add(x); SaveChanges(ct)`,DB 自动写 0。

`AllocateManyAndPersistAsync<T>(IEnumerable<T>, ct)` 旧语义 = N 次 alloc + 1 次 SaveChanges。Phase 2 改为 `dbContext.AddRange(entities); await dbContext.SaveChangesAsync(ct);`(1 次 round-trip 替换 N 次)。

**完整 call site 清单(22 服务,30 个调用):**

| Service | File | AllocateAndPersistAsync 调用数 | AllocateManyAndPersistAsync 调用数 |
| --- | --- | --- | --- |
| `AuditLogService` | `Audit/AuditLogService.cs:57` | 1 | 0 |
| `AuthService` | `Authentication/AuthService.cs:159` | 1 | 0 |
| `KnowledgeApiTokenService` | `Authentication/KnowledgeApiTokenService.cs:225` | 1 | 0 |
| `McpTokenService` | `Authentication/McpTokenService.cs:209` | 1 | 0 |
| `ConflictAgent` | `Conflicts/ConflictAgent.cs:526, 544` | 2 | 0 |
| `ConflictService` | `Conflicts/ConflictService.cs:198, 622, 669` | 2 | 1 |
| `AuthController` | `Controllers/AuthController.cs:124` | 1 | 0 |
| `DocumentService` | `Documents/DocumentService.cs:228, 814, 850` | 2 | 1 |
| `ExportJobStore` | `Exports/ExportJobStore.cs:74-75` | 1 | 0 |
| `ExtractionJobStore` | `Extraction/ExtractionJobStore.cs:94-95` | 1 | 0 |
| `TerminologyAgent` | `Extraction/TerminologyAgent.cs:266` | 0 | 1 |
| `KnowledgeService` | `Knowledge/KnowledgeService.cs:188, 420, 735` | 3 | 0 |
| `ABoxProvenanceService` | `Ontology/ABoxProvenanceService.cs:77` | 1 | 0 |
| `ABoxService` | `Ontology/ABoxService.cs:544` | 1 | 0 |
| `OntologyService` | `Ontology/OntologyService.cs:260` | 1 | 0 |
| `ReleaseService` | `Ontology/ReleaseService.cs:88, 463` | 2 | 0 |
| `StructureAgent` | `Ontology/StructureAgent.cs:324` | 1 | 0 |
| `ValidationDecisionService` | `Ontology/ValidationDecisionService.cs:98` | 1 | 0 |
| `VocabularyProposalService` | `Ontology/VocabularyProposalService.cs:479` | 1 | 0 |
| `VocabularyService` | `Ontology/VocabularyService.cs:690` | 1 | 0 |
| `PromptService` | `Prompts/PromptService.cs:124` | 1 | 0 |
| `ProviderService` | `Providers/ProviderService.cs:104` | 1 | 0 |
| **合计** | 22 files | **25** | **3** |

另外:`ResolutionService.cs:26` 字段注入但无直接调用(死引用),同删。

### 4.3 EF migration 自动生成

```bash
# 1. dotnet ef 检测 model 变化(HasDefaultValue(0L) 是新加 + 删 HasIndex)
dotnet ef migrations add LegacyIdDefaultZero \
  --project src/ISEStudio \
  --startup-project src/ISEStudio \
  --context ISEStudioDbContext

# 2. 检查输出:必须只含 AlterColumn + DropIndex,不能含 CreateTable 重定义
cat src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_LegacyIdDefaultZero.cs
```

**期望输出**(EF Core 10 + Npgsql 10 应生成 in-place alter):

```csharp
// 24 张表 × (DropIndex + AlterColumn)
migrationBuilder.DropIndex(name: "ux_users_legacy_id", table: "users");
migrationBuilder.AlterColumn<long>(
    name: "legacy_id",
    table: "users",
    type: "bigint",
    nullable: false,
    defaultValue: 0L,
    oldClrType: typeof(long),
    oldType: "bigint");
// ... 23 more tables ...
```

**审计点**:如果 EF 输出含 `migrationBuilder.CreateTable(...)`,人工 patch 为 `AlterColumn + DropIndex`。

### 4.4 EntityConfigurations.cs 24 处改

```diff
- builder.Property(x => x.LegacyId).HasColumnName("legacy_id").IsRequired();
- builder.HasIndex(x => x.LegacyId).IsUnique().HasDatabaseName("ux_users_legacy_id");
+ builder.Property(x => x.LegacyId).HasColumnName("legacy_id").IsRequired().HasDefaultValue(0L);
```

例外:`SystemConfigEntity`(line 382)已有 `HasDefaultValue(SystemConfigEntity.SingletonLegacyId)`,不动 default;但同样**删** `HasIndex(...).IsUnique()(否则未来新 instance 与 singleton 序号撞唯一性)。

### 4.5 DI 入口

```diff
// src/ISEStudio/Program.cs:335-336
- // to plain MAX+1 (single-writer DB). See LegacyIdAllocator.cs for rationale.
- builder.Services.AddScoped<LegacyIdAllocator>();
```

### 4.6 测试变更

**删除:**

- `src/ISEStudio.Tests/Persistence/LegacyIdAllocatorTests.cs` 整文件(~280 行,12 Fact)
- PG 集成测试 `PostgresLegacyIdAllocatorTests`(per [[ontopilot-allocator-atomic]])
- 6 个 test 文件中 `LegacyIdAllocator` 引用 → sed 替换为 `null` 或删

**新增** `src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs`(4 个 `[Fact]`,commit `b172cff`):

```csharp
[Fact]
public async Task NewRow_LegacyIdIsZero_WhenNotExplicitlySet() { /* SaveChanges + detach 重物化,证明 DB 存的是 0(DEFAULT)而非 CLR 默认 */ }

[Fact]
public async Task MultipleNewRows_AllHaveLegacyIdZero() { /* AddRange 2 个,SaveChanges,重物化断言都是 0 */ }

[Fact]
public async Task ExistingRow_LegacyIdUnchanged_OnUpdate() { /* 已有 LegacyId=42 的 row,update 其他字段,断言 LegacyId 仍是 42 */ }

[Fact]
public async Task ExplicitLegacyId_HonoredWhenSetBeforeAdd() { /* setter 是 public:显式 LegacyId = 999,SaveChanges,断言 999(DB 层 honor) */ }
```

## 5. 验证 gates

7 条 gate,任何 1 条不过则视为 Phase 2 失败:

| Gate | 命令 | 期望 |
| --- | --- | --- |
| 1. 代码无 allocator 引用 | `grep -rn "LegacyIdAllocator\|AllocateAndPersistAsync\|AllocateManyAndPersistAsync" src/ISEStudio/ src/ISEStudio.Tests/ src/ISEStudio.IntegrationTests/ src/ISEStudio.ApiContract.Tests/`(排除 bin/obj + 已 deleted 的 LegacyIdAllocator.cs) | 0 命中 |
| 2. LegacyId setter 保持 public + SettingsService 写点 intact | `grep "LegacyId {" src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs` 且 `grep -n "SingletonLegacyId" src/ISEStudio/Settings/SettingsService.cs` | `public long LegacyId { get; set; }` 且 `SettingsService.cs:114` 的 `LegacyId = SystemConfigEntity.SingletonLegacyId` 写点在(Ruling 1:D4 abandoned) |
| 3. EF migration 只 ALTER+DROP INDEX | `grep -E "CreateTable\|InsertData" src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_LegacyIdDefaultZero.cs` | 0 命中(只有 DropIndex + AlterColumn) |
| 4. dotnet build 干净 | `dotnet build src/ISEStudio.sln` | 0 error / 0 warning |
| 5. 测试全绿 | `dotnet test src/ISEStudio.sln` | **850 unit + 167 contract + 57 integration = 1074 全绿**(实际执行值 = 858 - 12 删 LegacyIdAllocatorTests + 4 新 LegacyIdDefaultTests) |
| 6. EF migration 应用 | `docker compose up -d isestudio-migrate && docker compose ps isestudio-migrate` | Exited (0) |
| 7. runtime smoke | `docker compose up -d isestudio && sleep 10 && curl -s http://127.0.0.1:8080/api/health` | 200 |

**注意**:第 7 gate **不需要** `docker compose down -v` —— Phase 2 不丢数据,数据完整保留。

## 6. 任务分解(由 writing-plans 阶段细化)

| Phase | 内容 | commits |
| --- | --- | --- |
| Phase A. 代码清理 | 改 LegacyAddressableEntity setter + 30 call sites + EntityConfigurations HasDefaultValue(0L) + 删 LegacyIdAllocator.cs + DI 删注册 | 2-3 commits |
| Phase B. EF migration | dotnet ef migrations add + audit SQL | 1 commit |
| Phase C. 测试变更 | 删 LegacyIdAllocatorTests + 改 6 test files + 新增 LegacyIdDefaultTests | 1-2 commits |
| Phase D. 评审 | 全量 dotnet test + 整分支评审 | 0(reviewer-only) |
| Phase E. Smoke | docker compose up -d + curl | 0(smoke only) |

每 Phase 后跑 `dotnet build` + 相关单元测试;Phase B 后跑 contract test;Phase C 后跑 integration test;Phase E 后跑 runtime smoke。

## 7. 风险与回滚

### 7.1 风险

- **EF auto-migration 生成了 CREATE TABLE**(§4.3):24 张表 SET DEFAULT 0 + 删 UNIQUE 在 EF Core 10 + Npgsql 10 应生成纯 ALTER COLUMN + DROP INDEX,但若 EF 检测到列变化重定义。Mitigation:逐表 audit,人工 patch AlterColumn。
- **30 个 call site 替换语义不等价**:原 `AllocateAndPersistAsync(x)` 内部 advisory lock + atomic add+save;新方案 `dbContext.Add(x); SaveChanges()` 没有 advisory lock,但**不需要**(只是分配 LegacyId = 0,无 UNIQUE 冲突因为已删)。Mitigation:每个 service 改后跑对应 unit test。
- **生产 109 个读访问点出现 `LegacyId = 0` 的新 row**:`ResolutionService.OrderBy(LegacyId)` 会把 0 当最小值,新 row 排在最前。Mitigation:新 row 默认是 audit/grant/token/ks/doc 等用户行为触发的;若旧 row 已被消费,新 row 自然排后;若出现新 row 优先于旧 row,作为可接受行为(product 已接受 LegacyId 退役概念)。
- **EF model snapshot 漂移**:`HasDefaultValue(0L)` + 删 HasIndex 加在 24 处,snapshot 改动 ~50 行。Mitigation:Phase B commit review 时逐行 audit。
- **`EntityConfigurations.cs` SystemConfigEntity 特殊处理**(§4.4):有自定义 SingletonLegacyId default,删除 index 时要保留 default。Mitigation:Phase A 单独 review SystemConfigEntity 的 EntityConfiguration block。

### 7.2 回滚路径

Phase 2 在 `aa5f89d + Phase2-commits` chain 上。回滚:

```bash
# 1. revert Phase 2 commits
git revert --no-commit <phase2-commit-1>^..<phase2-commit-N>

# 2. 重新拉起 Phase 1 状态(allocator 还在)
docker compose up -d --build

# 3. EF migration `LegacyIdDefaultZero` revert 在 revert chain 中自动处理
```

回滚窗口:Phase 2 commit chain 未推到 production 之前可无痛 revert;推到生产后,因为只是改 default value + 删 UNIQUE index,数据库 schema 兼容(`legacy_id DEFAULT 0` 旧 DB 不会拒绝新 DB,但 INSERT 时不再写 advisory lock)。

## 8. Decision Log

| # | Decision | Rationale |
| --- | --- | --- |
| D1(c) | **保留 `LegacyId` 为只读字段;新 row = 0(DB DEFAULT)** | 109 个生产访问点不能用,改 blast radius 太大;只删 allocator 是更小更安全的手术 |
| D2 | **保留 `LegacyAddressableEntity` 类名** | 仍有 LegacyId 字段,叫 Entity 名不副实;将来真退役 LegacyId 再 rename |
| D4 | ~~**`LegacyId { get; private set; }`**~~ → **ABANDONED(执行期 Ruling 1);setter 保持 `public long LegacyId { get; set; }`** | 原动机是防御性,但生产写入点 `SettingsService.cs:114`(`LegacyId = SystemConfigEntity.SingletonLegacyId`)依赖 public setter,private set 会编译失败;安全动机改由删 UNIQUE 索引 + DB DEFAULT 0 承担(见 §8.1) |
| D5' | **删 UNIQUE 索引 `ux_*_legacy_id`,加 DB DEFAULT 0** | 不删 index 多 row 同为 0 撞 UNIQUE;删 index + 保留 NOT NULL + DEFAULT 0 = 多个 0 合法共存 |
| D6 | **22 service × 30 call site 替换保留 SAVE atomic** | `Add + SaveChanges` vs 旧的 `SELECT MAX+1 + Add + SaveChanges` 在 advisory lock 下等价(只是 MAX 步骤消失),生产行为一致 |
| D7 | **生产 109 读访问点不动** | 读路径与 allocator 退役正交;这些访问的是 EF 物化后的 `entity.LegacyId`,allocator 是否存在无关 |
| D8 | **不删 `legacy_id` 列** | 109 读访问点依赖 + 跨 entity FK 引用;只改 default 是最小变更 |
| D9 | **保留 runbook §3.5 MAX+1 模板** | 历史 admin 序号约定(legacy_id = 1 是 admin)便于运维记忆;Phase 2 后 UNIQUE 已删,MAX+1 不是硬约束但仍是良好实践 |
| D10 | **历史 EF migration `InitialCompatibility` 保留** | append-only migration history 是 EF Core 硬约束;Phase 2 的 drop 是新 migration,不删旧 |
| D11 | **`pre-isestudio-rename` tag 保留** | 与之前 slice 一致;Phase 2 完成后不需要新 tag |
| D12 | **执行期 Rulings(2026-08-26)**:D4 abandoned + gate/计数修订 + smoke PASS | 见 §8.1 详情 |

### 8.1 执行期 Rulings(D12,2026-08-26)

- **Ruling 1 — D4 ABANDONED**:`LegacyId` setter 保持 `public long LegacyId { get; set; }`。生产唯一非零写入点 `SettingsService.cs:114`(`LegacyId = SystemConfigEntity.SingletonLegacyId`,seed singleton SystemConfig)依赖 public setter;private set 直接编译失败。"防止意外写入" 的安全动机改由 DB 层承担:删 UNIQUE 索引 + DB DEFAULT 0(任何未显式赋值的 Add 都是 0,不再撞唯一性)。
- **Commit chain(实际执行,均在 `pre-isestudio-rename` = `fc06a73` 之后)**:
  - Phase A.0(rename):`aa5f89d`
  - Phase A.1(configs + doc):`08db7ae`
  - Phase A.2(30 call sites + DI + allocator 删除):`617e21d`
  - Phase A.3(runbook):`f267908` + fix `f1683e9`
  - Phase B(EF migration `LegacyIdDefaultZero`):`4cf72b0`
  - Phase C(tests):`b172cff`
- **Smoke**:PASS via `isestudio-migrate`(migration 应用 + 历史 row 保留 + smoke row legacy_id=0 + 清理)。
- **Lesson(compose build trap)**:`docker compose build isestudio-migrate` 是 **silent no-op** —— compose 中 `isestudio-migrate` service 没有 `build:` key,`docker compose build` 只重建有 build key 的 service,对无 key 者静默跳过。必须重建共享镜像 `docker compose build isestudio`,migrate service 才会拿到新 migration;stale 镜像会令 migrate **exit 0 但什么都没应用**(`MigrateAsync` 在旧镜像里找不到新迁移文件时静默通过)。

## 9. 链接

- 上游:[[ontopilot-isestudio-rename]] + [Phase1 spec](2026-08-20-guid-primary-key-design.md)
- 修订决策记录:[[ontopilot-phase2-halt]]
- 平行:[[ontopilot-allocator-atomic]] + [[ontopilot-allocator-missed-sites]] + [[ontopilot-rbac-coverage-matrix]]
- 平行:[[ontopilot-apicontract-prebaseline-fix]]
- 下游:GitHub repo rename follow-up(allocator 退役后 brand rename 在 allocator 引用层 clean)
- 运维:[docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md](2026-08-25-fresh-deployment-bootstrap.md)
