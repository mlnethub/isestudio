# 知识摄入连接器与网络安全 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在持久同步协调器之上交付本轮可用的外部来源、API push 与 statements 写入，并用统一网络策略阻断 SSRF。

**Architecture:** 主动拉取的 kind 通过 `ISourceAdapter` 返回 `SourceItem`，同步协调器统一负责 Blob、来源身份、原始版本和解析任务；被动 `api` 与 `statements` 使用受限 token 入口，不伪装成周期性扫描。所有可用 kind 通过同一个 adapter/handler 注册表暴露给管理 API 与前端。

**Tech Stack:** .NET 10、ASP.NET Core、EF Core/Npgsql、PostgreSQL 16、`HttpClientFactory`、xUnit、Testcontainers；S3/GCS 使用各自官方 .NET SDK，**不引入 Azure SDK**。

## Global Constraints

- 依赖[基础](2026-09-29-knowledge-sources-foundation.md)、[管理](2026-09-29-knowledge-sources-management.md)及[同步](2026-09-29-knowledge-sources-sync.md)计划；先核对[设计稿](../specs/2026-09-29-knowledge-ingestion-sources-design.md)。
- `azure_blob` 只保留共享 kind 标识：本轮不实现 adapter、Azure SDK 依赖、创建/同步、UI 选项或验收用例；`memory/upload` 也不对外创建。
- 所有可摄入普通文档的 kind 必须给非空稳定 `ExternalKey`；不以文件名或字节 SHA 代替上游身份；协调器仍保留 KS 级 SHA 去重和成功扫描才标记缺失的约束。
- 缺少运行凭据、无效配置或上游返回部分页面时运行失败，保留已提交条目，不对账缺失。绝不把 token、连接器密钥、带凭据 URL 或请求正文写入错误、审计或日志。
- `folder` 只接收手动上传；`api` push 和 `statements` 不定时扫描，也不参与缺失对账；UI kinds 列表只展示本阶段注册成功的 kind。

## 文件职责与接口

- Create `src/ISEStudio/Sources/Networking/SourceNetworkPolicy.cs`, `SafeSourceHttpClient.cs`：解析目标 DNS、校验每个地址及逐跳重定向；受控 GET/PROPFIND 的请求选项提供封存凭据与白名单请求头，共用连接和响应限制；仅 HTTP(S)。
- Create `src/ISEStudio/Sources/Adapters/` 下 `UrlSourceAdapter.cs`, `RssSourceAdapter.cs`, `CustomSourceAdapter.cs`, `GitHubIssuesSourceAdapter.cs`, `JiraIssuesSourceAdapter.cs`, `S3SourceAdapter.cs`, `GcsSourceAdapter.cs`, `WebDavSourceAdapter.cs`, `NotionSourceAdapter.cs`：各自只负责配置验证、完整发现和稳定 key；共用网络策略或官方 SDK 客户端。
- Modify `src/ISEStudio/Sources/SourceAdapterRegistry.cs`：扩展管理阶段已有的 `folder` 注册表，加入已就绪 kind 的主动 adapter 或被动 push handler、`active_sync`、公开配置字段描述与校验器；管理 API 只返回当前可用 kind 的描述，不注册 `azure_blob`。
- Create `src/ISEStudio/Controllers/SourcePushController.cs`, `src/ISEStudio/Sources/SourcePushService.cs`：API 单条 push 以及结构化 statements 接口；在入库前按 KS、SourceId、kind 和 Source token 校验。
- Modify `src/ISEStudio/Sources/SourceSyncCoordinator.cs`, `SourceService.cs`, `SourceDtos.cs`, `src/ISEStudio/Program.cs`, `src/ISEStudio/ISEStudio.csproj`：提供单条摄入入口、连接器注册和配置；不修改现有图谱 `/api/knowledge/{id}/sources` 路由。
- Test `src/ISEStudio.Tests/Sources/SourceNetworkPolicyTests.cs`, `SourceAdapterTests.cs`, `SourcePushTests.cs`; `src/ISEStudio.IntegrationTests/Ingestion/SourceConnectorTests.cs`：确定性 HTTP/fake SDK、身份/分页、凭据与图谱回归。

---

### Task 1: 出站网络策略及注册资格

**Files:** Create `src/ISEStudio/Sources/Networking/SourceNetworkPolicy.cs`, `SafeSourceHttpClient.cs`, `src/ISEStudio.Tests/Sources/SourceNetworkPolicyTests.cs`; Modify `src/ISEStudio/Sources/SourceAdapterRegistry.cs`, `src/ISEStudio/Program.cs`, `src/ISEStudio/Sources/SourceService.cs`.

**Interfaces:** `SourceNetworkPolicy.ValidateAsync(Uri uri, CancellationToken ct) -> IReadOnlyList<IPAddress>`；`SafeSourceHttpClient.GetAsync(Uri uri, SourceRequestOptions options, CancellationToken ct) -> Stream` 和 `PropFindAsync(Uri uri, int depth, SourceRequestOptions options, CancellationToken ct) -> Stream`，无凭据调用使用空选项。`SourceRequestOptions` 区分受限凭据（Authorization/Cookie，仅由服务端封存配置解密后提供）与白名单普通头（Accept、If-None-Match、Notion-Version）；拒绝调用方设置 Host、Forwarded、Depth 及任意未列入白名单的头，Depth 仅由 `depth` 参数控制。两种方法走相同的 DNS 固定连接/重定向/限额流程；不向 adapter 暴露可绕过策略的原始 `HttpClient`。注册表的 `CreatableKinds` 只含可实际执行的 kind，绝不含 `azure_blob`。在连接器安装前，`folder` 仍可创建。

- [ ] **Step 1: 写失败测试。** 对 `file:`、`http://127.0.0.1/`、`http://[::1]/`、`169.254.169.254`、RFC1918、IPv6 ULA、多 A/AAAA 记录中含一个私网地址、公开 URL 302 到私网地址分别断言拒绝；对允许 CIDR 内目标及公开 HTTP(S) 断言通过；断言禁用自动重定向且每跳重新解析并校验，重绑定解析地址不能由 HTTP handler 自行另行解析。受控选项发送 Authorization/Cookie、Accept、If-None-Match 与 Notion-Version；Host、Forwarded、Depth 及任意未列入白名单的头被拒。带 Authorization/Cookie 的 HTTPS 请求重定向到同 host 的 HTTP、同 host 不同端口或其他 host 时不得在目标发送凭据；同 origin 的 HTTPS 重定向允许继续使用凭据，普通头不因重定向升级为凭据。PROPFIND 用 fake handler 断言正确方法与受限 Depth、私网重定向被拒、跨 origin 凭据不转发、301/302 不被重写为 GET；不得通过裸 `HttpClient` 绕过策略。超时、超字节、超条目、未知 Content-Type 均失败；缺少密钥时不回退明文。运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~SourceNetworkPolicyTests`，预期失败。
- [ ] **Step 2: 实现安全边界。** 从受管配置读取显式允许的 CIDR（默认空）；用可注入 DNS 解析器检查解析后的**所有**目标地址，拒绝 loopback、私有、link-local、组播、保留地址及不允许的 IPv6 映射地址；使用经过校验的 IP 发起连接，同时保持原 host 用于 TLS SNI/Host 校验。请求选项仅从 `SourceSecretProtector` 解密后的封存字段设置凭据，普通头只接受明确白名单，不记录头值。`HttpClientHandler.AllowAutoRedirect = false`，按最多 5 跳重复上述流程；每请求默认 30 秒、响应 20 MiB、每次发现最多 1000 条，按流读取时限额。重定向以 `(scheme, host, port)` 比较 origin；带凭据请求拒绝 HTTPS→HTTP 降级及任何跨 origin 重定向，不转发 Authorization、Cookie 或其他机密头；只允许同 origin 保留凭据。`PropFindAsync` 只允许受限 `Depth`（0/1），禁用自动重定向；仅对 307/308 在逐跳重新校验后保持 PROPFIND 方法，其他重定向失败关闭，不改写成 GET；请求和响应同样适用 DNS 固定连接、时限及体积限制。只捕获脱敏的异常类别，不记录 URL query/userinfo 或响应体。
- [ ] **Step 3: 验证注册列表。** 沿用管理阶段的 `SourceAdapterRegistry`，此任务完成时 `GET /api/knowledge/{id}/ingestion-sources/kinds` 仍只含 folder；后续每完成一个主动 adapter 或被动 push handler 才将该 kind 及配置校验/描述加入同一注册表。为 `azure_blob/memory/upload` 写负面断言：创建和手动同步均拒绝；任何现存该 kind 配置也不触发后台 worker。重跑 Step 1 命令并运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~SourceApiTests`，预期通过；仅暂存本任务文件并提交 `feat(sources): constrain outbound connector requests`。

### Task 2: URL、RSS 与 custom 的 HTTP 发现

**Files:** Create `src/ISEStudio/Sources/Adapters/UrlSourceAdapter.cs`, `RssSourceAdapter.cs`, `CustomSourceAdapter.cs`, `src/ISEStudio.Tests/Sources/SourceAdapterTests.cs`; Modify `src/ISEStudio/Sources/SourceAdapterRegistry.cs`, `src/ISEStudio/Sources/SourceService.cs`, `src/ISEStudio.Tests/Sources/SourceApiTests.cs`.

**Interfaces:** `ISourceAdapter.DiscoverAsync(SourceEntity source, CancellationToken ct) -> SourceScan`；URL key 是移除 fragment 并标准化 host/scheme 的规范 URL；RSS key 优先 feed GUID，缺失时为规范 item link；custom key 为响应项的非空 `id`。`SourceScan.IsComplete` 仅在所有分页/条目成功读取后为 true。

- [ ] **Step 1: 写失败的 adapter 测试。** 用注入的 fake HTTP handler 返回网页、RSS/Atom XML、自定义 JSON items 和多页结果；断言稳定 key、来自上游的 `DocTime`、重复 key 拒绝、空 item/过大 body 失败、只完成第一页时 `IsComplete == false`。RSS 全文补全只请求通过网络策略的正文链接；补全失败不得把部分 feed 标为完整成功。`custom` 配置必须指定安全 HTTP(S) endpoint 及 JSON items/id/content 契约，未声明完整分页结束标志时不得对账。在 `SourceApiTests` 中以已注册的 `custom` kind 创建含非敏感 endpoint 和封存认证值的 Source，断言详情可预填 endpoint、列表/详情/更新响应均不含认证值或密文；PATCH 只修改 endpoint 后认证值仍可用于 adapter，显式清除时才删除。另对 URL/RSS/custom 的可回显 URL 配置断言：POST 和 PATCH 均拒绝含 userinfo、`?token=...`、`?api_key=...`、签名参数或其他未列入非敏感白名单的 query；普通列表、详情、审计和日志均不得回显这些测试凭据，合法无凭据 URL 仍可预填。运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter 'FullyQualifiedName~SourceAdapterTests|FullyQualifiedName~SourceApiTests'`，预期失败。
- [ ] **Step 2: 实现并注册。** URL 单页内容使用当前文档解析队列；RSS 使用结构化 XML parser，解析 RSS GUID/Atom id 与链接并逐项获取内容；custom 使用 `System.Text.Json` 解析 items schema，拒绝未知类型/空 ID/重复 ID。URL 类型 config 字段在 POST/PATCH 时用 URI 解析器拒绝 userinfo 与默认不允许的 query；只有各连接器显式允许并验证为非敏感的参数可保留，不能从字段名 `secret=false` 推断整个 URL 值安全。`config` 白名单只回传通过值校验的非机密 URL、feed 与分页设置，认证字段只经 `SourceSecretProtector` 解封；连接器运行前校验 schema，所有跨域和重定向再次走 `SafeSourceHttpClient`。分别注册 `url/rss/custom`；测试同步协调器可由同一 key 幂等更新文档、RSS 列表摘要仅 RSS 非空。
- [ ] **Step 3: 验证。** 重跑 Step 1 命令（含 `SourceApiTests` 的真实 kind 配置读写与凭据保留断言）；再运行 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~SourceSyncTests`，期望均通过；只提交本任务路径，提交 `feat(sources): discover url rss and custom items`。

### Task 3: GitHub 与 Jira 工单

**Files:** Create `src/ISEStudio/Sources/Adapters/GitHubIssuesSourceAdapter.cs`, `JiraIssuesSourceAdapter.cs`; Modify `src/ISEStudio/Sources/SourceAdapterRegistry.cs`, `src/ISEStudio/Sources/SourceService.cs`, `src/ISEStudio.Tests/Sources/SourceAdapterTests.cs`, `src/ISEStudio.Tests/Sources/SourceApiTests.cs`.

**Interfaces:** GitHub key 为仓库内 issue 的不变 node ID（若上游未给出则拒绝）；Jira key 为 issue id 而非可变化的显示 key；更新内容包含状态、标题、正文和时间戳。

- [ ] **Step 1: 写失败测试。** fake HTTP 返回多页工单、关闭/重开状态变更、速率限制 429、分页中断、空 id、被重定向到内部地址。断言相同 issue 只更新原文档且版本递增，失败的分页不标缺失；已存在 `{a,b}` 时模拟第一页 200 返回 `a`、第二页 304，断言 304 不被当成空页或完整扫描，`b` 不标缺失且 run 失败；对工单列表页断言请求不发送 `If-None-Match`。无权限/401 只产生脱敏错误，日志不含访问 token。在 `SourceApiTests` 中验证 Jira base URL 的创建和 PATCH 拒绝 userinfo 与未获非敏感白名单允许的 query，详情不得泄漏这些测试凭据。运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter 'FullyQualifiedName~SourceAdapterTests|FullyQualifiedName~SourceApiTests'`，预期新增测试失败。
- [ ] **Step 2: 实现两个 adapter。** GitHub 仓库 owner/name、Jira base URL/project 为必填配置；Jira URL 配置在注册前落实与 Task 2 相同的创建/PATCH 值级校验及安全回显。只访问经网络策略允许的 API 端点，安全处理分页、429 和 ETag；本阶段对列表及分页请求不发送 `If-None-Match`，每次完整枚举所有页才能声明 `IsComplete`。收到意外 304 时视为不完整扫描并令 run failed，不能把该页当作空列表或触发缺失对账；未来若引入条件列表请求，须先有可复用的完整分页 key 快照与相应对账测试。rate limit/非完整分页同样触发 run failed。上游 token 从封存 config 解密后经受控 `SourceRequestOptions` 发送，ETag 可作为响应元数据记录，但本阶段不对列表页发送条件请求；带凭据的重定向只允许同 origin，绝不向其他 origin 转发认证头。输出可由现有 parser 处理的文本和上游 updated_at，注册两种 kind；不增加专用解析器。
- [ ] **Step 3: 验证。** 重跑 Step 1 及 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~SourceSyncTests`，预期通过；只提交本任务路径，提交 `feat(sources): ingest issue trackers`。

### Task 4: S3 与 GCS 对象发现

**Files:** Create `src/ISEStudio/Sources/Adapters/S3SourceAdapter.cs`, `GcsSourceAdapter.cs`; Modify `src/ISEStudio/Sources/SourceAdapterRegistry.cs`, `src/ISEStudio/Sources/SourceService.cs`, `src/ISEStudio/ISEStudio.csproj`, `src/ISEStudio.Tests/Sources/SourceAdapterTests.cs`, `src/ISEStudio.IntegrationTests/Ingestion/SourceConnectorTests.cs`.

**Interfaces:** S3 key 是 bucket+object key，GCS key 是 bucket+object name（Source 范围内稳定），对象 ETag/generation 仅用于识别修改，不作为文件名；从上游 last-modified 提供 `DocTime`。禁用不可信自定义 endpoint；所需区域和 bucket 必须显式配置。

- [ ] **Step 1: 写失败测试。** 用模拟的 SDK list/get client 返回多页、空桶、断页、同名对象更新、超尺寸对象；断言只有所有页成功完成时可标缺失、相同 object identity 去重、不把 storage 凭据暴露给 Source DTO；对象下载失败不丢弃其他已成功对象。运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~SourceAdapterTests`，预期失败。
- [ ] **Step 2: 安装并封装官方 SDK。** `dotnet add src/ISEStudio/ISEStudio.csproj package AWSSDK.S3` 和 `dotnet add src/ISEStudio/ISEStudio.csproj package Google.Cloud.Storage.V1`；将 SDK 列举/下载隔离在可注入接口后，限制客户端到受信任的云服务地址，不允许配置任意 endpoint 或关闭 TLS 校验；S3/GCS 独立 config schema 检查 bucket、prefix、region/凭据，密钥仅封存存储；逐页消费 continuation token 并显式限定 item 字节和数量。注册 `s3/gcs`，不注册 `azure_blob`。
- [ ] **Step 3: 验证。** 重跑 Step 1；`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~SourceConnectorTests` 断言 item 到解析 job/文件版本的完整路径；`dotnet build src/ISEStudio.sln --no-restore`；均预期成功。只提交本任务路径和已解析的包版本，提交 `feat(sources): sync s3 and gcs objects`。

### Task 5: WebDAV 与 Notion

**Files:** Create `src/ISEStudio/Sources/Adapters/WebDavSourceAdapter.cs`, `NotionSourceAdapter.cs`; Modify `src/ISEStudio/Sources/SourceAdapterRegistry.cs`, `src/ISEStudio/Sources/SourceService.cs`, `src/ISEStudio.Tests/Sources/SourceAdapterTests.cs`, `src/ISEStudio.Tests/Sources/SourceApiTests.cs`.

**Interfaces:** WebDAV key 为规范化的远端 href（仅在配置的根路径下）；Notion key 为页面/数据库 item ID，不使用标题作为身份。只有完整遍历 scope 和分页才设置 `IsComplete`。

- [ ] **Step 1: 写失败测试。** fake HTTP 验证 PROPFIND 多级遍历、href 越界、循环集合、目录不当作文件、文件变更；Notion 分页、block 列表与 last_edited_time、归档/删除；WebDAV 的 PROPFIND 必须通过 `SafeSourceHttpClient.PropFindAsync`，返回的 href 下载走其 `GetAsync`，两者的重定向都过 SSRF/凭据策略。故障/限流不对账，日志及 DTO 不含 WebDAV 凭据/Notion token；在 `SourceApiTests` 中验证 WebDAV/Notion 若有可回显 URL 配置，创建和 PATCH 拒绝 userinfo 与未获非敏感白名单允许的 query，详情不得泄漏这些测试凭据。运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter 'FullyQualifiedName~SourceAdapterTests|FullyQualifiedName~SourceApiTests'`，预期失败。
- [ ] **Step 2: 实现并注册。** WebDAV 使用限定根目录的 XML 解析及广度遍历，拒绝离开根的 href 或循环引用；列举使用受控 `PropFindAsync`，下载使用受控 `GetAsync`，不得调用原始 `HttpClient`；Notion 使用官方分页接口，固定的 Notion-Version 经白名单普通头传入，Authorization 经受控凭据选项传入，正文规范化为可解析文本；两个 adapter 均对页数、深度、文件大小限额，凭据仅从封存 config 解密，所有出站请求经受控 client。注册 `webdav/notion` 前对其可回显 URL 配置实施 Task 2 的值级校验，认证信息仅存封存字段；源同步沿用共同协调器。
- [ ] **Step 3: 验证。** 重跑 Step 1；运行 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~SourceSyncTests`，期望通过；只提交本任务文件，提交 `feat(sources): discover webdav and notion content`。

### Task 6: Source-scoped API push

**Files:** Create `src/ISEStudio/Controllers/SourcePushController.cs`, `src/ISEStudio/Sources/SourcePushService.cs`, `src/ISEStudio.Tests/Sources/SourcePushTests.cs`; Modify `src/ISEStudio/Sources/SourceSyncCoordinator.cs`, `src/ISEStudio/Sources/SourceAdapterRegistry.cs`, `src/ISEStudio/Program.cs`, `src/ISEStudio.IntegrationTests/Ingestion/SourceConnectorTests.cs`.

**Interfaces:** `POST /api/knowledge/{id}/ingestion-sources/{sourceId}/documents` 接收 Bearer Source token、必填 `external_key` 与字节流；`SourceSyncCoordinator.IngestItemAsync(Guid sourceId, SourceItem item, CancellationToken ct) -> SourceItemResult` 复用同步的逐条事务，但不执行缺失对账、不创建 source-run。仅两个 push 动作显式 `[AllowAnonymous]`，由 `SourcePushService` 验证 Source token；不得依赖默认 Session/JWT Bearer 认证结果、KS 用户角色或现有只读 API token 放行，受限 token 不获得查询或管理权限。

- [ ] **Step 1: 写失败 HTTP 测试。** 在 SSO 开启和关闭的认证配置下，合法 Source Bearer token 均可进入 push 服务并成功提交；缺失/旧 token、跨 KS/source token、仅有 viewer/editor/admin 会话 cookie、现有只读 API Bearer/JWT token、空 key、过大文件均拒绝且不写 Blob；合法请求按 source/key 两次重试同 SHA 不产生新版本，内容变化新增一个版本并排解析；非 `api` kind 拒绝 push；普通 Source DTO 不含 token。在 `SourceConnectorTests` 中增加独立 PostgreSQL 连接的 push 与真实 Source 删除交错测试：两种 Source 锁先后顺序下都无死锁，删除先完成时 push 不写入文档或绑定。运行 `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~SourcePushTests` 和 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~SourceConnectorTests`，预期失败。
- [ ] **Step 2: 实现 token 限域与单条入口。** 仅在 `SourcePushController` 的 documents/statements 两个 push 动作上标注 `[AllowAnonymous]`，不在控制器或管理路由全局开放匿名；无论默认认证把 `Bearer` 分派给 JWT 还是 Cookie，动作均从 Authorization 头独立解析 Bearer Source token，调用 `SourceService.VerifyAsync`，拒绝 cookie、用户 JWT 和只读 API token 代替它，不从 query/body 取 token。每次提交前验证 token 绑定的 KS/Source 与 kind=`api`，按上限读入流，再复用 coordinator 的来源 key upsert/Blob/版本/解析 job 事务；预先验证后仍须在写事务中先锁 Source 并复核其存在及 token 状态，再锁 Document，删除已完成时不得继续写入。401/403 不区分不存在的 SourceId 细节，入口应用速率和体积限制且禁止响应缓存。`api` 注册为可创建被动 kind，但手动/定时 sync 返回 400；不提交 token 到审计或异常消息。
- [ ] **Step 3: 验证。** 重跑 Step 1 的两个测试命令；再运行 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~SourceSyncTests`，期望通过；仅暂存本任务文件并提交 `feat(sources): accept scoped api document pushes`。

### Task 7: 结构化 statements push 与总体验收

**Files:** Modify `src/ISEStudio/Controllers/SourcePushController.cs`, `src/ISEStudio/Sources/SourcePushService.cs`, `src/ISEStudio/Sources/SourceAdapterRegistry.cs`, `src/ISEStudio/Ontology/ABoxService.cs`, `src/ISEStudio/Ontology/ABoxManager.cs`, `src/ISEStudio/Ontology/RdfImportService.cs`, `src/ISEStudio/Ontology/ReleaseService.cs`, `src/ISEStudio/Ontology/IRdfStatementRepository.cs`, `src/ISEStudio/Ontology/PostgresRdfStatementRepository.cs`, `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs`, `Configurations/EntityConfigurations.cs`, `src/ISEStudio.Tests/Sources/SourcePushTests.cs`, `src/ISEStudio.IntegrationTests/Ingestion/SourceConnectorTests.cs`; Create `src/ISEStudio/Infrastructure/Persistence/Entities/SourceStatementEntities.cs` and EF migration; review existing provenance API before implementation.

**Interfaces:** `POST /api/knowledge/{id}/ingestion-sources/{sourceId}/statements` 只允许 kind=`statements` 且 token 属于该 Source；JSON 中每条含稳定上游 id、subject/predicate/object 和 datatype/language（按现有图谱模型校验）。同一 source/id 同内容重试不重复写事实；同 id 不同内容返回 409，不隐式替换/撤回事实；`statements` 不创建 `DocumentEntity`。

- [ ] **Step 1: 写失败 PostgreSQL 与权限测试。** 合法三元组写入当前 KS 的 ABox 层并保留来源/溯源，可在现有图谱读接口见到；预置同 KS 中其他 graph 的人工事实和本 graph 的无关事实，push 后及并发人工 ABox 编辑、RDF merge 导入后均不得丢失；使用两个独立 PostgreSQL 连接和屏障交错 push 与 merge，验证两方提交后的事实均存在。重复 source/id 同 payload 不增事实或关联，同 id 不同 payload 返回 409；两个 Source 推送同一事实时事实只出现一次但保留两条来源关联；删除 Source 后事实、来源快照及其他 Source 关联仍可读。不同 KS token、仅有用户会话或其他 Bearer token 均不能写，SSO 开/关的合法 statements Source Bearer token 均可用；非法 RDF/谓词被拒绝且本条不入库；模拟图谱写入之后、来源记录写入之前抛异常及并发同 id 重试，均不得留下孤立事实/记录；无普通 Document/Blob/chunk/file-version 行生成；audit 仅记录数量/SourceId。运行 `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~SourceConnectorTests`，预期失败。
- [ ] **Step 2: 接入现有图谱写路径。** 新增 `SourceStatementEntity { Id, KnowledgeSystemId, SourceId?, ExternalStatementId, PayloadSha256, FactKey, SourceNameSnapshot, CreatedAt }`，唯一 `(SourceId, ExternalStatementId)`（SourceId 非空），Source FK `SetNull`、KS FK Restrict；另建按 `SourceStatementId` 关联 `FactKey` 的来源关系（唯一 `(SourceStatementId, FactKey)`），可按 KS/FactKey 查询多来源，不覆盖现有 `AboxProvenanceEntity` 的单 FactKey 行。规范化 RDF term 后对 payload 计算稳定 hash；同 id/hash 返回已有结果，不同 hash 返回 409。先验证 token、kind、KS 及图谱领域约束，再在 PostgreSQL 同一事务中先锁 Source 并复核存在性及 token，再取得该 KS 的 ABox 写锁并完成图谱写入串行化（锁 KS 行）；删除先持 Source 锁时 push 不得继续写入。扩展 `IRdfStatementRepository`/`PostgresRdfStatementRepository` 提供同 DbContext/事务中的单事实 append-if-absent 操作；现有 `ABoxManager.AddStatements` 经 `ReplaceLayerAsync` 整层替换的路径不能用于 push，因为它会删除同 KS 其他 graph 的事实。所有可能写 ABox 的路径（包括人工增删、`RdfImportService` 的 merge/replace、`ReleaseService` 的恢复，以及其他调用 `ReplaceLayerAsync` 的 ABox 入口）必须在读取旧层状态之前取得相同 KS 写锁，并在同一事务内完成读-改-写；显式 replace/恢复按原语义替换，不将并发 push 意外覆盖为陈旧快照。保留非当前 graph 和无关事实，并在事务中保存摄入记录、来源关联和审计，不得先独立提交图谱再写幂等记录。唯一键冲突时回滚并重读后按 hash 返回幂等成功或 409；图谱写入、关联、审计任一步失败均回滚，重试能重新提交。单条失败不影响已经提交的其他条目；不直接拼接 SPARQL。`statements` 注册为被动 kind，不纳入 Source worker 或缺失对账。
- [ ] **Step 3: 验证阶段验收。** 重跑 Step 1、`dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter 'FullyQualifiedName~SourceAdapterTests|FullyQualifiedName~SourceNetworkPolicyTests|FullyQualifiedName~SourcePushTests'` 和 `dotnet build src/ISEStudio.sln --no-restore`；确认 `GET /ingestion-sources/kinds` 包含已完成的 kind 且不含 `azure_blob`，测试均应通过。只提交本任务文件/迁移，提交 `feat(sources): ingest structured source statements`。

## Self-Review

- 本轮注册 `url/rss/custom/github_issues/jira_issues/s3/gcs/webdav/notion/api/statements`；普通文档一律走统一 upsert/版本/解析任务，statements 只走图谱写入；`azure_blob` 的 SDK 与连接器均延期。
- URL/RSS/custom/工单/WebDAV/Notion 的出站请求同受 DNS/重定向/超时/体积限制；云 SDK 不接受用户自定义 endpoint。单页/单项失败不能被误判为完整扫描。
- 接口使用 `ingestion-sources`，与现有图谱来源 `/sources` 并存；凭据只在受限服务端解封，普通响应和审计不泄密。
