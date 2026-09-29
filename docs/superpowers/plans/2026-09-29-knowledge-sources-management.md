# 知识摄入 Source 管理与密钥 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在基础阶段之上提供可授权的 Source CRUD、可封存的连接器配置，以及可轮换、受限查看的 Source API token。

**Architecture:** Source 管理保持在现有 KS 作用域内；控制器用 `KSRoleAuthorize`，服务再次核验 KS 与操作者身份。管理 DTO 只投影非敏感配置；凭据和 token 在服务端封存，普通响应、审计与日志均不输出明文。本阶段先建立可注入的 `SourceAdapterRegistry`，仅注册不需 adapter 的 `folder` 元数据；后续同步阶段定义 adapter 接口，连接器阶段在同一注册表加入已就绪的主动 adapter 和被动 push handler。

**Tech Stack:** .NET 10、ASP.NET Core、EF Core/Npgsql、PostgreSQL 16、xUnit、AES-256-GCM。

## Global Constraints

- 依赖 [基础阶段计划](2026-09-29-knowledge-sources-foundation.md) 的 `SourceEntity`、`DocumentEntity.SourceId` 与默认 folder 来源；执行前先核对 [设计稿](../specs/2026-09-29-knowledge-ingestion-sources-design.md)。
- Source 与虚拟 Folder 独立；删除 Source 使用 `ON DELETE SET NULL`，文档、Blob、图谱及溯源保留；同步阶段创建的来源绑定随 Source 级联删除。
- Viewer 只能读取脱敏配置；Editor/Owner 可写 Source、触发同步、查看或轮换 token；无 KS 权限者不得访问。
- 生产 AES-256-GCM 密钥由部署环境提供；缺少密钥时普通 folder Source/文档操作仍可用，但封存配置、token 查看/轮换及需要凭据的连接器均失败关闭；配置了非空但格式错误或不是 32 字节的密钥时启动失败，绝不回退为明文。
- 后续同步与连接器还未实现时，API 不得将未注册的 kind 标记为可创建或可同步；`azure_blob` 本轮仅保留已知 kind 标识，不注册 adapter，不进入可创建列表。

## 文件职责与边界

- Create `src/ISEStudio/Sources/SourceKind.cs`、`SourceAdapterRegistry.cs`：唯一的 kind 定义及可注入的注册表；`memory`/`upload` 仅兼容读取，不对外创建；注册表给 API/UI 返回当前真正可用的 kind，本阶段仅含 `folder`。
- Create `src/ISEStudio/Sources/SourceDtos.cs`、`SourceService.cs`、`SourceServiceCollectionExtensions.cs`：公开契约、KS 范围 CRUD、脱敏与审计。
- Create `src/ISEStudio/Sources/SourceSecretProtector.cs`：AES-GCM 封存/解封与密钥配置验证；不得把明文放到可序列化 DTO 或日志。
- Create `src/ISEStudio/Controllers/IngestionSourcesController.cs`：`/api/knowledge/{id}/ingestion-sources`、`/{sourceId}`、`/{sourceId}/token`；采用既有 auth/role 属性，不覆盖现有 `OntologyController.SourcesAsync` 的图谱来源路由。
- Modify `src/ISEStudio/Infrastructure/Persistence/Entities/SourceEntities.cs`：如需存储 token，仅保留密文和轮换时间，不用可逆明文列。
- Modify `src/ISEStudio/Program.cs`：注册服务、启动时验证已配置密钥的格式与长度；更新 `src/ISEStudio/appsettings.Development.json` 的非机密开发配置说明，密钥本身只由环境提供。
- Test `src/ISEStudio.Tests/Sources/SourceApiTests.cs`、`SourceSecretProtectorTests.cs`：角色矩阵、DTO/日志不泄密、轮换旧 token 失效、删除保留文档。

---

### Task 1: Source 管理、角色和脱敏投影

**Files:** Create `src/ISEStudio/Sources/SourceKind.cs`, `SourceAdapterRegistry.cs`, `SourceDtos.cs`, `SourceService.cs`, `SourceServiceCollectionExtensions.cs`, `src/ISEStudio/Controllers/IngestionSourcesController.cs`, `src/ISEStudio.Tests/Sources/SourceApiTests.cs`; Modify `src/ISEStudio/Program.cs`.

**Interfaces:** `SourceService.ListAsync(Guid ksId, Actor actor, CancellationToken ct)`, `GetAsync(Guid ksId, Guid sourceId, Actor actor, CancellationToken ct)`, `CreateAsync(Guid ksId, SourceUpsertRequest request, Actor actor, CancellationToken ct)`, `UpdateAsync(Guid ksId, Guid sourceId, SourceUpsertRequest request, Actor actor, CancellationToken ct)`, `DeleteAsync(Guid ksId, Guid sourceId, Actor actor, CancellationToken ct)`；列表/写入响应 `SourceOut` 不含 token 和 secret 键，`GET /api/knowledge/{id}/ingestion-sources/{sourceId}` 返回相同基础字段及 `config`，其中仅按 kind 白名单投影非敏感值，secret 字段完全省略（不返回密文、明文或掩码）。`GET /api/knowledge/{id}/ingestion-sources/kinds` 返回已注册可创建 kind 的描述对象 `{kind, active_sync, config_fields:[{name,type,required,secret}]}`；此阶段只返回 `folder` 且 `config_fields=[]`、详情 `config={}`。字段描述用于呈现与校验，详情中对应的非敏感值用于编辑预填。

- [ ] **Step 1: 写失败的 HTTP 授权测试。** 沿用 `DocumentApiTests` 的 `AuthTestWebApplicationFactory` 建 KS 和 viewer/editor/owner/admin 客户端。断言 viewer `GET /api/knowledge/{ksId}/ingestion-sources` 返回默认 folder、`GET /{sourceId}` 返回 `config={}`、`POST` 被拒；editor `POST` 创建第二个 folder 并 `PATCH` 改名成功；跨 KS `GET/PATCH/DELETE` 不可见且不可修改；`GET /ingestion-sources/kinds` 仅含 `folder`；现有 `GET /api/knowledge/{ksId}/sources` 仍返回图谱来源；普通响应 JSON 不含 `IngestTokenCiphertext`、`ingest_token` 或敏感 config 字段。删除第二个 folder 后检查关联文档仍在但 `SourceId == null`，Folder 不变。命令：`dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~SourceApiTests`，预期先因控制器不存在而失败。
- [ ] **Step 2: 实现共享 kind 与可创建列表。** `SourceKind` 用字符串常量定义 spec 中 `folder/url/rss/custom/github_issues/jira_issues/s3/azure_blob/gcs/webdav/notion/api/statements/memory/upload`；此阶段创建并注入 `SourceAdapterRegistry`，仅注册被动容器 `folder`（`active_sync=false`、`config_fields=[]`），由其元数据驱动创建校验、详情投影与 `/kinds`，不依赖尚未定义的 `ISourceAdapter` 或预注册未实现的 kind。连接器阶段在该注册表扩展已就绪的主动 adapter/被动 push handler，只有注册成功的 kind 进入 `CreatableKinds`。校验未知 kind、空名、同一 KS 重名（如已有约束允许则按约束返回 409）、`sync_interval_minutes <= 0`、同时填写 interval 和 cron 均返回 400；`folder` 不允许定时计划。列表 DTO 显式列举 `id,kind,name,icon,sync_interval_minutes,sync_cron,last_synced_at,last_sync_status,last_sync_error,last_sync_added,created_at,document_count,missing_document_count`，RSS 补全摘要只在 RSS 可用后填充；详情在这些字段之外仅投影该 kind 已注册 schema 中 `secret=false` 且通过值级校验的 `config` 值。未知配置键及所有 `secret=true` 字段均不输出，不得直接序列化 `SourceEntity.Config`。
- [ ] **Step 3: 接入服务、审计与角色。** 控制器类标注 `[Authorize]`，读操作标注 `[KSRoleAuthorize(Minimum = KSRole.Viewer)]`，写操作标注 Editor；路由参数必须同时验证 `source.KnowledgeSystemId == ksId`。服务调用现有 `KnowledgeSystemAccessService` 做二次权限检查，沿用 `DocumentService.WriteAuditAsync` 的审计实体字段规范记录 `source.create/update/delete`，详情只记 sourceId、kind 和变更字段名。删除使用 DB 事务；先锁定该 Source 行，再按 ID 锁定并重读受影响 Document，避免与同样先锁 Source 的上传、同步及 push 路径形成反向等待；同步阶段上线后，若有 queued/running job 则返回 409 且不删除，否则查询该 Source 绑定涉及的文档（不得先持绑定行锁再等 Document 锁），删除 Source（绑定与历史级联）并清空指向它的文档主归属 `SourceId`/`ExternalKey`，在同一事务中按剩余全部绑定重算受影响文档的 `MissingSince`；无绑定的文档清空缺失标记。同步阶段的 `SourceSyncTests` 验证删除共享 Source、其余绑定已缺失及全部绑定被删时的文档缺失状态；本阶段没有绑定表，只验证无绑定的 folder 删除。同 Source 的入队/认领也须先锁同一 Source 行并复核其存在，锁顺序固定 Source 后 job，避免检查与删除之间的竞态。SQLite 测试使用等价事务/条件写判定，不能只先查询再删除。验证默认 folder 被删除后上传仍能选择剩余最早的 folder；若无 folder，按基础阶段上传逻辑创建新默认项。
- [ ] **Step 4: 运行绿灯和合同测试。** 重跑 Step 1 命令，期望角色矩阵、跨 KS、脱敏、删除保留文档均通过；另跑 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests`，期望上传与 Folder 测试通过。仅暂存本任务的明确文件后提交 `feat(sources): expose scoped source management`。

### Task 2: AES-GCM 配置封存与 token 生命周期

**Files:** Create `src/ISEStudio/Sources/SourceSecretProtector.cs`, `src/ISEStudio.Tests/Sources/SourceSecretProtectorTests.cs`; Modify `src/ISEStudio/Sources/SourceService.cs`, `SourceDtos.cs`, `src/ISEStudio/Controllers/IngestionSourcesController.cs`, `src/ISEStudio/Program.cs`, `src/ISEStudio.Tests/Sources/SourceApiTests.cs`.

**Interfaces:** `ISourceSecretProtector.Seal(string plaintext, string associatedData)` / `Open(string ciphertext, string associatedData)`；associatedData 为 `knowledgeSystemId:sourceId:fieldName`。`POST /api/knowledge/{id}/ingestion-sources/{sourceId}/token/rotate` 返回本次新 token；`POST /api/knowledge/{id}/ingestion-sources/{sourceId}/token/reveal` 仅 Editor/Owner 允许且审计，均要求交互用户身份。API/statements push 验证用单独的 `VerifyAsync(Guid ksId, Guid sourceId, string presentedToken, CancellationToken ct)`，仅接受当前 KS 的 `api` 或 `statements` Source，不接受已有知识库只读 token。

- [ ] **Step 1: 写失败的安全测试。** 测试随机 nonce 下相同明文产生不同密文、篡改 tag 或错 KS/source/field 无法解封；密钥未配置时应用可启动、folder Source 可读写，但封存配置及 token 端点失败关闭；非空密钥格式错误或解码后不是 32 字节时启动失败。HTTP 测试在隔离测试数据库中直接播种同 KS 的 `api` Source（必要时另播种 `statements`），不通过尚未开放的创建 API；使用已有 editor/viewer 身份验证 viewer 不能 reveal/rotate、editor 轮换后旧 token 的 `VerifyAsync` 失败且新 token 成功，跨 KS 或 `folder` 的 token 不可用于 push 校验。断言 `/kinds` 与 `POST` 仍只允许 `folder`，普通列表、详情、更新响应和审计详情均不出现 token 或连接器密码；日志捕获器中也不出现测试密钥字符串。命令：`dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter 'FullyQualifiedName~SourceSecretProtectorTests|FullyQualifiedName~SourceApiTests'`，预期红灯。
- [ ] **Step 2: 实现封存格式和配置写入规则。** 从环境变量 `ISESTUDIO_SOURCE_ENCRYPTION_KEY` 读取 base64 解码后的 **32 字节**密钥，`AesGcm` 使用每次独立的 12 字节随机 nonce、16 字节 tag；存储格式 `v1:` 加上 base64(nonce|tag|ciphertext)。对配置中的 kind 专属机密字段（连接器启用阶段逐一列明）进行封存；PATCH 的 `config` 按 kind schema 对已提交字段逐项合并，省略字段（含机密字段）保留原值/密文，显式置空清除机密字段，未知字段拒绝；不得用前端未回显的空占位覆盖密文。普通详情 DTO 只输出白名单非敏感字段；连接器阶段必须对 URL 类型字段的值执行安全校验，拒绝 userinfo 与未明确列入非敏感参数白名单的 query，不得仅凭 `secret=false` 回显含凭据的 URL。解密仅在 adapter 或受限 token 接口内发生。未知版本或解密失败应返回配置错误，不将密文/明文写日志。
- [ ] **Step 3: 完成 token 轮换及身份校验。** 使用 `RandomNumberGenerator.GetBytes(32)` 生成至少 256-bit 熵 token；密文存入 `SourceEntity.IngestTokenCiphertext`，轮换在单事务中替换旧密文并审计，旧 token 当即不再匹配。推送校验检查请求的 ksId/sourceId 及 kind 属于 `api` 或 `statements`，具体路由再收紧到各自 kind；token 绑定 Source/KS 并固定时间比较，不赋予任何 KS 用户角色；token 明文只在 rotate/reveal 响应出现，响应带 `Cache-Control: no-store`。无可用密钥时 token 端点失败关闭，不暴露历史 token。
- [ ] **Step 4: 验证。** 重跑 Step 1 命令；随后运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~SourceApiTests` 与 `dotnet build src/ISEStudio.sln --no-restore`，期望 0 failures / build 成功；仅暂存任务文件，提交 `feat(sources): seal credentials and rotate source tokens`。

## Self-Review

- CRUD、KS 隔离、viewer/editor/owner/admin、凭据封存、脱敏、token 授权查看和轮换归此计划；SourceSyncRun 与同步入口由下一计划实现。
- 此阶段 kind 列表只暴露已就绪 adapter；为每种 kind 的配置增加实际白名单和签名校验是在连接器计划内完成，不能凭本计划声明外部来源可用。
- 执行前复核前一阶段合入的实体属性/迁移列名；不重用已有只读 KS API token，避免将读取权限扩展到摄入。
