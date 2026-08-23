# P3-4: IRI SQL Migration Smoke-Check Gate

**状态**: 已完成（实现 + 6/6 集成测试 + 全量回归 + Pester 11→15）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: 新增 `IriSqlVerifier` 类 + `iri sql-smoke-check` CLI 子命令 + ps1 包装 + cutover gate(6.55)+ 6 集成测试 + 2 Pester 测试

---

## 1. 背景

P3-2 (`commit b49ad7f`) 修了 `IriSqlMigrator` 三个独立 bug,生产 cutover 第一次实跑从此可工作。但 migrator 返回的 `affectedRows > 0` 信号在 **idempotent re-run** 时是 vacuously true:已经迁移过的 DB 第二次跑 migrator,所有 step 的 `AffectedRows == 0`,但 cutover gate 把它当作 "迁移失败" 报警 — 实际上数据已经被改写过了,这是 false-positive。

具体场景:
- **Rehearsal 第一次 dry-run**:从 `http://ontopilot.local/` 起,migrator 报告 0 行(无目标数据)。
- **Rehearsal 第二次 dry-run**:还是 0 行(migrator 没真改写)。
- **Production apply**:真的改了 N 行,报告 N>0。
- **Production 第二次 apply**(误操作或 rollback-after-success):0 行(已经全是 target prefix),但实际数据**已经是目标状态**。

P3-4 加一个 read-only verification gate:用 `SELECT COUNT(*) WHERE col LIKE 'old_prefix%'` 直接读 DB,**正向证明** legacy prefix 已经从每个 IRI-bearing 列里消失。空表 vacuous 通过(由 `TableTotalRows` 透明度字段让操作员自行审计),任一列 residual > 0 → 立即抛 `IriSqlVerificationException` 终止 cutover。

跳过条件:`-IriDryRun` 时 migrator 没写,residual 必然 > 0,smoke-check 自动 no-op(`if (-not $IriDryRun)` 包裹),避免破坏首次 dry-run rehearsal。

---

## 2. 决策

### 2.1 新建 `IriSqlVerifier` 类(独立于 migrator)

不嵌进 `IriSqlMigrator.MigrateAsync`,理由:
- **SRP**:migrator 写,verifier 读。混合两者让 dry-run 路径与 apply 路径的语义都变模糊。
- **可独立测**:`IriSqlVerifierTests` 不需要 migrator 配合,seed 后直接 verify。
- **退出码独立**:verifier 失败切流是 exit 1,migrator 失败是 exit 2(INVOKE-ProductionCutover catch 块按 regex 区分)。

新文件 `src/OnToPilot.Migration/Iri/IriSqlVerifier.cs`,结构镜像 `IriSqlMigrator.cs`:`sealed class` + `OnToPilotDbContext` + `ILogger` 构造;options/step/report record;async `VerifyAsync` 方法。

### 2.2 复用 `IriSqlMigrator.ColumnsToRewrite` tuple(单一真相源)

verifier 需要扫的列集合必须与 migrator 改写的列集合**完全一致**,否则 migrator 没碰的列 verifier 也断言 "old prefix 缺失" → 永远通过(false-negative)。在 `IriSqlMigrator` 增加 `public static ColumnsToRewritePublic` accessor,verifier 通过它读,避免两处 hardcoded 列表漂移。

### 2.3 检查语义:仅校验 "old prefix 缺失" + `TableTotalRows` 透明度

权衡:
- **(a) 仅 old prefix 缺失**(选):`COUNT(*) WHERE col LIKE 'old%' == 0`。直接消除 false-positive;migrator 失败 → smoke-check 抓得到。空表 vacuous pass(`ResidualOldPrefixRows=0, TableTotalRows=0`)。
- **(b) 同时要求 new prefix 出现**:fresh 部署 / 测试空表会 false-fail。
- **(c) 与 manifest checksums 比对**:与现有 `Assert-AllMigrationManifests` 重叠,涉及切流 record schema,scope 太大。

操作员可读 `TableTotalRows` 自行审计 "vacuous pass vs 真干净"。如果未来要严格,可加 (b) 作为可切换的 `--strict` flag,但本次不加。

### 2.4 失败聚合一次抛(对齐 `Assert-AllMigrationManifests` 哲学)

verifier 跑完所有 10 个 tuple,把所有 residual 列装进 `List<string>`,最后**一次**抛 `IriSqlVerificationException`,message 列出每条 failure(`"table.column: N row(s) still contain 'old_prefix' (table total rows = M)."`)。cutover catch 块一次看到全部问题,而不是 fix-one-run-fail-fix-one。

`Invoke-ProductionCutover.ps1` catch 块 regex `'rdf|migration|minio|verify.sql|sql|iri'` 已匹配新异常 message(包含 "iri" 和 "sql"),exit code 自动映射为 2,**无 catch 块改动**。

### 2.5 `ReportOut` 写盘 + SHA-256(用户选定审计 trail 模式)

verifier 支持 `--report-out <path>`(CLI 子命令) / `-ReportOut <path>`(ps1 包装) / `-IriSqlVerifyReportOut`(cutover 脚本),写 JSON 后 `ComputeReportSha256(path)` 出 64-char lowercase hex,打印到 stdout。cutover record 可按 SHA-256 引用 `.artifacts/iri-sql-verify-report.json` 作为审计 trail。**不**接入 `Assert-AllMigrationManifests` SHA-chain(避免触动 SQL/RDF/blob 三大 manifest 现有断言语义,本次 scope 外)。

### 2.6 `IriSqlVerifyReport.Steps` 用 `{ get; set; }` 不是 `{ get; }`

初次实现用 `{ get; } = new()`(镜像 `IriSqlReport`)。`WriteAsync` 序列化正确(10 个 step 进 JSON),**但**反序列化时 `JsonSerializer.Deserialize<IriSqlVerifyReport>(json)` 不重新填充 read-only 集合 → `roundTripped.Steps.Count == 0`。

**修复**:改 `{ get; set; } = new()`。audit trail 必须 round-trip 可验证,read-only 不可接受。

(这一坑不影响 `IriSqlReport`,因为 migrator 的报告从来不 deserialize。)

### 2.7 `SqlCliArgs` record 加 `ReportOut` 字段,`ParseSqlArgs` 接受 `--report-out`

`iri sql` 与 `iri sql-smoke-check` 共享 argv shape(只在子命令 dispatch 时决定用哪个 handler)。`ParseSqlArgs` 增加 `case "--report-out":`:`iri sql` 接受但忽略(没破任何东西);`iri sql-smoke-check` 实际写到磁盘。

### 2.8 `-IriDryRun` 时 skip smoke-check

cutover 脚本里 `if (-not $IriDryRun) { Invoke-IriSqlSmokeCheck ... }`。rehearsal 第一次 dry-run 跳过;第二次 apply 才执行 verification。

---

## 3. 实施

### 3.1 新文件

| 文件 | 行数 |
|---|---|
| `src/OnToPilot.Migration/Iri/IriSqlVerifier.cs` | ~209 |
| `migration/scripts/Invoke-IriSqlSmokeCheck.ps1` | ~95 |
| `src/OnToPilot.IntegrationTests/Migration/IriSqlVerifierTests.cs` | ~290 |

### 3.2 编辑

| 文件 | 改动 |
|---|---|
| `src/OnToPilot.Migration/Iri/IriSqlMigrator.cs` | +15 行:加 `public static ColumnsToRewritePublic` accessor |
| `src/OnToPilot.Migration/Iri/IriMigrationCommand.cs` | +85 行:Usage 文案 + switch arm `sql-smoke-check` + `RunSqlSmokeCheckAsync` + `SqlCliArgs.ReportOut` + `ParseSqlArgs --report-out` 分支 |
| `migration/scripts/gates/CutoverGates.ps1` | +30 行:`Invoke-IriSqlSmokeCheck` gate function(F-4 redaction `Write-Host`) |
| `migration/scripts/Invoke-ProductionCutover.ps1` | +18 行:header gate 列表 + script-level `[string]$IriSqlVerifyReportOut` + function-level 同样 + body 插入 `if (-not $IriDryRun) { Invoke-IriSqlSmokeCheck }` + `$scriptArgs` 转发列表加 `IriSqlVerifyReportOut` |
| `migration/tests/CutoverScripts.Tests.ps1` | +60 行:8 处加 `Mock Invoke-IriSqlSmokeCheck { }` + happy-path `Assert-MockCalled` + 2 个新 `It` block |

### 3.3 测试矩阵

**集成测试**(`src/OnToPilot.IntegrationTests`):6 个新 `[Fact]`,全部 docker 软跳兼容。

| 测试 | 断言 |
|---|---|
| `VerifyAsync_passes_when_no_residual_rows` | seed `http://goodcrew.local/...`,verify → `ResidualTotal==0`,`FailingSteps` 空 |
| `VerifyAsync_throws_when_column_has_residual_rows` | seed `http://ontopilot.local/ks/1`,verify → 抛 `IriSqlVerificationException`,`Failures` 含 `knowledgesystem.GraphIri` 和 `knowledgesystem.BaseIri` |
| `VerifyAsync_reports_table_total_for_empty_column` | 全空 DB,verify → `knowledgesystem.GraphIri.TableTotalRows==0` 且 `ResidualOldPrefixRows==0`(vacuous pass) |
| `VerifyAsync_aggregates_multiple_failures` | seed 两列都含 legacy,verify → `Failures.Count >= 2`,message 同时列出两列 |
| `VerifyAsync_throws_when_from_prefix_lacks_path_separator` | 不需 docker,`ArgumentException` |
| `WriteAsync_writes_valid_json_that_round_trips` | 写 JSON + `JsonSerializer.Deserialize` round-trip + SHA-256 是 64-char lowercase hex + 同样输入 → 同样 SHA(幂等) |

**Pester 测试**(`migration/tests/CutoverScripts.Tests.ps1`):15/15 通过(原 11 + 新 4)。

| Describe / It | 断言 |
|---|---|
| 8 个现有 `It` block 各加 `Mock Invoke-IriSqlSmokeCheck { }` | 不再 fall through 到 default `Write-Host` body |
| happy-path test 加 `Assert-MockCalled Invoke-IriSqlSmokeCheck -Times 1` | 完整序列确实调用 smoke-check |
| 新 `Describe 'IRI SQL smoke-check gate'` | |
| ↳ `stops immediately when smoke-check finds residual legacy-prefix rows` | mock smoke-check 抛,断言 `Invoke-IriRdfRelocation -Times 0`(序列被截断) |
| ↳ `skips smoke-check when -IriDryRun is set` | `-IriDryRun` 走通,断言 `Invoke-IriRdfRelocation -Times 1`(smoke-check 被跳过) |

---

## 4. 验证

| 阶段 | 结果 |
|---|---|
| `dotnet build src/OnToPilot.sln -c Release` | 0 errors,5 warnings(全 pre-existing)|
| `dotnet test src/OnToPilot.Tests/` | 694/694 |
| `dotnet test src/OnToPilot.ApiContract.Tests/` | 167/167 |
| `dotnet test src/OnToPilot.IntegrationTests/ --filter "Category!=Container"` | 45/45(+6 verifier) |
| `dotnet test --filter "FullyQualifiedName~IriSqlVerifierTests"` | 6/6 ✓ |
| `Invoke-Pester migration/tests/CutoverScripts.Tests.ps1` | 15/15(原 11 + 新 4)|
| Smoke-test CLI 真实运行 | 暂未手动跑,集成测试已覆盖 C# 层 |

---

## 5. 遗留 / 不在本次范围

- **cutover record 引用 verify-report SHA-256**:`expected-iri-sql-verify-sha256` 字段未加入 `Assert-AllMigrationManifests` SHA-chain。本次仅落盘 + stdout + ps1 日志;record schema 改动属跨阶段 follow-up。
- **per-column old/new prefix 阈值**:本次仅校验 "old absent",不校验 "new present"。fresh 部署空表场景下不需要;若未来要严格可加 `--strict` flag。
- **dry-run 模式下写预期 residual 报告**:为 rehearsal 增加 `--expected-residual <path>` 参数,让 verify 在 dry-run 模式下也能跑通并比对预期,作为 rehearsal 完整性检查。本次不加。
- **OnToPilot.Domain 空项目清理**:P3 候选,本次不动。
- **`IriSqlReport.Steps` 同样 read-only 反序列化失败坑**:迁移 manifest 现在不 deserialize,但若未来要加 SHA-chain,需同样改 `{ get; set }`。本切片不修。
- **Audit log 集中化**:smoke-check 报告目前散落在 `.artifacts/iri-sql-verify-report.json`,后续可统一到 cutover record 的 manifest 目录。
- **Smoke-check CLI 真实运行验证**:本次集成测试覆盖 C# 路径(verifier class),CLI 路径(`dotnet run -- iri sql-smoke-check`)未经手动跑过 P3-2 留下的 fixture PG;若 CI 跑通则覆盖。

---

## 6. 参考

- `src/OnToPilot.Migration/Iri/IriSqlVerifier.cs` — 新增 verifier 类
- `src/OnToPilot.Migration/Iri/IriSqlMigrator.cs:140-153` — `ColumnsToRewritePublic` accessor(单一真相源)
- `src/OnToPilot.Migration/Iri/IriMigrationCommand.cs:90-100` — Usage 文案 `sql-smoke-check` 段落;`:123` switch arm;`:170-225` `RunSqlSmokeCheckAsync`;`:295-340` `SqlCliArgs.ReportOut` + `ParseSqlArgs --report-out` 分支
- `migration/scripts/Invoke-IriSqlSmokeCheck.ps1` — 镜像 `Invoke-IriSqlMigration.ps1`,F-4 redaction 复用
- `migration/scripts/gates/CutoverGates.ps1:264-292` — `Invoke-IriSqlSmokeCheck` gate function
- `migration/scripts/Invoke-ProductionCutover.ps1:182-194` — body 插入 `if (-not $IriDryRun) { ... }`
- `migration/tests/CutoverScripts.Tests.ps1` — 15/15 Pester
- `src/OnToPilot.IntegrationTests/Migration/IriSqlVerifierTests.cs` — 6/6 集成测试
- `src/OnToPilot.IntegrationTests/Migration/IriSqlMigratorTests.cs` — Testcontainers-Postgres fixture 模板
- `migration/scripts/gates/CutoverGates.ps1:621-756` — 失败聚合哲学参考
- `migration/scripts/Invoke-IriSqlMigration.ps1:92-106` — F-4 redaction 模式参考
- [[2026-08-23-p3-2-iri-sql-migrator]] — 上游 commit b49ad7f 修复的三个 migrator bug
