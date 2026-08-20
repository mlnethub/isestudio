# OnToPilot .NET 数据迁移与切换实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐项执行。步骤使用 `- [ ]` 跟踪。生产步骤必须由获授权操作人确认后执行。

**目标：** 为 PostgreSQL、Oxigraph 和本地 blob 生成可重复、可校验、可回滚的迁移工具，并在停写窗口安全切换到 .NET。

**架构：** 三类数据分别生成迁移 manifest，统一由 `Invoke-MigrationRehearsal.ps1` 编排。RDF 先验证目录副本直读；失败则走 N-Quads 逻辑迁移。生产切换脚本把停写、备份、独占锁、验证和回滚前置条件编码为硬门禁。

**技术栈：** PostgreSQL SQL/pg_dump、.NET Migration CLI、Oxigraph.NET、PowerShell、MinIO/S3

## 全局约束

- 所有迁移先在生产备份副本演练；禁止首次在生产数据上运行。
- Python 后端停止且 PostgreSQL 写权限撤销后，才可执行生产 SQL/RDF/blob 迁移。
- 原 RocksDB 目录始终保留只读回滚副本；.NET 只验证复制目录。
- SQL 每张表必须校验行数、关键外键 orphan 数与确定性内容 checksum。
- blob 必须逐对象校验 SHA-256；release artifacts 不迁入 MinIO。
- 任一门禁失败立即停止，不自动继续到下一数据层。

---

### 任务 1：实现 SQL GUID/LegacyId 迁移与回滚

**文件：**

- 创建：`migrations/SqlAlchemyToEfCore/001_add_guid_and_legacy_ids.sql`
- 创建：`migrations/SqlAlchemyToEfCore/002_backfill_foreign_keys.sql`
- 创建：`migrations/SqlAlchemyToEfCore/003_apply_ef_constraints.sql`
- 创建：`migrations/SqlAlchemyToEfCore/verify.sql`
- 创建：`migrations/SqlAlchemyToEfCore/rollback.sql`
- 创建：`src/OnToPilot.Migration/Sql/SqlMigrationCommand.cs`
- 测试：`src/OnToPilot.IntegrationTests/Migration/SqlMigrationTests.cs`

**接口：**

- 输出：24 表 GUID 主外键、稳定 `LegacyId`、兼容约束和可逆迁移日志。

- [ ] **步骤 1：写快照迁移失败测试**

```csharp
[Fact]
public async Task Sql_migration_preserves_rows_and_all_foreign_keys()
{
    var before = await Snapshot.CaptureAsync(PythonDatabase);
    await Migration.ApplyAsync(PythonDatabase, CancellationToken.None);
    var after = await Snapshot.CaptureAsync(PythonDatabase);
    Assert.Equal(before.TableCounts, after.TableCounts);
    Assert.All(after.OrphanCounts, pair => Assert.Equal(0, pair.Value));
    Assert.Equal(before.BusinessChecksums, after.BusinessChecksums);
}
```

- [ ] **步骤 2：在生产形态 fixture 上运行并确认失败**

运行：`dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~SqlMigration`
预期：失败，迁移脚本尚不存在。

- [ ] **步骤 3：实现可重入 SQL 和迁移日志**

```sql
CREATE EXTENSION IF NOT EXISTS pgcrypto;
ALTER TABLE "user" ADD COLUMN IF NOT EXISTS guid_id uuid;
ALTER TABLE "user" ADD COLUMN IF NOT EXISTS legacy_id bigint;
UPDATE "user" SET guid_id = gen_random_uuid() WHERE guid_id IS NULL;
UPDATE "user" SET legacy_id = id WHERE legacy_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_user_legacy_id ON "user"(legacy_id);
```

- [ ] **步骤 4：验证 apply、重复 apply 和 rollback**

运行：`dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~SqlMigration`
预期：首次迁移、幂等重跑、全部 FK、JSON/bytea、rollback 后 Python 可读均通过。

- [ ] **步骤 5：提交**

```bash
git add migrations/SqlAlchemyToEfCore src/OnToPilot.Migration/Sql src/OnToPilot.IntegrationTests/Migration/SqlMigrationTests.cs
git commit -m "feat: add reversible sql data migration"
```

### 任务 2：实现 RDF 跨绑定验证与 N-Quads 回退

**文件：**

- 创建：`src/OnToPilot.Migration/Rdf/RdfMigrationCommand.cs`
- 创建：`migration/fixtures/rdf-smoke-queries.json`
- 创建：`migration/scripts/Export-PythonRdf.ps1`
- 创建：`migration/scripts/Test-RdfParity.ps1`
- 测试：`src/OnToPilot.IntegrationTests/Migration/RdfMigrationTests.cs`

**接口：**

- 输出：`verify-copy`、`import-nquads`、`write-revert-smoke` 命令与 graph/query hash manifest。

- [ ] **步骤 1：写源目录保护失败测试**

```csharp
[Fact]
public async Task Verify_copy_never_opens_or_changes_source_directory()
{
    var before = DirectoryHash.Compute(SourceStore);
    await Command.VerifyCopyAsync(SourceStore, ProbeCopy, Queries, CancellationToken.None);
    Assert.Equal(before, DirectoryHash.Compute(SourceStore));
    Assert.True(Report.SourceOpenedByDotNet is false);
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~RdfMigration`
预期：失败，迁移命令不存在。

- [ ] **步骤 3：实现双路径策略**

```csharp
public sealed record RdfMigrationReport(
    string Strategy,
    ulong QuadCount,
    IReadOnlyList<string> NamedGraphs,
    IReadOnlyDictionary<string, string> QueryResultHashes,
    bool WriteRevertPassed);
```

- [ ] **步骤 4：验证直读副本和逻辑迁移**

运行：`dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~RdfMigration; pwsh migration/scripts/Test-RdfParity.ps1 -Source backend/data/oxigraph -Work .artifacts/rdf-rehearsal`
预期：直读支持时计数/图/查询一致；强制 fallback 时 N-Quads 导入产生相同 manifest；写入后 revert 恢复原 hash。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot.Migration/Rdf migration/fixtures/rdf-smoke-queries.json migration/scripts src/OnToPilot.IntegrationTests/Migration/RdfMigrationTests.cs
git commit -m "feat: add rdf compatibility and fallback migration"
```

### 任务 3：实现 blob 到 MinIO 的 manifest 迁移

**文件：**

- 创建：`src/OnToPilot.Migration/Blobs/BlobMigrationCommand.cs`
- 创建：`migration/scripts/Invoke-BlobMigration.ps1`
- 创建：`migration/manifests/blob-manifest.schema.json`
- 测试：`src/OnToPilot.IntegrationTests/Migration/BlobMigrationTests.cs`

**接口：**

- 输出：从 `aa/bb/hash` 到 MinIO `hash` 的迁移；manifest 记录来源路径、object key、size、SHA-256、引用文档数。

- [ ] **步骤 1：写完整性与去重失败测试**

```csharp
[Fact]
public async Task Duplicate_document_references_upload_one_object_and_keep_two_rows()
{
    await SeedTwoDocumentsSharingBlobAsync();
    var report = await Migration.RunAsync(Source, Bucket, CancellationToken.None);
    Assert.Single(report.Objects);
    Assert.Equal(2, report.Objects[0].ReferenceCount);
    Assert.Equal(2, await Db.Documents.CountAsync());
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~BlobMigration`
预期：失败，迁移命令不存在。

- [ ] **步骤 3：实现流式迁移和 SHA-256 校验**

```csharp
public sealed record BlobManifestEntry(
    string SourcePath, string ObjectKey, long Size, string Sha256, int ReferenceCount);
```

- [ ] **步骤 4：验证 dry-run、resume 与损坏检测**

运行：`dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~BlobMigration; pwsh migration/scripts/Invoke-BlobMigration.ps1 -Source backend/data/blobs -DryRun`
预期：dry-run 不写对象；中断后 resume 跳过已验证对象；hash 不一致立即失败；无引用 release 文件不迁移。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot.Migration/Blobs migration/scripts/Invoke-BlobMigration.ps1 migration/manifests src/OnToPilot.IntegrationTests/Migration/BlobMigrationTests.cs
git commit -m "feat: add verified minio blob migration"
```

### 任务 4：编排演练、生产切换和回滚

**文件：**

- 创建：`migration/scripts/Invoke-MigrationRehearsal.ps1`
- 创建：`migration/scripts/Invoke-ProductionCutover.ps1`
- 创建：`migration/scripts/Invoke-ProductionRollback.ps1`
- 创建：`migration/scripts/Complete-Observation.ps1`
- 创建：`migration/runbooks/production-cutover.md`
- 创建：`migration/runbooks/production-rollback.md`
- 创建：`migration/runbooks/production-cutover-record.template.md`
- 测试：`migration/tests/CutoverScripts.Tests.ps1`

**接口：**

- 输出：机器强制的 preflight、停写、备份、迁移、smoke、24 小时观察和回滚流程。

- [ ] **步骤 1：写“未停 Python 则拒绝切换”的失败测试**

```powershell
It 'refuses cutover while python backend is running' {
    Mock Test-PythonBackendStopped { $false }
    { & $CutoverScript -Record $ValidRecord } | Should -Throw '*Python backend must be stopped*'
    Assert-MockCalled Invoke-SqlMigration -Times 0
}
```

- [ ] **步骤 2：运行 Pester 并确认失败**

运行：`Invoke-Pester migration/tests/CutoverScripts.Tests.ps1`
预期：失败，脚本不存在。

- [ ] **步骤 3：实现硬门禁顺序**

```powershell
Assert-PythonBackendStopped
Assert-DatabaseWriteFreeze
Assert-VerifiedBackup -Record $Record
Invoke-RdfCopyVerification
Invoke-BlobMigration
Invoke-SqlMigration
Assert-AllMigrationManifests
Start-DotNetBackend
Invoke-PostCutoverSmoke
```

- [ ] **步骤 4：完成全量演练和回滚演练**

运行：`Invoke-Pester migration/tests/CutoverScripts.Tests.ps1; pwsh migration/scripts/Invoke-MigrationRehearsal.ps1 -BackupPath .artifacts/production-backup -ReportPath .artifacts/migration-report.json`
预期：所有脚本测试通过；演练报告含 SQL/RDF/blob manifest；回滚后 Python 登录、知识系统读取和 RDF 查询 smoke 通过。

- [ ] **步骤 5：人工审阅生产记录后执行切换**

运行：`pwsh migration/scripts/Invoke-ProductionCutover.ps1 -Record migration/runbooks/production-cutover-record.md`
预期：仅在记录完整且所有校验和匹配时继续；启动 24 小时观察期。

- [ ] **步骤 6：结束观察或执行回滚**

成功运行：`pwsh migration/scripts/Complete-Observation.ps1 -Record migration/runbooks/production-cutover-record.md`

回滚运行：`pwsh migration/scripts/Invoke-ProductionRollback.ps1 -Record migration/runbooks/production-cutover-record.md`

预期：成功路径将备份标记为第 30 天后方可删除；回滚路径停止 .NET、恢复 SQL 权限/备份引用并只让 Python 打开原 RDF 目录。

- [ ] **步骤 7：提交演练证据，不提交生产秘密**

```bash
git add migration/scripts migration/tests migration/runbooks migration/manifests
git commit -m "ops: add rehearsed dotnet cutover and rollback"
```
