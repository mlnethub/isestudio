# 知识摄入 Source 界面与验收 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 KS 文档工作区增加独立 Source 管理和上传选择器，并用跨层验收验证 Source 与虚拟 Folder、授权和同步状态互不混淆。

**Architecture:** 保留 `KsDocuments` 的 Folder 浏览；在 `DocumentsExtractionPanel` 增加同级 Sources 视图并调用独立的 `ingestion-sources` API。前端由服务端 `GET /ingestion-sources/kinds` 决定实际可选 kind；前后端合同和 Playwright .NET 工作流共同验收，不把未交付的 `azure_blob` 暴露给用户。

**Tech Stack:** React 19、TypeScript、Vite、pnpm、Vitest、Playwright、ASP.NET Core .NET 10、xUnit/PostgreSQL。

## Global Constraints

- 依赖[基础](2026-09-29-knowledge-sources-foundation.md)、[管理](2026-09-29-knowledge-sources-management.md)、[同步](2026-09-29-knowledge-sources-sync.md)及[连接器](2026-09-29-knowledge-sources-connectors.md)计划，并核对[设计稿](../specs/2026-09-29-knowledge-ingestion-sources-design.md)。
- 图谱证据 `GET /api/knowledge/{id}/sources`、`SourceDoc` 保持原状；摄入 Source 只调用 `/api/knowledge/{id}/ingestion-sources`，新类型命名为 `IngestionSource`。
- `folder` 是被动 Source kind；`DocumentMeta.folder` 是独立虚拟 Folder。上传仅可选当前 KS 的 `folder` Source；移动文档的 Folder 不修改来源和 `external_key`。
- UI 仅列出服务端返回的可创建 kind；`azure_blob` 本轮不出现在选项中，不开发 SDK、配置页或验收路径。
- 普通列表/详情不渲染 token/密文；Viewer 只能查看，Editor/Owner 才可写或触发；token 显示限受授权的专用操作，轮换后旧值不再可用。
- token 浏览器测试仅运行于 Playwright 本次启动的本地/CI 测试后端和隔离测试 KS，禁止连接任何预启动、共享或生产后端。启动前由测试运行环境提供 `ISESTUDIO_SOURCE_ENCRYPTION_KEY`（base64 编码的随机 32 字节，仅保存在进程环境中；CI 使用机密变量，禁止打印或提交）。例如在 PowerShell 会话先执行 `$env:ISESTUDIO_SOURCE_ENCRYPTION_KEY = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`。默认跳过 token 用例；仅显式设置 `ISESTUDIO_SOURCE_TOKEN_E2E=1` 且未设置 `DOTNET_BASE_URL` 时启用。配置必须禁止此模式复用已有服务，端口被占用就失败并记录未验证，不得回退到复用；预启动后端的 CI 必须另开由 Playwright 管理的隔离作业执行 token 验收。

## 文件职责与接口

- Modify `frontend/src/lib/types.ts`, `frontend/src/lib/api.ts`：摄入 Source、运行/job DTO 与 API 方法；避免覆盖图谱 `SourceDoc` 和 `api.getSources`。
- Create `frontend/src/components/IngestionSourcesPanel.tsx`：Source 列表、配置编辑、状态/最近运行历史、手动同步、受限 token 轮换/查看；使用现有按钮、表单、对话框和状态组件。
- Modify `frontend/src/components/DocumentsExtractionPanel.tsx`, `frontend/src/components/KsDocuments.tsx`：独立 Sources tab、默认/其他 folder Source 选择器，保留 Folder 搜索/导航、解析和移动文档入口。
- Modify `frontend/src/lib/i18n.tsx`：英文和中文 Source 文案/状态/错误，不显示凭据明文。
- Test `frontend/e2e/dotnet/ingestion-sources.spec.ts`；Modify `frontend/playwright.config.ts` 为 token 用例加入隔离启动门槛；沿用 `frontend/e2e/dotnet/helpers/auth.ts` 的 seeded admin 和 `helpers/config.ts` 的后端可达性检查；浏览器只测真实后端可执行路径。Viewer/Editor HTTP 授权由 `SourceApiTests` 覆盖，fake adapter/同步状态与缺失对账由 `SourceSyncTests`/`SourceSyncWorkerTests` 覆盖；现有前端没有组件级 `.test.tsx` 文件，不新增独立测试框架。

---

### Task 1: 有类型的管理客户端与独立 Sources 视图

**Files:** Modify `frontend/src/lib/types.ts`, `frontend/src/lib/api.ts`, `frontend/src/lib/i18n.tsx`, `frontend/src/components/DocumentsExtractionPanel.tsx`; Create `frontend/src/components/IngestionSourcesPanel.tsx`, `frontend/e2e/dotnet/ingestion-sources.spec.ts`.

**Interfaces:** `IngestionSource { id, kind, name, icon?, sync_interval_minutes?, sync_cron?, last_synced_at: string | null, last_sync_status, last_sync_error?, last_sync_added, document_count, missing_document_count, created_at }`；`IngestionSourceDetail` 继承基础字段并包含仅非敏感值的 `config`；`IngestionSourceRun { id, status, started_at, finished_at?, added_count, updated_count, error? }`；`api.listIngestionSources(ksId)`、`getIngestionSource(ksId, sourceId)`、`getIngestionSourceKinds(ksId)`、`listIngestionSourceRuns(ksId, sourceId)`；已有 `SourceDoc` 和 `api.getSources` 不变。

- [x] **Step 1: 写失败的浏览器合同。** 用现有 `.NET` Playwright 配置的 seeded admin 登录 KS，进入 Documents 工作区；新增 Sources tab 应可见默认 folder Source，文档 tab 仍可浏览现有虚拟 Folder。测试拦截 `GET /api/knowledge/{ksId}/ingestion-sources` 检查名称、状态及 `last_synced_at`；从未同步的 Source 显示“尚未同步”，不能把 `created_at` 误标为最近同步时间。确认旧 `/sources` 图谱请求仍保持；后端不可达时沿用既有 e2e 可达性判断（按现有套件约定跳过并在验收记录注明未验证）。Viewer 只读/不能触发或查看 token 的断言放在 `SourceApiTests`，不假设 Playwright 有 Viewer 账号。命令：`cd frontend; pnpm test:e2e:dotnet --grep "ingestion sources"`；预期新增用例先失败。
- [x] **Step 2: 接入管理 API 与视图。** 在 `types.ts` 添加独立类型；在 `api.ts` 只增加 `ingestion-sources` 前缀的请求，保留所有图谱 API 方法。`DocumentsExtractionPanel` 现有 documents/queue tabs 加同级 sources tab，用 URL `?tab=sources` 保留刷新/返回状态；独立 `IngestionSourcesPanel` 展示密集可扫描的 kind、名称、last_sync_status、最近同步时间（由 `last_synced_at` 提供，null 时显示“尚未同步”）、文档/缺失数、最近 50 条 run，加载/空/错误状态完整；Viewer 隐藏写入按钮。使用现有 UI kit、lucide 图标和中英翻译键。
- [x] **Step 3: 验证与提交。** 重跑 Step 1，执行 `cd frontend; pnpm build; pnpm lint`；期望用例、类型编译和 lint 均成功。只暂存本任务路径，提交 `feat(frontend): browse ingestion sources independently of folders`。

### Task 2: 创建/编辑、立即同步与 token 控件

**Files:** Modify `frontend/src/components/IngestionSourcesPanel.tsx`, `frontend/src/lib/api.ts`, `frontend/src/lib/types.ts`, `frontend/src/lib/i18n.tsx`, `frontend/playwright.config.ts`, `frontend/e2e/dotnet/ingestion-sources.spec.ts`.

**Interfaces:** `api.createIngestionSource/updateIngestionSource/deleteIngestionSource`, `syncIngestionSource`, `getIngestionSourceJob`, `rotateIngestionSourceToken`, `revealIngestionSourceToken` 对接管理/同步计划的 KS 路由；token 仅存组件临时状态，不放入 localStorage、URL 或普通 Source DTO。

- [ ] **Step 1: 写失败 UI 合同。** seeded admin 可从 `getIngestionSourceKinds` 返回的 kind 创建/修改/删除 folder Source，删除时保留已归属文档；编辑已保存的非敏感配置时通过 `getIngestionSource` 读取并预填其值，保存未修改的机密字段不得发送空占位、不得清除原密文（此合同由连接器阶段的 `SourceApiTests` 使用真实 kind 验证）。`folder/api/statements` 无同步按钮，`azure_blob` 不出现在可创建下拉菜单。token 查看/轮换只对可清理的临时 push Source 验证，关闭对话框后清除组件状态，失败不展示明文；将含明文操作的用例放入独立 Playwright `test.describe`，用 `test.use({ trace: 'off', screenshot: 'off', video: 'off' })` 覆盖全局失败留痕配置，禁止记录响应体、页面内容或 token 值到断言错误、控制台、报告及附件；用 `try/finally` 删除本次创建的测试 Source，即便失败也尝试清理。`frontend/playwright.config.ts` 在 `ISESTUDIO_SOURCE_TOKEN_E2E=1` 且 `DOTNET_BASE_URL` 存在时立即拒绝运行，此模式下 `webServer.reuseExistingServer = false`，端口已占用直接失败；测试还应验证这两种拒绝路径，不准在服务可达性判断中跳过该错误。未显式启用时 token 用例跳过且记未验证。真实主动来源需要可控上游才可端到端同步，queued/running/ok/failed、同源 job 去重及最近运行记录由 `SourceSyncWorkerTests`/`SourceSyncTests` 验证；Viewer/Editor/Owner 权限由 `SourceApiTests` 验证，不在此用例伪造响应。运行 `cd frontend; pnpm test:e2e:dotnet --grep "ingestion sources"`，预期非 token 新增用例失败。
- [x] **Step 2: 增加完整操作状态。** 每种 kind 的表单用 `/kinds` 的 config schema 构建字段，进入编辑时调用 `getIngestionSource`，只用详情 `config` 中的非敏感值预填；机密输入保持空且不发送未改动字段，只有显式输入新值才写入，清除机密字段需独立确认操作。PATCH 仅提交改动字段，由服务端按白名单合并，不能把空占位或旧详情对象整体回传。计划输入 interval/cron 互斥，验证错误聚焦对应控件。Source 列表以 action menu 承载编辑/删除/同步，确认删除后刷新列表及文档来源；同步使用 job ID 轮询详情直至终态，组件卸载时清除计时器；token 对话框使用受限 POST reveal/rotate、`Cache-Control: no-store` 服务端合同，只在短暂显示区展示明文并提供复制动作；网络失败保留非机密表单信息。不得在前端日志/遥测中打印 token。
- [ ] **Step 3: 验证与提交。** 重跑 Step 1；在无 `DOTNET_BASE_URL`、端口未占用、测试密钥已注入的隔离环境中设置 `$env:ISESTUDIO_SOURCE_TOKEN_E2E = '1'`，运行 `cd frontend; pnpm test:e2e:dotnet --grep "ingestion sources token"`，再执行 `pnpm build; pnpm lint`；确认 token 用例实际执行而非跳过、没有生成 trace/截图/视频附件、普通页面和测试报告/日志不含 token，且测试 Source 已清理；不打开或生成包含明文的 trace 来验证泄漏。任一留痕、跳过或清理检查失败则不算 token 验收通过。只暂存本任务路径，提交 `feat(frontend): manage source config sync and tokens`。

### Task 3: 手工上传选择 folder Source 且不改变 Folder 导航

**Files:** Modify `frontend/src/components/KsDocuments.tsx`, `frontend/src/lib/api.ts`, `frontend/src/lib/types.ts`, `frontend/src/lib/i18n.tsx`, `frontend/e2e/dotnet/ingestion-sources.spec.ts`.

**Interfaces:** `api.uploadDocument(ksId: string, file: File, folder = "/", sourceId?: string)` 在 FormData 中可选附加 `source_id`；不改传给后端的 `folder` 路径。`DocumentMeta.source_id: string | null` 对应基础阶段 `DocumentOut.SourceId`，仅表示主归属，不表示全部绑定。选择器只取 `listIngestionSources(ksId)` 中 `kind === "folder"` 的项，默认服务端约定 `(created_at, id)` 最早者；不传 Source 时后端仍自动选择默认。

- [ ] **Step 1: 写失败的上传/移动测试。** 同一 KS 创建第二个 folder Source，UI 选中后上传 `note.txt` 到 `/manual`；HTTP multipart 同时带 `folder=/manual`、`source_id=<secondId>`；通过真实文档列表/详情响应断言 `source_id == secondId`，文档可继续按虚拟 Folder 浏览，移动到 `/archive` 后重新读取该响应确认 `folder` 已变化而 `source_id` 不变。上传相同字节到不同 folder Source 时仍只有一个 KS 文档且响应中的 `source_id` 不改变；选择已删除/跨 KS Source 时服务端 400 且 UI 呈现错误。拖拽上传与文件选择器走同一选择状态。当前 seeded-admin Playwright 会话不做 Viewer 控件断言；Viewer 上传拒绝由基础阶段的 `DocumentApiTests` 分别验证省略和指定 `source_id` 的请求，控件隐藏另按 Task 4 的真实 Viewer UI 验收。命令：`cd frontend; pnpm test:e2e:dotnet --grep "ingestion sources"`，预期失败。
- [x] **Step 2: 传递独立选择状态。** `KsDocuments` 从 Source 列表计算 folder-only 选项及默认选项，label 显示 Source 名称、与虚拟 Folder 输入分列；所有文件输入/拖拽路径统一经过 `uploadFiles` 的 `sourceId`，但保留 `cwd` 与移动 Folder API 原逻辑。在 `types.ts` 的 `DocumentMeta` 增加可空 `source_id`；`api.uploadDocument` 只在 sourceId 有值时向 FormData 添加 `source_id`；删除默认 Source 后刷新列表并按服务端 `(created_at,id)` 顺序选择下一条，没有 folder Source 时交由服务端按基础计划创建默认项。
- [x] **Step 3: 验证与提交。** 重跑 Step 1 和现有 `cd frontend; pnpm test:e2e:dotnet --grep "upload.*extract.*publish"`，再跑 `cd frontend; pnpm build; pnpm lint`；期望通过。只暂存本任务文件，提交 `feat(frontend): choose folder source for document upload`。

### Task 4: 跨层验收与文档合同

**Files:** Modify `frontend/e2e/dotnet/ingestion-sources.spec.ts`, `frontend/src/content/docs/en/documents.md`, `frontend/src/content/docs/zh-CN/documents.md`, `docs/acceptance.md`; Test existing `src/ISEStudio.Tests/Sources/SourceApiTests.cs`, `src/ISEStudio.IntegrationTests/Ingestion/SourceSyncTests.cs`, `SourceConnectorTests.cs`.

**Interfaces:** 验收仅针对本轮已注册 `folder/url/rss/custom/github_issues/jira_issues/s3/gcs/webdav/notion/api/statements`；`azure_blob` 明确延期且被管理 API/UI 拒绝，不暗示整份旧版 Utopia kind 清单已上线。

- [ ] **Step 1: 补端到端断言。** 浏览器用 seeded admin 和真实后端走一次登录、选 KS、创建 folder Source、上传到其他 folder Source、解析文档、移动虚拟 Folder，并从真实文档列表/详情响应核对 `source_id` 仍为所选 SourceId；仅在上述独占测试环境创建临时 api Source 并轮换 push token，含明文用例须沿用 Task 2 独立 `test.describe` 的关闭留痕和失败清理要求，浏览器只检查对话框清除与非敏感页面内容，旧 token 失效由 `SourcePushTests` 断言；确认 `azure_blob` 不出现在创建菜单且后端创建返回 400。非 Editor 权限与非法网络目标分别由 `SourceApiTests`/`SourceNetworkPolicyTests` 验证；fake 主动来源同步/缺失、job 状态由 `SourceSyncTests`/`SourceSyncWorkerTests` 验证。不在浏览器用例中 mock 后端响应作为全链路验收，不依赖公网或生产机密。
- [x] **Step 2: 更新用户文档。** 在现有中英文件说明 Source 与 Folder 的区别、手动上传默认与选择来源、可用 kind/状态、token 查看和轮换、完整扫描才标记缺失；在 `docs/acceptance.md` 记录运行命令、失败边界与本轮 `azure_blob` 不可用，不写密钥值。
- [x] **Viewer UI 验收（需真实 Viewer 会话）。** 使用一个仅有目标 KS Viewer 权限的测试账号登录同一文档工作区，核对 Sources 可读、创建/编辑/删除/同步/token 控件和上传选择器/拖拽入口均不可见；无 KS 权限账号不可进入该 KS。将测试环境、账号角色（不记凭据）和结果写入 `docs/acceptance.md`。仅 seeded-admin 自动化或 `SourceApiTests` 通过时，该 UI 项须标记“未验证”，不得声明 Viewer UI 已通过；后续若提供 Viewer Playwright fixture，可将此验收转为自动用例。
- [ ] **Step 3: 运行验收门槛。** `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter 'FullyQualifiedName~SourceApiTests|FullyQualifiedName~SourceSecretProtectorTests|FullyQualifiedName~SourceNetworkPolicyTests|FullyQualifiedName~SourceAdapterTests|FullyQualifiedName~SourcePushTests|FullyQualifiedName~DocumentApiTests'`; `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter 'FullyQualifiedName~KnowledgeSourceSchemaTests|FullyQualifiedName~DocumentVersionStoreTests|FullyQualifiedName~DocumentBlobConcurrencyTests|FullyQualifiedName~SourceSyncTests|FullyQualifiedName~SourceSyncWorkerTests|FullyQualifiedName~SourceConnectorTests'`; `dotnet build src/ISEStudio.sln --no-restore`; `cd frontend; pnpm test; pnpm build; pnpm lint; pnpm test:e2e:dotnet`；确认 PostgreSQL 锁交错与 RDF merge/push 并发用例实际执行，token 用例另须按 Task 2 的隔离命令实际执行。需逐条确认通过，token 被跳过或依赖（Docker、服务）不可用时记录为未验证，不写成已通过；只暂存本任务文档/测试，提交 `test(sources): verify source ingestion and document workflows`。

## Self-Review

- UI 挂载在既有 KS Documents 工作区的同级 tab，保留 Folder 视图、上传/解析/移动流程；管理 CRUD、状态/历史、同步和 token 操作按角色呈现。
- 客户端将 `IngestionSource` 与图谱 `SourceDoc` 分开；所有 API 使用 `/ingestion-sources`，不与现有 `/sources` 冲突。
- 全链路覆盖 Source 身份、版本、授权、缺失对账、SSRF、结构化 statements 和 API token；`azure_blob` 已从实施和验收中排除，待单独批准 SDK 方案。
