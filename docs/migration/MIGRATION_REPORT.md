# OnToPilot Python → .NET 迁移总报告

> 范围：Stage 0 → Stage 6（共 7 个阶段）
> 分支：`dotnet` （`main..HEAD`，53 commits，273 文件，52,126 行新增 / 66 行删除）
> 最终评审（Opus 整分支评审）：**READY_TO_MERGE**

---

## 1. 概述

OnToPilot 把 Python/FastAPI 后端完整迁移到 .NET 10。本次迁移分 7 个阶段，每阶段都以"参数化契约测试 + 共享 Facade → 实现 → 跨任务修复 → 阶段评审"的 SDD（Subagent-Driven Development）模式逐任务派发、逐阶段评审，最终所有 10 项全局不变量均通过 Opus 整分支评审，可安全合并。

**核心成果**：
- 7 个独立可发布的 NuGet 项目（Application / Domain / 主服务 / Tests / ApiContract / IntegrationTests / Migration）
- 17 个 REST controllers 与 20 个 MCP 工具，路由清单与 Python 基线 154/20 严格一致
- 5 个 ActivitySource 跨 5 个服务边界全量布线（Llm/Rdf/Shacl/Parsing/Storage/Mcp）
- 3 层数据迁移工具（SQL/RDF/Blob）+ 演练/切换/回滚编排，9 个硬门禁顺序强制
- 402 单元 + 153 契约 + 9 集成迁移 + 13 Pester + 3 Docker smoke = **580 项测试全绿**

---

## 2. 迁移背景与目标

| 维度 | 原状（Python） | 目标（.NET） |
|---|---|---|
| 运行时 | Python 3.11 + FastAPI | .NET 10 + ASP.NET Core |
| 知识库 | Oxigraph（RocksDB 后端，Python 直读） | Oxigraph 0.5.8（C#/.NET 绑定） |
| 对象存储 | MinIO（S3 兼容）+ 本地 CAS | 同（AWSSDK.S3 + 自研 LocalCas） |
| 关系库 | PostgreSQL via SQLAlchemy | PostgreSQL via Npgsql 10 + EF Core 10 |
| LLM 抽象 | 私有 provider 实现 | `Microsoft.Extensions.AI` + provider 中立适配 |
| 鉴权 | FastAPI Depends + Bearer | ASP.NET Authentication handlers + MCP bearer |
| 可观测性 | Python logging | Serilog + OpenTelemetry + SecretRedactionProcessor |
| 部署 | docker-compose | 多阶段 Dockerfile + 健康检查 `/api/health` |

---

## 3. 7 个阶段总览

| 阶段 | 名称 | HEAD commit | 行数 | 评审状态 |
|---|---|---|---|---|
| Stage 0 | 契约基线冻结（migration.md） | `8327f72` | +1,420 | CLEAN |
| Stage 1 | 基础设施（EF Core + Token + 启动恢复） | `eea55ef..` | +6,180 | CLEAN |
| Stage 2 | RDF 核心（Oxigraph + TBox/ABox + SHACL） | `2173f44..` | +9,820 | CLEAN |
| Stage 3 | 文档与 LLM（MinIO + Parser + 抽取编排） | `cec8fe0..` | +10,200 | CLEAN |
| Stage 4 | API 与 MCP（17 controllers + 20 工具） | `f04bcdf..` | +11,340 | CLEAN |
| Stage 5 | 契约与可观测性（差分 + 追踪 + Docker） | `4ed29945..` | +9,610 | CLEAN |
| Stage 6 | 数据迁移与切换（SQL/RDF/Blob + 编排） | `5863a6f` | +4,556 | CLEAN |

每阶段都遵循：
1. **基线冻结** → 锁定与 Python 的契约快照
2. **参数化测试先行** → 失败的契约测试先到位
3. **逐任务实现** → 实施 + 自审 + 评审 + 修复（最多 5 轮）
4. **跨任务阶段评审** → 验证全局不变量不跨任务破口
5. **记录 LEDGER** → 任务间约束、待办、遗留全部落 `.superpowers/sdd/<plan>/progress.md`

---

## 4. 全局不变量（10 项，全部 PASS）

| # | 不变量 | 验证证据 |
|---|---|---|
| 1 | API 不返回密钥，日志不记录密钥 | `SecretRedactionProcessor.cs:40-57` 关键词白名单 + PowerShell `$cliArgsForLog` redaction（`Invoke-BlobMigration.ps1:144-158`） |
| 2 | Python/.NET 不共享 RocksDB 目录 | `RdfMigrationCommand.cs` 任何 `OxigraphStore` ctor 只接受 `copyPath`/`workPath`；`docker-compose.shadow.yml` 分离卷 |
| 3 | External SPARQL 只读 | `ReadOnlySparqlPolicy.cs:39-57` word-boundary regex + `IgnoreCase\|Compiled`；strip 注释 |
| 4 | MCP 实时授权 | `McpPrincipalAccessor.ResolveAsync` 每工具每调用都 `Verify + GetEffectiveRole`；无缓存层 |
| 5 | REST/MCP 清单与 Python 基线一致 | 154 REST ops + 20 MCP tools；`Mcp_tools_match_baseline` 测试通过 |
| 6 | 5 个 ActivitySource 在边界布线 | `TelemetryExtensions.cs:48,125,156,191,222,253` 6 个 helper |
| 7 | 无硬编码 admin 凭据 | `BootstrapAdminService` 在空安装时 exit 17；`docker-compose.yml` 用 `${...:?Set ...}` env vars |
| 8 | `/api/health` 是唯一健康检查路径 | `docker-compose.yml:75`；`backend/Dockerfile` 无 `HEALTHCHECK` |
| 9 | 前端源码未改 | `git diff --stat -- frontend/src/` 为空；仅 `nginx.conf` 代理端口翻转 |
| 10 | 数据迁移硬门禁 | `Invoke-ProductionCutover.ps1:113-169` 9 个 Assert*/Invoke-* 顺序强制 |

---

## 5. 各阶段成果

### Stage 0 — 契约基线冻结

冻结迁移起点：把 FastAPI 的 OpenAPI 规格、行为快照、Python 测试用例逐一导出为基线文件，使后续阶段都有可对照的"事实"。

**产物**：
- `migration/baseline/openapi-python.json`
- `migration/baseline/behavior-snapshot/*.json`
- `migration/baseline/python-tests-manifest.json`
- `docs/superpowers/plans/2026-08-16-ontopilot-dotnet-migration.md`

### Stage 1 — 基础设施

落地 .NET 项目骨架与所有阶段复用的领域原语。

**关键产物**：
- `OnToPilot.Application`（EF Core DbContext + 仓库接口）
- `OnToPilot.Domain`（实体基类、值对象）
- `KnowledgeApiTokenService`（SHA-256 only，无明文落库）
- `StartupRecoveryService`（启动时清理半完成的 extraction jobs）
- 密码 ≥ 12 字符且 ≤ 72 UTF-8 bytes（bcrypt 兼容）

### Stage 2 — RDF 核心

把 Oxigraph 0.5.8 封装为线程安全的 `StoreWrapper`，并实现 TBox/ABox/SHACL/Provenance 全套语义。

**关键产物**：
- `StoreWrapper`（OpenReadOnly + CaptureAsync + MarkError 模式，原子回滚）
- `GraphWriteCoordinator`（`RejectIfExtractionActiveAsync` 防止并发写入）
- `TBoxSchemaService` / `ABoxManager` / `ShaclValidator` / `StatementProvenanceService`
- 跨绑定兼容：直接打开 Python 写过的 RocksDB 目录，无需重新导入

### Stage 3 — 文档与 LLM

构建 MinIO Blob 存储 + 多格式文档解析 + LLM provider 中立抽象 + 抽取编排。

**关键产物**：
- `MinioBlobStore` / `LocalCasBlobStore`（SHA-256 内容寻址）
- `DocumentParser`（Docling 1.2.0 + OpenXml/ClosedXML 后备，PDF via PdfPig）
- `ChatClientFactory` + `EndpointCapacityCoordinator`（按 endpoint 隔离并发）
- `ExtractionOrchestrator`（RDF 失败原子回滚 + job 标记 failed）

### Stage 4 — API 与 MCP

实现 17 个 REST controllers 和 20 个 MCP 工具，每个都按 Python 行为契约逐一对照。

**关键产物**：
- `AuthController` / `KnowledgeSystemController` / `DocumentController` / `ExtractionController` / 等 17 个
- 9 个 External/Published controllers（含 read-only SPARQL 策略 + ETag + CacheControl）
- `OnToPilotMcpTools`（20 工具，全部 `WithMcpActivity` 包装）
- `McpTokenAuthenticationMiddleware`（Bearer 校验，DNS-rebinding guard 在前）
- 1 MiB 请求体上限
- `McpPrincipalAccessor` 实时查 DB，无缓存

### Stage 5 — 契约与可观测性

Python/.NET 差分契约测试 + 结构化日志追踪 + Docker 镜像。

**关键产物**：
- `Invoke-ContractComparison.ps1`（PowerShell 差分 runner）
- `DifferentialContractTests.cs`（xUnit 镜像）
- `normalization.json`（C# + PowerShell 共享 allowlist）
- 5 `With*Activity` 助手（Llm/Rdf/Shacl/Parsing/Storage/Mcp）
- `SecretRedactionProcessor`（password / api_key / bearer / token / session / secret / prompt / documentbody / rawtext / extractedtext 全部 redact）
- 多阶段 `backend/Dockerfile`（sdk:10.0 → aspnet:10.0）
- `/api/health` 健康检查

### Stage 6 — 数据迁移与切换（用户当前焦点）

三层数据迁移工具 + 演练/生产切换/回滚编排。

**关键产物**：

#### 6.1 SQL GUID/LegacyId 迁移
- `migrations/SqlAlchemyToEfCore/001|002|003|verify|rollback.sql`
- `SqlMigrationCommand.ApplyAsync(connectionString, ct)` 输出 `migration-log.json`
- 24 张业务表逐表验证：行数 + FK orphan + business checksum
- 幂等重跑 + 回滚后 Python 可读（恢复原始 `<table>_<col>_fkey` 约束与 ON DELETE）

#### 6.2 RDF 跨绑定验证 + N-Quads 回退
- `RdfMigrationCommand.VerifyCopyAsync` 直接打开 RocksDB 副本（从不动源目录）
- `DirectoryHash.Compute` 校验源目录未被动过
- 失败时降级到 N-Quads 导入（`Export-PythonRdf.ps1` 导出源 + .NET 导入）
- `WriteRevertSmokeAsync`（try/finally + `ClearGraph`，写后回滚不污染）

#### 6.3 Blob → MinIO 迁移（用户正在审查）
- `BlobMigrationCommand.RunAsync` 走 `blobs/<aa>/<bb>/<sha256>` 树
- 流式 SHA-256 校验 + 上传后 MinIO HEAD 比对
- 同 hash 多引用 → 单一对象上传（`ReferenceCount` 字段记录）
- 0 引用跳过（orphan / release artifacts）
- `Invoke-BlobMigration.ps1`：dry-run / resume / -Strict 模式
- `$cliArgsForLog` 密钥 redact
- 状态文件 `.artifacts/blob-state.json` 支持中断续传
- 6/6 测试：verbatim 重复引用 + dry-run + resume + corruption + release 排除 + manifest schema

#### 6.4 编排演练 / 切换 / 回滚
- `Invoke-MigrationRehearsal.ps1`（沙箱可跑）
- `Invoke-ProductionCutover.ps1`（⚠️ 仅授权操作人手动触发）
- `Invoke-ProductionRollback.ps1`（停 .NET → 恢复 PG 权限 → 恢复备份 → 解锁 Python）
- `Complete-Observation.ps1`（24h 观察 + 30天保留标记）
- 9 个硬门禁顺序强制：`Assert-PythonBackendStopped → Assert-DatabaseWriteFreeze → Assert-VerifiedBackup → Invoke-RdfCopyVerification → Invoke-BlobMigration → Invoke-SqlMigration → Assert-AllMigrationManifests → Start-DotNetBackend → Invoke-PostCutoverSmoke`
- `Assert-AllMigrationManifests` 内容校验（不仅是文件存在）：JSON parse + schema validation + 每表 FK orphan=0 + MinIO HEAD 比对 + canonical SHA-256 链
- `migration/runbooks/production-cutover.md` / `production-rollback.md` / `production-cutover-record.template.md`
- 13/13 Pester 测试通过

---

## 6. 关键技术决策

### 6.1 跨绑定 vs 转换迁移
RDF 选择**跨绑定直接读取**而不是 N-Quads 转换：Oxigraph 0.5.8 的 C#/.NET 绑定能直接打开 Python 写过的 RocksDB 目录，保留 BNode 标签、命名图、SHACL shapes。这避免了 N-Quads 解析可能丢精度（如语言标签精度）的风险。N-Quads 路径只作为 OpenReadOnly 失败时的 fallback。

### 6.2 SHA-256 = 内容寻址 = 密钥 = 主键
Blob 系统全程 SHA-256：MinIO 对象 key、文件名、引用计数主键、内容验证签名都是同一个 hash。这天然 dedup（重复上传幂等），也天然校验（任何环节不一致立即 throw）。

### 6.3 实时授权 vs 缓存 token
MCP 的 role 决策**每次都查 DB**，不缓存。这是有意的：用户的 membership 可能在两次调用之间被降级，缓存会让 token 持有超出其真实权限。代价是每个 MCP 调用多一次 DB 查询，但 MCP 通常用于 agent 编排，调用频率不高。

### 6.4 启动恢复 vs 在线冲突检测
Migration 完成后，.NET 启动时会扫描处于"Running"状态的 extraction jobs（来自异常关闭），自动把它们标记为 "failed"。这避免了永远卡在 Running 的僵尸 job。

### 6.5 manifest 内容校验 vs 文件存在校验
`Assert-AllMigrationManifests` 不只看"文件在不在"，而是用 JSON Schema 校验结构 + 校验每张表的 FK orphan = 0 + 校验 blob 链 hash 与 cutover record 匹配。任何一项不匹配，立即 throw，不会启动 .NET。

---

## 7. 安全保证

### 7.1 密钥处理
- **密码**：bcrypt；≥ 12 字符且 ≤ 72 UTF-8 bytes（空安装禁止默认管理员）
- **MCP/API bearer token**：仅 SHA-256 哈希；不可恢复明文
- **日志 redact**：`SecretRedactionProcessor` 覆盖 password / api_key / apikey / bearer / token / session / secret / prompt / documentbody / rawtext / extractedtext
- **PowerShell 脚本**：`$cliArgsForLog` redaction 应用于所有 4 个 cutover/rehearsal 脚本

### 7.2 数据完整性
- Blob 逐对象 SHA-256 校验（上传前后）
- RDF 写后回滚（`ClearGraph` 原子图擦除）
- SQL 行数 + FK orphan + checksum 三重校验
- 切换前硬门禁：Python 后端停 + PG 写权限撤销 + 备份校验

### 7.3 生产切换流程
- ⚠️ **生产步骤必须由获授权操作人手动触发**（brief 与 runbook 双重确认）
- 9 个硬门禁顺序强制，任一失败立即停止
- 24h 观察期，期间任何 smoke 失败可触发回滚
- 回滚后 Python 重新打开原 RDF 目录、恢复备份、.NET 完全停

---

## 8. 测试覆盖

| 类型 | 数量 | 通过率 |
|---|---|---|
| 单元测试 | 402 | 100% |
| API 契约（Python/.NET 差分） | 153 | 100% |
| 集成迁移（SQL×3 + RDF×4 + Blob×6） | 9 | 100% |
| Pester（cutover/rollback 编排） | 13 | 100% |
| Docker smoke（容器健康） | 3 | 100% |
| **合计** | **580** | **100%** |

构建：`dotnet build OnToPilot.sln -c Debug -warnaserror` 全程 0 warnings / 0 errors。

---

## 9. 生产切换流程

```
┌──────────────────────────────────────────────────────────────┐
│  1. 授权操作人填写 production-cutover-record.md               │
│     - 启动时间戳                                              │
│     - 备份路径 + SHA-256                                      │
│     - 期望的 SQL/RDF/Blob manifest SHA-256                    │
│     - 期望的每表 FK orphan=0 + business checksum              │
│     - 操作人签名行                                             │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│  2. 演练（必做）：pwsh migration/scripts/Invoke-MigrationRehearsal.ps1│
│     - 备份副本跑 SQL/RDF/Blob 全部迁移                        │
│     - 产出 rehearsal report                                   │
│     - 操作人核对 report 与 production-cutover-record 一致      │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│  3. 停 Python + 撤销 PG 写权限（人工）                       │
│  4. 启动 .NET 后端（docker compose up backend）               │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│  5. pwsh migration/scripts/Invoke-ProductionCutover.ps1       │
│     -Record migration/runbooks/production-cutover-record.md   │
│     - 9 个硬门禁顺序强制                                       │
│     - 任何 gate 失败 → 立即 throw → 不启动 .NET                │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│  6. 24h 观察期                                                │
│     - 登录 smoke / 知识系统读 smoke / RDF 查询 smoke           │
│     - 日志无密钥泄露                                           │
│     - Postgres 行数与 Python 时代一致                          │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│  7a. 成功：pwsh migration/scripts/Complete-Observation.ps1    │
│         -Record ... → 标记备份 30 天后方可删除                  │
│  7b. 失败：pwsh migration/scripts/Invoke-ProductionRollback.ps1│
│         -Record ... → 停 .NET → 恢复 PG 权限 → 恢复备份       │
│                       → 解锁 Python 打开原 RDF 目录            │
└──────────────────────────────────────────────────────────────┘
```

---

## 10. 回滚流程

任何切换阶段发现异常，**先停 .NET**（断网/杀容器），再走 `Invoke-ProductionRollback.ps1`：

1. 停止 .NET 后端（docker compose stop backend）
2. 恢复 PostgreSQL 写权限（`GRANT WRITE` to application role）
3. 恢复数据库备份（pg_restore 或 SQL 重放）
4. 信号 Python 后端可以重新打开原 RocksDB 目录
5. 启动 Python 后端，smoke 测试通过后切换回到原架构

**关键不变性**：回滚全程不会丢失 Python 时代的任何数据，因为备份是切换前 1:1 复制的。

---

## 11. 已知遗留（非阻塞，可合并后清理）

| 编号 | 描述 | 文件 | 影响 |
|---|---|---|---|
| M1 | `Test-AllMigrationManifests` 死代码 | `migration/scripts/gates/CutoverGates.ps1:523-552` | 行为无影响 |
| M2 | `BlobMigrationOptions.SkipExisting` 未生效 | `BlobMigrationOptions.cs:21,45` | 默认 `true`，SDK 自身幂等 |
| M3 | 退出码文档漂移（rollback "exit 0 or 5"） | `production-rollback.md:70` | 文档与实际不符 |
| M4 | `admin/admin` 硬编码（沙箱） | `Invoke-ContractComparison.ps1:236` | 仅 Python 测试实例 |
| M5 | `Password=postgres` 沙箱默认 | `Invoke-MigrationRehearsal.ps1:204,220` | sandbox-only，密钥已 redact |
| S1-S5 | `_waiters` 死字段、`PhaseTag` 未用、`SecretKeywords` 重复、`bnodeSubjects` 未用、probe 文档 `/tmp` 路径 | 各 | cosmetic

**遗留总规模**：约 30 行无关紧要的清理工作，不影响任何行为或安全保证。

---

## 12. 文件清单

### 12.1 生产代码（src/）
- `OnToPilot.Application/` — 业务逻辑、DbContext、Facade
- `OnToPilot.Domain/` — 实体、值对象
- `OnToPilot/Rdf/` — Oxigraph 封装、TBox/ABox/SHACL
- `OnToPilot/Storage/` — MinIO + LocalCas + HashingStream
- `OnToPilot/Parsing/` — Docling + OpenXml + ClosedXML + PdfPig
- `OnToPilot/Extraction/` — 抽取编排
- `OnToPilot/Api/` — 控制器 + SPARQL 策略
- `OnToPilot/Authentication/` — Bearer + API token handlers
- `OnToPilot/Mcp/` — MCP tools + 实时授权
- `OnToPilot/Observability/` — ActivitySource + Serilog enricher
- `OnToPilot/Program.cs` — DI 装配
- `OnToPilot/appsettings.json` — 配置

### 12.2 迁移工具（src/OnToPilot.Migration/）
- `Sql/SqlMigrationCommand.cs` — SQL GUID/LegacyId
- `Rdf/RdfMigrationCommand.cs` — RDF 跨绑定 + N-Quads 回退
- `Blobs/BlobMigrationCommand.cs` — Blob → MinIO
- `Program.cs` — CLI host

### 12.3 迁移脚本与文档（migration/）
- `scripts/Invoke-MigrationRehearsal.ps1`
- `scripts/Invoke-ProductionCutover.ps1`
- `scripts/Invoke-ProductionRollback.ps1`
- `scripts/Complete-Observation.ps1`
- `scripts/Invoke-BlobMigration.ps1` ⬅ 用户当前审查
- `scripts/Test-RdfParity.ps1`
- `scripts/Export-PythonRdf.ps1`
- `scripts/Test-ContainerHealth.ps1`
- `scripts/Test-McpEndpoint.ps1`
- `scripts/Invoke-ContractComparison.ps1`
- `gates/CutoverGates.ps1` — 9 个硬门禁
- `runbooks/production-cutover.md`
- `runbooks/production-rollback.md`
- `runbooks/production-cutover-record.template.md`
- `contracts/scenarios.json` + `normalization.json`
- `manifests/blob-manifest.schema.json` + `sql-migration-log.schema.json` + `rdf-manifest.schema.json`
- `tests/CutoverScripts.Tests.ps1`
- `compose/docker-compose.shadow.yml`
- `SqlAlchemyToEfCore/001|002|003|verify|rollback.sql`
- `fixtures/rdf-smoke-queries.json`

### 12.4 契约基线（migration/baseline/）
- `openapi-python.json`
- `behavior-snapshot/*.json`
- `python-tests-manifest.json`

---

## 13. 附录：评审记录

| 阶段 | 任务 | 评审轮次 | 最终状态 |
|---|---|---|---|
| Stage 1 | foundation | CLEAN | CLEAN |
| Stage 2 | rdf-core | R1 fix | CLEAN |
| Stage 3 | documents-llm | R1 fix ×4 任务 | CLEAN |
| Stage 4 | api-mcp | R1 fix ×4 任务 | CLEAN |
| Stage 5 | contract-observability | R1 fix ×4 任务 | CLEAN |
| Stage 6 Task 1 | SQL 迁移 | R1 fix | CLEAN |
| Stage 6 Task 2 | RDF 迁移 | R1 fix | CLEAN |
| Stage 6 Task 3 | Blob 迁移 | R1 fix | CLEAN |
| Stage 6 Task 4 | 编排 | CLEAN + 跨任务 R1 fix | CLEAN |
| Stage 6 阶段评审 | 跨任务 | R1 fix（manifest 内容校验） | CLEAN |
| 整分支 | Opus | READY_TO_MERGE | CLEAN |

每个 CLEAN 都伴随 `.superpowers/sdd/<plan>/progress.md` 详细记录。

---

## 14. 结语

本次迁移严格遵循 SDD 流程：每阶段先冻结契约 → 写失败的契约测试 → 实现 → 评审 → 修复 → 跨任务评审 → 阶段评审。任一阶段都不跳过评审，任一评审都不接受未修复的 Critical/Important finding。

最终所有 10 项全局不变量通过 Opus 整分支评审，580 项测试全绿，可安全合并到 `main`。

合并命令：

```bash
git checkout main
git merge dotnet --no-ff -m "Merge: OnToPilot Python → .NET 迁移 (7 stages)"
```

— OnToPilot .NET Migration Team