# Guid 主键 Phase 2:Legacy 字段退役(2026-08-26)

## 1. 背景

[Phase 1(2026-08-20)](2026-08-20-guid-primary-key-design.md) 已完成 **wire 表面** 的主键切换:所有 JSON / URL / FK 现在都是 `Guid Id`。但 **DB schema 与代码兼容层** 还在跑历史的 `legacy_id` 列 + `LegacyIdAllocator`,占着 24 表的 `bigint NOT NULL UNIQUE` 列与 advisory-lock allocator。

Phase 2 目标:**删 `legacy_id` 列 + 退役 `LegacyIdAllocator` + 简化 Entity 基类 + IriSqlMigrator 同步清理**,把整个栈的"双轨"压回单轨。代价是接受现有 postgres volume 数据清零(运维侧单独决策,见 [[ontopilot-isestudio-rename]] §"Unlock / next steps")。

### 1.1 动机

| 维度 | Phase 1 现状 | Phase 2 目标 |
| --- | --- | --- |
| Wire 主键 | `Guid Id`(单轨) | 不变 |
| DB 主键列 | `id uuid`(wire)+ `legacy_id bigint UNIQUE`(兼容层) | 仅 `id uuid`,删 `legacy_id` |
| DB 索引 | `PK_*` on `id` + `ux_*_legacy_id` on `legacy_id` | 仅 `PK_*` |
| Entity 基类 | `LegacyAddressableEntity : Entity { long LegacyId }`(24 个 entity 全继承) | 直接 `Entity`(无 LegacyId) |
| 主键分配 | `LegacyIdAllocator.AllocateAndPersistAsync`(advisory lock + MAX+1) | EF 自动 `Guid.NewGuid()`(已就绪) |
| IriSqlMigrator | `ColumnsToRewrite` 含 `legacy_id` | 不含(IRI rename 不再覆盖 legacy_id) |
| `dotnet test` 数量 | 858 + 167 + 63 = 1088 | ≤ 1088(LegacyId 相关测试被删) |
| 数据 | 现有 volume 有完整 users / KS / audit | 清 volume(运维同意) |

### 1.2 收益

- 代码可读性:`Entity` 直接持 `Guid Id`,不再有 "addressable" 副类
- 写入路径简化:`SaveChangesAsync()` 一次,无 advisory lock + MAX 读取
- DB schema 简化:24 表 -1 列 -1 索引,Postgres planner 略快
- 移除 advisory-lock 死代码:`LegacyIdAllocator` 退役
- IriSqlMigrator 移除死分支:`legacy_id` 已是 not exists,rewrite 路径不再需要

### 1.3 代价

- 数据全丢(users / KS / 抽取历史 / 配置 / token / audit)—— 运维侧接受,生产需提前 dump
- `legacy_id` 不再可作为 cross-table correlation 字段(Phase 1 已经把 wire 全切到 Guid,所以外部依赖已 0)
- 审计 / 回溯场景需要重新建索引(若 audit 表里有大量 `legacy_id` 引用,见 §3 数据迁移)

## 2. 范围

### 2.1 IN(本次触及)

**代码层:**
- `src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs`(整文件删除)
- `src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs`(整文件删除)
- 所有 entity 类的基类:`LegacyAddressableEntity` → `Entity`(24 个 entity,见 §4.1)
- 所有 `AllocateAndPersistAsync` / `AllocateManyAndPersistAsync` 调用点(13 个 per allocator doc + 历史 5 个补漏点,见 [[ontopilot-allocator-missed-sites]])
- `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`:`HasColumnName("legacy_id")` + `HasIndex(...ux_*_legacy_id)` 配置删除
- DI 注册:`AddScoped<LegacyIdAllocator>` 移除

**Stage 3 territory 顺手清理(rename 切片漏改的 6 个文件,class 已 rename 但 filename 未 rename):**

| 现 filename | 内容 class | 新 filename |
| --- | --- | --- |
| `OnToPilotDbContext.cs` | `ISEStudioDbContext` | `ISEStudioDbContext.cs` |
| `OnToPilotDbContextFactory.cs` | `ISEStudioDbContextFactory` | `ISEStudioDbContextFactory.cs` |
| `Mcp/OnToPilotMcpPrompts.cs` | `ISEStudioMcpPrompts` | `Mcp/ISEStudioMcpPrompts.cs` |
| `Mcp/OnToPilotMcpResources.cs` | `ISEStudioMcpResources` | `Mcp/ISEStudioMcpResources.cs` |
| `Mcp/OnToPilotMcpTools.cs` | `ISEStudioMcpTools` | `Mcp/ISEStudioMcpTools.cs` |
| `Serialization/OnToPilotJsonContext.cs` | `ISEStudioJsonContext` | `Serialization/ISEStudioJsonContext.cs` |

这 6 个文件 rename 是 **git mv**(保留 history)+ 不动 class body,合在 Phase A 一个独立 commit 里。

**EF 迁移层:**
- `src/ISEStudio/Infrastructure/Persistence/Migrations/`:
  - 新增 `20260826HHMMSS_DropLegacyId.cs`(生成 + 校验)
  - model snapshot 同步(`ISEStudioDbContextModelSnapshot.cs`)
  - 历史 `InitialCompatibility` 不删(只追加)

**IriSqlMigrator 层:**
- `src/ISEStudio.Migration/Iri/IriSqlMigrator.cs`:`ColumnsToRewrite` 移除 `legacy_id`
- `src/ISEStudio.Migration/Iri/IriSqlVerifier.cs`:`legacy_id` row 移除
- 相关测试:`src/ISEStudio.Tests/Migration/` + `src/ISEStudio.IntegrationTests/Migration/` 删对应 It

**测试:**
- `src/ISEStudio.Tests/` 下引用 `LegacyId` 的 assertion 删 / 改
- `src/ISEStudio.Tests/Infrastructure/LegacyIdAllocatorTests.cs`(整文件删除)
- ApiContract baseline(119/119)继续 100% 绿(因为 Phase 1 已经把 wire 切干净,Phase 2 不外露 legacy_id)

**Compose / 运维:**

- `docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md`(本次触及范围**仅限 `docs/superpowers/runbooks/` 下的 runbook**,其他 spec / Python baseline 不动):删 §3.3 schema 表的 `legacy_id` 行 + §3.5 INSERT 模板的 `legacy_id` 列;§0 加 "Phase 2 后此 runbook 的 INSERT 不再需要 legacy_id" 标注;§4.6 旧 volume 讨论加 "Phase 2 后已不适用,见 §3.2" 指引
- 临时清 volume 步骤:`docker compose down -v`(纳入 §5 验证 gate)

### 2.2 DO-NOT-TOUCH(本次不动)

- **EF migration 历史**:`20260816140916_InitialCompatibility.cs`(包含 `legacy_id` DDL)+ Designer。**保留**作为历史快照。Phase 2 生成的 `DropLegacyId` 是 **append**,不是 replace。
- **ApiContract baseline**:`docs/superpowers/specs/` 下 `ApiContractScenario.cs` 等 Phase 1 已经把 `legacy_id` 移出 wire,基线稳定。
- **Python baseline / 历史文档**:`docs/migration/`、`migration/scripts/`、`docs/superpowers/specs/2026-08-25-isestudio-rename-design.md` 中提到 `legacy_id` 的历史叙述,**不删不改**(与 [[ontopilot-python-retirement]] 模式一致;这些 spec 描述的是切片完成时的状态,而非当前运行时,改写 = 篡改历史)。
- **bootstrap runbook §3.5 INSERT 模板以外**:`docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md` 其余内容(触发条件 / 密码约束 / 常见踩坑 / Lesson learned)描述的是 fail-closed 行为,不依赖 `legacy_id`,保留。
- **`pre-isestudio-rename` tag** → `fc06a73` 保留。
- **`pre-python-retirement` tag** → `pre-isestudio-rename` 的 parent 保留。
- **`IriSqlMigrator` 的 `OnToPilot.local` → `goodcrew.local` rename 逻辑**:只删 `legacy_id` 相关,其他列(`username` / `displayname` / 等)继续在 ColumnsToRewrite 里。
- **Python .NET 命名 / 项目结构**:与本切片无关。

### 2.3 依赖与解耦

| 上游 | 关系 |
| --- | --- |
| [Phase 1 spec](2026-08-20-guid-primary-key-design.md) | Phase 2 的前置条件;Phase 1 把 wire 切干净,Phase 2 才能删 DB 列不破 wire |
| [BootstrapAdminService](2026-08-25-isestudio-rename-design.md) §7 D1 | Phase 2 删 volume 之后,**必须重新跑 seed-admin**,否则 backend exit 17(详见 runbook) |
| IriSqlMigrator | 共享 EF Core 实体类;Phase 2 改 snapshot 后,IRI migrator 必须重新生成 baseline SHA-256 |
| ApiContract harness | 不依赖 `legacy_id`;继续 100% 绿 |

| 下游 | 影响 |
| --- | --- |
| Guid PK Phase 3 / Long-running | 无 —— Phase 2 是 Phase 1 之后的清理,无下一阶段 |
| GitHub repo rename follow-up | 解锁了:Phase 2 完成意味着 brand rename 在数据层也彻底 clean |

## 3. 数据迁移策略

**结论:清 volume(运维已接受)。** 详见 [[ontopilot-isestudio-rename]] §"Unlock / next steps" + 本 spec §1.1 的"代价"行。

### 3.1 切前 snapshot(可选,推荐 staging/prod)

```bash
# 1. 停后端避免写入竞争
docker compose stop isestudio isestudio-migrate

# 2. dump postgres(safety net;Phase 2 后这台机器可以不要)
docker exec ontopilot-postgres-1 pg_dump -U isestudio -d isestudio \
  --no-owner --no-acl -Fc > ontopilot-pre-phase2-$(date +%Y%m%d).dump

# 3. dump minio bucket
docker run --rm -v ontopilot_isestudio-minio:/from -v $(pwd):/to alpine \
  sh -c 'tar czf /to/ontopilot-minio-pre-phase2.tar.gz -C /from .'
```

dump 文件保留 N 天由运维决定,与 Phase 2 切片本身解耦。

### 3.2 Phase 2 cutover

```bash
# 1. 停全部
docker compose down

# 2. 删 volume(关键步骤)
docker volume rm ontopilot_isestudio-postgres ontopilot_isestudio-data ontopilot_isestudio-minio

# 3. 拉起:迁移自动跑(isestudio-migrate 容器)+ seed-admin 一次
docker compose --profile bootstrap run --rm seed-admin
docker compose up -d --build

# 4. 验证 /api/health + login
curl -s http://127.0.0.1:8080/api/health
```

### 3.3 切后 smoke test

- `users` 表非空(seed-admin 写入的 admin 还在)
- 任意业务接口(GET /api/knowledge-systems)返 200 而非 500
- docker volume ls 不再有 `ontopilot_ontopilot-*` 残留(若有,清掉)

## 4. 实现策略

### 4.1 Entity 基类简化

**之前**(`LegacyAddressableEntity`):
```csharp
public abstract class LegacyAddressableEntity : Entity
{
    public long LegacyId { get; set; }
}
```

**之后**:
```csharp
// LegacyAddressableEntity 直接消失;所有 entity 改为继承 Entity
public class UserEntity : Entity { ... }   // was : LegacyAddressableEntity
public class ChunkEntity : Entity { ... }  // was : LegacyAddressableEntity
```

当前 24 个 entity 全继承 `LegacyAddressableEntity`(verified by `grep -rhE "^public.+: (LegacyAddressable|Auditable)?Entity" src/ISEStudio/Infrastructure/Persistence/Entities/ | sort -u`),Phase 2 全部机械替换为 `: Entity`。`AuditableEntity` / `Entity` 是仓库里已有的两类基类;Phase 2 不引入新基类,也不区分审计 / 非审计 —— 当前所有 entity 都需要 `CreatedAt` / `UpdatedAt`,统一继承 `Entity` 已满足。

迁移步骤:
```bash
# 1. 列出所有继承点
grep -rn ": LegacyAddressableEntity" src/ISEStudio/ \
  | grep -v "/bin/" | grep -v "/obj/" | grep -v ".dll"

# 2. sed -i 's/: LegacyAddressableEntity/: Entity/g' 跨 24 个 entity 文件
# 3. 删 LegacyAddressableEntity.cs
# 4. dotnet build + dotnet test 验证
```

### 4.2 LegacyIdAllocator 调用点移除

13 个 call site(per `LegacyIdAllocator.cs` doc-comment)+ 历史补漏点(per [[ontopilot-allocator-missed-sites]] memory)的统一处理:

**之前**:
```csharp
var entity = new UserEntity { ... };
await _allocator.AllocateAndPersistAsync(entity, ct);  // 写 advisory lock + MAX + SaveChanges
```

**之后**:
```csharp
var entity = new UserEntity { Id = Guid.NewGuid(), ... };
await _db.SaveChangesAsync(ct);  // EF 自动生成 Id(if OnBeforeSaveGenerateGuid hook 已就位)
```

`Guid Id` 的自动生成机制在 Phase 1 已经走通(`OnBeforeSaveGenerateGuid` hook 或 EF 默认行为);Phase 2 不动这块,只删 allocator。

### 4.3 EF 迁移生成

```bash
# 改完 §4.1 / §4.2 后,regen snapshot + add migration
dotnet ef migrations add DropLegacyId \
  --project src/ISEStudio \
  --startup-project src/ISEStudio \
  --context ISEStudioDbContext

# 检查生成的 SQL(必须只有 DROP COLUMN + DROP INDEX,不能有 CREATE TABLE 重定义)
cat src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_DropLegacyId.cs
```

**审计点**:如果 EF 输出的 migration 含 `migrationBuilder.CreateTable(...)` 这种"重建表"语句(常见于 EF 检测到列重排 / 类型变化),需人工 patch 为:
```csharp
migrationBuilder.DropColumn(name: "legacy_id", table: "users");
migrationBuilder.DropIndex(name: "ux_users_legacy_id", table: "users");
```

如果 migration 还含 `INSERT INTO ... (...)` 这种"灌回旧数据"的语句,在"清 volume"前提下可以接受(但建议改写为 just-drop,因为数据已不要)。

### 4.4 IriSqlMigrator 同步

`ColumnsToRewrite` 列表(`src/ISEStudio.Migration/Iri/IriSqlMigrator.cs`)移除 `legacy_id` 项:

```csharp
// 之前
private static readonly IReadOnlyList<string> ColumnsToRewrite = new[]
{
    "legacy_id",  // ← Phase 2 删
    "username",
    "displayname",
    // ...
};

// 之后
private static readonly IReadOnlyList<string> ColumnsToRewrite = new[]
{
    "username",
    "displayname",
    // ...
};
```

`IriSqlVerifier`(`src/ISEStudio.Migration/Iri/IriSqlVerifier.cs`)的 baseline 里 `legacy_id` row 同步移除,并 regen SHA-256 baseline;`tests/Migration/` 下 5-10 个对应 `It("legacy_id_*")` 块删除。

## 5. 验证 gates

7 条 gate,任何 1 条不过则视为 Phase 2 失败:

| Gate | 命令 | 期望 |
| --- | --- | --- |
| 1. 代码无 `LegacyId` 残留 | `grep -rn "LegacyId" src/ISEStudio/ src/ISEStudio.Tests/ src/ISEStudio.Migration/ src/ISEStudio.ApiContract.Tests/ src/ISEStudio.IntegrationTests/`(排除 bin/obj) | 0 命中 |
| 2. 代码无 `LegacyIdAllocator` 残留 | `grep -rn "LegacyIdAllocator\|AllocateAndPersistAsync\|AllocateManyAndPersistAsync" src/`(排除 bin/obj) | 0 命中 |
| 3. 实体无 `legacy_id` 列名引用 | `grep -rn "\"legacy_id\"" src/ISEStudio/ src/ISEStudio.Migration/`(排除 bin/obj + migrations 历史快照) | 0 命中(只允许出现在 `20260816140916_InitialCompatibility.cs` 历史 migration 中) |
| 4. dotnet build 干净 | `dotnet build src/ISEStudio.sln` | 0 error / 0 warning |
| 5. 测试全绿 | `dotnet test src/ISEStudio.sln` | 858 + 167 + 63 - LegacyId 相关测试 = N 全绿(N ≤ 1088) |
| 6. EF migration 只 DROP 不 CREATE | `grep -E "CreateTable\|InsertData" src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_DropLegacyId.cs` | 0 命中(只有 DropColumn + DropIndex) |
| 7. runtime smoke test | `docker compose down -v && docker compose --profile bootstrap run --rm seed-admin && docker compose up -d --build && sleep 10 && curl -s http://127.0.0.1:8080/api/health` | exit 0 + health 返回 200 + cookie 名 `isestudio_session` + login 200 |

## 6. 任务分解(由 writing-plans 阶段细化)

| Phase | 内容 | 估计 commits |
| --- | --- | --- |
| Phase A. 代码清理 | 删 `LegacyIdAllocator` 类 + DI + 13+ 调用点 + Entity 基类简化 | 1-2 commits |
| Phase B. EF 配置 | 改 `EntityConfigurations.cs` + regen snapshot + add `DropLegacyId` migration | 1 commit |
| Phase C. IriSqlMigrator 清理 | `ColumnsToRewrite` 移除 `legacy_id` + `IriSqlVerifier` baseline 更新 + 删测试 | 1-2 commits |
| Phase D. 测试 + 评审 | 全量 `dotnet test` + 整分支评审(opus) | 0 commits(reviewer-only) |
| Phase E. Runtime smoke | `docker compose down -v` + `seed-admin` + `up -d` + curl 验证 | 0 commits(smoke only)|

每 Phase 后跑 `dotnet build` + 相关单元测试;Phase B 后跑 contract test;Phase C 后跑 IRI migrator 集成测试;Phase E 后跑 runtime smoke。

## 7. 风险与回滚

### 7.1 风险

- **EF auto-migration 生成了 CREATE TABLE**(§4.3 审计点):可能在 regen 时因为 EF 检测到列顺序变化而重定义表。Mitigation:逐行 audit 生成的 SQL,人工 patch。
- **IriSqlMigrator baseline SHA-256 漂移**:Phase 2 改完 ColumnsToRewrite 后,smoke-check 的 SHA-256 baseline 必须重生成。Mitigation:CutoverGates.ps1 已在 P3-4 集成 CI,Phase 2 后跑 `iri sql-smoke-check --update-baseline`。
- **ApiContract 回归**:Phase 1 已锁 baseline,但 Phase 2 改 EF model 可能让某些集成测试 fixture 里 hardcoded 的 `legacy_id` 字面量找不到。Mitigation:`grep -rn "legacy_id" src/ISEStudio.ApiContract.Tests/` 预检。
- **Docker volume 误删**:运维若 dump 不全就 `down -v`,数据真没了。Mitigation:`§3.1 snapshot` 步骤必须先执行,跑通 `docker volume ls` 确认旧 volume 还在再删。

### 7.2 回滚路径

Phase 2 在 `e8c8d02 + df1bcb3 + 8064e7a + 8a8222f + 8d99b6d + Phase2` commit chain 上。回滚:

```bash
# 1. revert Phase 2 commits
git revert --no-commit <phase2-commit-1>^..<phase2-commit-N>

# 2. 重生 volume(如果之前 dump 了)
docker volume create ontopilot_isestudio-postgres
docker run --rm -v ontopilot_isestudio-postgres:/to -v $(pwd):/from alpine \
  sh -c 'pg_restore -d /to /from/ontopilot-pre-phase2.dump'

# 3. 重新拉起 Phase 1 状态
git checkout <pre-phase2-commit>
docker compose up -d --build
```

回滚窗口:Phase 2 全部 commit 但 **未** `docker compose down -v` 之前,可以无痛 revert + drop new code;一旦 `down -v` 跑过,回滚必须先用 dump(§3.1)。

## 8. Decision Log

| # | Decision | Rationale |
| --- | --- | --- |
| D1 | 清 volume,接受数据丢失 | 用户在 brainstorming 阶段直接决策(staging / dev 可重灌,生产需提前 dump) |
| D2 | 同时退役 `IriSqlMigrator` 的 `legacy_id` 重写分支 | 死代码不保留;verifier baseline 同步更新 |
| D3 | EF migration `DropLegacyId` 只 DROP,不重建 | EF Core 10 + Npgsql 10 在 column drop 上生成 in-place alter;若生成 CREATE TABLE 则人工 patch |
| D4 | 历史 EF migration `InitialCompatibility` 保留 | append-only migration history 是 EF Core 的硬约束;Phase 2 的 drop 是新 migration,不删旧 |
| D5 | `pre-isestudio-rename` tag 保留 | 与之前 slice 一致;Phase 2 完成后不需要新 tag |

## 9. 链接

- 上游:[[ontopilot-isestudio-rename]](brand rename + runbook 解锁了 Phase 2)+ [Phase1 spec](2026-08-20-guid-primary-key-design.md)
- 平行:`[[ontopilot-allocator-missed-sites]]`(已记录的 13+5 个 allocator 调用点是 Phase A 的清单)
- 平行:`[[ontopilot-apicontract-prebaseline-fix]]`(Phase 0.5 的 baseline 锁定延续到 Phase 2)
- 下游:GitHub repo rename follow-up(本 spec 完成后,brand 在所有层 clean)
- 运维:[`docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md`](2026-08-25-fresh-deployment-bootstrap.md)
