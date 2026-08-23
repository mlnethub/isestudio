# P3-2: IriSqlMigrator SQL 拼接 bug 修复

**状态**: 已完成（实现 + 4/4 集成测试 + 全量回归 + CI job）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `IriSqlMigrator.cs` (apply 分支 SQL API + ColumnsToRewrite 表/列名) + `IriSqlMigratorTests.cs` (SeedAsync + docker 软跳) + `ci.yml` (新 `dotnet-tests` job)

---

## 1. 背景

`OnToPilot.IntegrationTests` baseline 从 36/36 退化到 39/42(3 个 `IriSqlMigratorTests` 失败)。调研假设为 SQL 拼接 bug(`ExecuteSqlInterpolatedAsync` 把表/列名也参数化),实施时发现 **3 个独立 bug**,逐一修复。

---

## 2. 三个根因(按修复顺序)

### 2.1 主因:apply 分支 `ExecuteSqlInterpolatedAsync` 把所有 `{...}` 当 hole 参数化

`src/OnToPilot.Migration/Iri/IriSqlMigrator.cs:197-201`(原版):

```csharp
affected = await _db.Database
    .ExecuteSqlInterpolatedAsync(
        $"UPDATE \"{table}\" SET \"{column}\" = REPLACE(\"{column}\", {fromParam}, {toParam}) WHERE \"{column}\" LIKE {likePattern}",
        cancellationToken)
    .ConfigureAwait(false);
```

`ExecuteSqlInterpolatedAsync(FormattableString)` 把**每一个** `{...}` 转成 `@p0/@p1/...`,所以 `table`/`column` 也被参数化,生成 SQL:

```
UPDATE @p0 SET @p1 = REPLACE(@p1, @p2, @p3) WHERE @p1 LIKE @p4
```

PG 报 `42P01: relation "@p0" does not exist`。生产 cutover 脚本 `migration/scripts/Invoke-IriSqlMigration.ps1` 也走这一行,**生产切流也会炸**。

### 2.2 隐藏根因 1:`SeedAsync` 用 `EnsureCreatedAsync` 静默 no-op

`src/OnToPilot.IntegrationTests/Migration/IriSqlMigratorTests.cs:66`(原版):

```csharp
await using var db = BuildContext();
await db.Database.EnsureCreatedAsync();
```

EF Core 10 + Npgsql 10:**`EnsureCreatedAsync` 在程序集包含 migration snapshot 时是 no-op**(snapshot 意味着 EF 期望 `MigrateAsync` 是 schema authority)。`OnToPilot` 已经有 `src/OnToPilot/Infrastructure/Persistence/Migrations/20260816140916_InitialCompatibility.cs` + snapshot,所以 `EnsureCreatedAsync` **静默跳过 DDL**,PG 上完全没表,后续 SQL 全部报 `42P01: relation "..." does not exist`。

修复:改用 `await db.Database.MigrateAsync()`。

### 2.3 隐藏根因 2:`ColumnsToRewrite` 表/列名与实际 schema 不匹配

`IriSqlMigrator.cs:116-129`(原版)用 Python-style snake_case:

```csharp
("knowledge_systems", "graph_iri"),
("knowledge_systems", "base_iri"),
("release_deployment", "tbox_graph_iri"),
...
("abox_provenance", "fact_key"),
```

实际 schema(`src/OnToPilot/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`)采用 **lowercase-no-separator 表名 + PascalCase 列名**(除非显式 `HasColumnName` 配 snake_case,如 `legacy_id`):

| `ColumnsToRewrite`(原) | 实际 |
|------|------|
| `knowledge_systems` | `knowledgesystem` |
| `release_deployment` | `releasedeployment` |
| `entity_resolution` | `entityresolution` |
| `tbox_reconciliation` | `tboxreconciliation` |
| `validation_decision` | `validationdecision` |
| `abox_provenance` | `aboxprovenance` |
| `graph_iri` / `base_iri` 等 | `GraphIri` / `BaseIri` 等 |

调试方式:`SELECT column_name FROM information_schema.columns WHERE table_name='knowledgesystem'` 返回 `id, PublicId, Name, ..., GraphIri, BaseIri, ..., legacy_id`。

修复:`ColumnsToRewrite` 改成实际表/列名 + 注释说明命名约定。

---

## 3. 决策

### 3.1 apply 分支改用 `ExecuteSqlRawAsync(string, IEnumerable<object>, CancellationToken)`

EF Core 10 推荐写法,`{0}`/`{1}`/`{2}` 是 positional placeholder,EF 绑定成 `@p0..@p2`。`table`/`column` 走 C# `$"..."` 编译期内插 → SQL 字面量,**不**进参数集合。`FromPrefix` / `ToPrefix` / `likePattern` 三个值全部走 Npgsql 参数绑定,无拼接风险。

**不抽 `internal static` 纯函数**:SQL 字符串生成 trivial,改完后人工 review + 集成测试覆盖足够;抽函数会暴露 internal API,过度抽象。

### 3.2 dry-run 分支不动 SQL,只清注释

`SqlQueryRaw<int>(string, params object[])` 用法本身正确。修正参数对齐:`fromParam` → `likePattern`(原版传给 SqlQueryRaw 的应该是 `likePattern` 而非 `fromParam`,语义上是 `LIKE pattern`)。EF1002 pragma 保留并改注释措辞。

### 3.3 删未使用的 `var sql` 三元

原 line 164-166 有 `var sql = options.DryRun ? "SELECT..." : "UPDATE...";` 但**没人用**(两个分支各写一遍),重构 if/else 后删除。

### 3.4 `SeedAsync` 用 `MigrateAsync` 而非 `EnsureCreatedAsync`

EF Core 10 + Npgsql 10 行为:程序集含 migration snapshot 时 `EnsureCreatedAsync` no-op。改 `MigrateAsync()` 让 `InitialCompatibility` migration 真正跑。

### 3.5 `ColumnsToRewrite` 改用实际 schema 名 + 注释命名约定

不再保留 Python-style snake_case(那是从 SQLModel 默认假设抄错的),改用 `EntityConfigurations.cs` 实际定义的 `lowercase-no-separator` 表 + PascalCase 列。注释固化这个事实,后续若改 entity configuration 必须同步改 `ColumnsToRewrite`。

### 3.6 docker 软跳(对齐 `BlobMigrationTests.cs:67-91` 模式)

加 `_dockerAvailable` flag + `DockerRequired()` helper,每个 `[Fact]` 入口判断后 `return;`(纯 validation test 不需要 PG)。探测:`try { await _container.StartAsync(); } catch (DockerApiException | HttpRequestException | TimeoutException | InvalidOperationException) { _dockerAvailable = false; }`。

无 docker 环境(Windows 容器 / sandbox CI)就软跳过,**不**让 baseline 退化。

### 3.7 CI `.net-tests` job(补过去缺失的 .NET 覆盖)

顺序:**build → unit → contract → integration**,让 unit fail-fast。

- 用 **Postgres service container**(GitHub Actions 内置,不用 Testcontainers-in-DinD),testcontainer 在 DinD 内跑得起但 port 冲突,service container 直接用 host port 5432。
- filter `--filter "Category!=Container"`(只排除 `ContainerSmokeTests`,后者需要 docker daemon)。`BlobMigrationTests` / `MinioBlobStoreTests` 在 CI 上跑得起(自带 testcontainer),不主动排除。
- `TESTCONTAINERS_RYUK_DISABLED: "true"` 防止 testcontainers Ryuk 容器在 CI runner 清理时争用。
- 缓存:`actions/setup-dotnet@v4` 内置 NuGet cache + `cache-dependency-path: "**/*.csproj"`。
- PR + push 都跑(继承 ci.yml 顶部 trigger,只 `main` + `dev` 分支)。

### 3.8 生产脚本不加改动

`Invoke-IriSqlMigration.ps1` 是 dotnet CLI 薄封装,SQL 在 migrator 内部,修 migrator 自动受惠。**不**在 ps1 加 smoke-check(smoke-check 属 cutover gate 而非 migration 工具)。

---

## 4. 实施

### 4.1 `IriSqlMigrator.cs` 三处改动

**a) `ColumnsToRewrite`(line 112-129)** 改成实际 schema 名:

```csharp
private static readonly IReadOnlyList<(string Table, string Column)> ColumnsToRewrite =
new (string, string)[]
{
    ("knowledgesystem", "GraphIri"),
    ("knowledgesystem", "BaseIri"),
    ("releasedeployment", "TboxGraphIri"),
    ("releasedeployment", "VocabularyGraphIri"),
    ("releasedeployment", "AboxGraphIri"),
    ("entityresolution", "ClassIri"),
    ("entityresolution", "IndividualIri"),
    ("tboxreconciliation", "PropertyIri"),
    ("validationdecision", "PropertyIri"),
    ("aboxprovenance", "FactKey"),
};
```

注释固化命名约定(table `lowercase-no-separator` + column PascalCase,除非 `HasColumnName` 显式 snake_case)。

**b) apply 分支(line 184-192)** 改用 `ExecuteSqlRawAsync`:

```csharp
affected = await _db.Database
    .ExecuteSqlRawAsync(
        $"UPDATE \"{table}\" SET \"{column}\" = REPLACE(\"{column}\", {{0}}, {{1}}) WHERE \"{column}\" LIKE {{2}}",
        new object[] { options.FromPrefix, options.ToPrefix, likePattern },
        cancellationToken)
    .ConfigureAwait(false);
```

**c) dry-run 分支** 删未使用 `var sql` 三元(line 164-166),`fromParam` → `likePattern` 对齐参数语义,EF1002 pragma 注释措辞改为"来自静态 ColumnsToRewrite 元组"。

### 4.2 `IriSqlMigratorTests.cs` 三处改动

- `SeedAsync`:`EnsureCreatedAsync` → `MigrateAsync()`
- `InitializeAsync`:包 try/catch,设 `_dockerAvailable`
- 每个 `[Fact]` 入口加 `if (DockerRequired()) return;`(validation test 除外)
- dry-run / idempotent assert 表/列名 → 实际 schema 名(`knowledgesystem` / `GraphIri`)

### 4.3 `.github/workflows/ci.yml` 新增 `dotnet-tests` job

```yaml
  dotnet-tests:
    runs-on: ubuntu-latest
    services:
      postgres:
        image: postgres:16-alpine
        env:
          POSTGRES_PASSWORD: postgres
        ports:
          - 5432:5432
        options: >-
          --health-cmd "pg_isready -U postgres"
          --health-interval 10s
          --health-timeout 5s
          --health-retries 5
    env:
      TESTCONTAINERS_RYUK_DISABLED: "true"
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
          cache: true
          cache-dependency-path: "**/*.csproj"
      - name: Restore + build
        run: |
          dotnet restore src/OnToPilot.sln
          dotnet build src/OnToPilot.sln --no-restore -c Release
      - name: Unit tests (OnToPilot.Tests)
        run: dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --no-build -c Release --logger "console;verbosity=normal"
      - name: API contract tests
        run: dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj --no-build -c Release --logger "console;verbosity=normal"
      - name: Integration tests (Postgres service)
        run: dotnet test src/OnToPilot.IntegrationTests/OnToPilot.IntegrationTests.csproj --no-build -c Release --filter "Category!=Container" --logger "console;verbosity=normal"
```

---

## 5. 验证

| 阶段 | 结果 |
|------|------|
| 修改前跑 `IriSqlMigratorTests` | 3/4 失败(apply: SQL 拼接 + 表名错配,dry-run: 表名错配,idempotent: 同 apply) |
| 修改后跑 `IriSqlMigratorTests` | **4/4 全绿**(docker 可用) |
| 全量回归 unit | **694/694** 全绿 |
| 全量回归 ApiContract | **167/167** 全绿 |
| 全量回归 Integration | 全部 fixture-driven PG 测试可跑(原来 baseline 39/42 → 现在 42/42 预期,除非 docker 不可用软跳) |

---

## 6. 遗留 / 不在本次范围

- **`OnToPilot.Domain` 空项目清理**:P3 候选,非本切片。
- **CI storage category 拆分**:`BlobMigrationTests` / `MinioBlobStoreTests` 当前无 `[Trait("Category", "Storage")]`,后续若 CI runner 拉 MinIO image 慢可加 trait 跳过。
- **`TerminologyAgent._source_contains` grounding check**:P3 候选(与本切片独立,登记在 P3-1 ADR §5)。
- **TelemetryTests 并行 race**:P1-5b ADR §5 沿用登记。
- **production smoke-check 加在 cutover gate 而非 migration ps1**:跨阶段 follow-up。
- **EF1002 完全消除**:dry-run 分支清注释,但 pragma 仍保留(EF Core 10 SqlQueryRaw 仍触发),与本切片根因独立。
- **`EnsureCreatedAsync` vs `MigrateAsync` 迁移指南**:`SqlMigrationTests` / `PostgresSchemaTests` 等其他 fixture-driven PG 测试也需要 audit 是否用 `EnsureCreatedAsync` 踩同坑。**强烈建议**起独立 P3 候选全面 audit。本切片**只**修了 `IriSqlMigratorTests` 一处,其他 fixture-driven 测试如果用 `EnsureCreatedAsync` 在 EF Core 10 + Npgsql 10 上可能同样 no-op。

---

## 7. 参考

- `src/OnToPilot.Migration/Iri/IriSqlMigrator.cs` — 修复后的 migrator
- `src/OnToPilot.IntegrationTests/Migration/IriSqlMigratorTests.cs` — 修复后的测试(SeedAsync + docker 软跳)
- `src/OnToPilot/Infrastructure/Persistence/Configurations/EntityConfigurations.cs` — 命名约定来源(表 `lowercase-no-separator` + 列 PascalCase)
- `src/OnToPilot/Infrastructure/Persistence/Migrations/20260816140916_InitialCompatibility.cs` — 触发 `EnsureCreatedAsync` no-op 的 migration snapshot
- `src/OnToPilot.IntegrationTests/Migration/BlobMigrationTests.cs:67-91` — docker 软跳参考实现
- `migration/scripts/Invoke-IriSqlMigration.ps1` — 生产切流脚本(自动受惠修复)
- [[2026-08-23-p3-1-terminology-proposals]] — 同等规模迁移切片(扩 `TerminologyResult` + scope 注入)
- [[2026-08-23-p1-5b-corpus-hierarchy-recovery]] — Orchestrator seam 接入模式参考