# ISEStudio 知识摄入来源设计

## 目标

为 ISEStudio 增加基于 Source 的摄入能力。Source 的领域模型以 Utopia
`crates/utopia-store/src/sources.rs` 和 `crates/utopia-core/src/models.rs` 为主要参考；
`migrations/0002_ingest.sql` 用于理解 schema 演进，`crates/utopia-ingest` 用于参考
内容解析。生产实现仍保留在现有 .NET/PostgreSQL 应用边界内。

Source 描述摄入配置、来源身份、同步状态和历史；它不等同于文档的虚拟 Folder。
当前 Utopia 中可手工创建的 kind 包括 `folder`、`url`、`rss`、`custom`、
`github_issues`、`jira_issues`、`s3`、`azure_blob`、`gcs`、`webdav`、`notion`、
`api` 和 `statements`。本轮不实现 `azure_blob` 连接器或引入 Azure SDK，因此该 kind
不开放创建、同步或前端选择；内部 `memory` 与兼容遗留 `upload` 也不开放创建；当前模型
不支持 `watch_folder`。

## 范围与非目标

本功能包含：

- 在 KS 范围内创建、查询、更新、同步和删除 Source；
- 支持当前 Utopia 可创建 SourceKind 的模型与连接器边界，但本轮暂不交付 `azure_blob`；
- 文档 `source_id`、`external_key` 与既有虚拟 `folder` 字段之间的身份规则；
- Source 同步调度、持久运行记录、文档版本和解析任务派发；
- Source 管理界面及 KS 范围 API；
- 连接器所需的凭据封存、网络访问限制和操作审计。

Utopia Rust crate 仅作为解析和规范化行为的参考，不通过 FFI 接入，也不作为 sidecar
运行。运行时继续使用现有 .NET `IDocumentParser`、Blob 存储、文档版本机制、图谱服务和
持久任务基础设施。本设计不重构 RDF 抽取、本体治理或发布流程。

## Source 与 Folder 模型

Source 是 KS 下独立的摄入容器或连接器配置，不包含 `FolderPath`，也不对应某个文档
Folder。每个 KS 创建时自动创建一个默认 `folder` Source；一个 KS 可有多个 `folder`
Source。手动上传默认选中该默认 Source，也可显式选择其他 `folder` Source。为保留
Utopia Source 模型，不额外持久化 `is_default`；默认目标是创建时生成的第一个 folder
Source，后续默认选择按创建时间最早的现存 folder Source 计算。

文档自己的虚拟 `folder` 路径继续独立保存；创建、移动或删除虚拟 Folder 不创建、移动或
删除 Source。Source 管理和 Folder 导航是两个不同维度。手动上传只能指定 kind 为
`folder` 的 Source；Source 参数省略时使用默认 folder Source。孤立文档或删除 Source
后解除关联的文档允许 `source_id = NULL`。

Source 字段遵循 Utopia 模型：KS ID、kind、name、kind 专属 JSON `config`、可选 icon、
互斥的 `sync_interval_minutes` / `sync_cron`、最近同步时间/状态/错误/新增数量、可选
ingest token 和创建时间。Source 列表投影还提供文档数、缺失文档数：手动 folder 来源按
文档主归属计数，同步来源按去重后的绑定 DocumentId 计数，缺失数按该 Source 缺失的
绑定计数，不用共享文档的主归属/文档级缺失代替。另有 RSS 全文补全摘要（仅 RSS 适用）。Source 详情按 kind 白名单返回非敏感配置值供编辑预填，列表保持精简；URL 类型配置值须先拒绝 userinfo，query 参数默认拒绝，仅连接器显式列为非敏感且校验过的参数可保存并回显；令牌、签名和其他凭据必须走独立的封存字段，不能藏在可回显 URL 中。配置中的凭据只进不出：普通响应完全省略密钥字段，PATCH 省略的密钥保留原密文，只有显式替换或清除才修改；需查看
的 API push token 通过受限专用接口获取。

| kind | 语义 |
| --- | --- |
| `folder` | 被动容器；接收手动上传，不执行自动拉取。 |
| `url` / `rss` | 拉取网页或 feed，并按来源内稳定身份更新文档。 |
| `custom` | 调用符合 Utopia ingest 契约的自定义抓取器，按其 items 结果摄入。 |
| `github_issues` / `jira_issues` | 拉取工单及其状态变更，形成可追踪文档。 |
| `s3` / `gcs` | 从对象存储同步对象。 |
| `azure_blob` | 保留模型标识；本轮不实现连接器、Azure SDK 集成或创建/同步入口。 |
| `webdav` / `notion` | 从对应外部服务同步内容。 |
| `api` | 接收带 source-scoped token 的文档推送。 |
| `statements` | 接收结构化陈述并进入图谱写入流程，不创建普通文档。 |

kind 清单由一个共享 `SourceKind` 枚举驱动创建校验、同步分派和前端选项，仅注册并展示
已实现的 kind，不另维护彼此可能漂移的手写 allowlist。`memory` 是 KS 内部来源，不可手工创建或删除；`upload`
仅用于旧数据兼容，新建手动上传容器使用 `folder` kind。当前部署不支持读取服务器本地
目录：Utopia 已否决 `watch_folder`，自部署环境应通过对象存储、WebDAV、Notion 等连接器
接入内容。

## 文档身份与版本

文档的 `SourceId` 与虚拟 `Folder` 独立：`SourceId` 可空，文档可在不改变来源的情况下
移动 Folder。文档上的 `SourceId`/`ExternalKey` 仅表示首次归属，不能表达跨 Source
共享文档的全部身份；同步身份以独立 `SourceDocumentBindingEntity` 为准，每个
`(SourceId, ExternalKey)` 唯一并指向一个文档。手动上传的文档 `ExternalKey` 为 NULL，
不创建绑定，也不用文件名构造 source identity。URL 使用规范 URL，RSS 使用 feed GUID
或条目链接，custom/工单/对象存储/WebDAV/Notion 使用上游稳定 ID，API 使用调用方提供
的非空 key。

整个 KS 仍按 `(KnowledgeSystemId, Sha256)` 去重。上传或同步遇到已有的同内容文档时不再
创建文档行；同步命中已有 SHA 时为该 Source/key 新建或保留指向该文档的绑定，不改变
已有文档的归属字段。同一绑定再次出现且 SHA 未变时不变更文档。内容变化时，若该绑定
独占文档、`IsManualUpload` 为 false 且文档的 `SourceId` 是该 Source，则原地更新并递增原始文件版本；若文档还被
任何其他绑定引用（含同 Source 不同 key），或其 `SourceId` 属于其他 Source/手工上传，则按新 SHA 查找
已有 KS 文档或新建文档，并在事务中将该绑定改指向目标，不修改旧共享文档。原地更新
遇到 KS 中另一文档已占用新 SHA 时，也改绑到该文档。绑定改指后，若旧文档原本的
`SourceId`/`ExternalKey` 指向该绑定，则将这两个归属字段置空，避免留下失效的主身份；
旧文档已无绑定时清空文档级 `MissingSince`。
手动上传命中同步文档的 SHA 时设置 `IsManualUpload = true` 并清空文档级 `MissingSince`，
保留已有绑定与主归属。文件名不是手动上传的身份键，同名但内容不同的手动文件遵循现有
上传语义创建新的文档行。

ISEStudio 当前 `DocumentVersionEntity.ContentSha256` 是解析后纯文本的哈希，`ChunkCount`
和 `DocumentVersionChunkEntity` 保存的是解析快照；它不等价于 Utopia 的原始文件版本。
因此保留现有 `DocumentVersionEntity` 及 chunk 表的解析快照职责，新增
`DocumentFileVersionEntity`，参考 Utopia `crates/utopia-store/src/documents.rs` 的
`document_versions`：记录文档 ID、按文档单调递增的 `Version`、原始文件 SHA-256、字节数、
可空 `DocTime` 和 `CreatedAt`。`DocTime` 表示文档自身时间，不用摄入时间代替；文件字节
仍由内容寻址 Blob 保存，版本行通过 SHA-256 引用。文件版本与解析快照通过关联实体建立
多对多关系：不同原始版本可能产生相同的解析文本，因此继续按现有规则复用
`DocumentVersionEntity`，并让多个文件版本关联同一解析快照。无法关联某个旧文件版本的
历史解析快照继续保留，不伪造关联。

创建文档时，在同一事务中写入第 1 个文件版本并排队解析；同一来源 key 且原始文件 SHA
未变时不新增文件版本；独占文档内容变化时原地更新并写入下一个文件版本，共享文档
内容变化时改绑到新建或已有文档（新建文档的文件版本从 1 开始）。仅对新建或原地更新
的文档重新排队。原始文件版本先于解析任务持久化，因此解析失败不会丢失已摄入文件。解析成功后仍通过现有
`DocumentVersionStore` 写入解析后纯文本及 chunks，并建立文件版本到解析快照的关联；
同一解析快照可供多个原始版本复用。
手动上传保持 ISEStudio 现有显式解析流程，但也应先记录原始文件版本。手工解析在同一事务中锁定并重读文档，按最高 `Version` 取得当前文件版本 ID 并校验其 SHA 与文档当前 SHA 一致，读取该版本的 Blob；解析快照存储复用调用方事务，结果只关联该版本 ID，不独立提交部分结果。即使文件内容经历 A→B→A、两个 A 共用同一 SHA，也不得按 SHA 反查版本；解析期间当前版本变化时不得将旧结果写成当前解析状态。
由原始文件触发的持久解析任务在入队时固定文件版本 ID 和 SHA；延迟执行时从该文件版本的 SHA 读取并校验 Blob，不得用文档当前 SHA 拒绝旧版本任务，也不得按当前文档 SHA 查找最新版本。过期任务可记录自身解析快照与状态，只有当前文件版本的任务可更新文档级解析状态、元数据和 chunk 计数；过期任务失败不把当前文档标为 failed。旧任务缺少版本 ID 且同 SHA 存在多个版本时不伪造解析快照关联。

仅在连接器能够证明完整枚举来源时，才把成功同步中未再出现的该 Source 的绑定标记
`MissingSince`；部分失败、取消或分页不完整时不标记其他绑定缺失。已成功提交的重现条目
即使本轮稍后失败，也在逐条事务内清除其绑定缺失标记，并重算关联文档的缺失状态。
文档上的 `MissingSince` 只在至少有一条绑定、所有绑定都缺失且 `IsManualUpload` 为 false 时设置；
任一绑定重现即清空。对同一文档的缺失对账、条目重现及 Source 删除须在事务中先锁操作所属的 Source 行，再按 ID 锁定并重读受影响文档、全部绑定并重算文档状态；不同 Source 分别持有自己的 Source 锁，但争用同一文档锁以串行化重算。删除后无绑定时清空缺失标记。手动上传被同步来源复用也不变成缺失文档。
缺失文档及其图谱/溯源引用均保留。
Blob 由手工上传、同步写入、文档删除及失败回收共享。所有路径须在按 SHA 的跨进程互斥边界内完成引用变更与回收判断，避免另一笔尚未提交的写入被误判为无人引用；上传、同步和 API 文档 push 的事务先锁定并复核所属 Source，再按 ID 锁定并重读现存 Document，最后取得 SHA 锁。Source 删除遵循 Source 后 Document 的顺序；仅删除 Document 时无需锁 Source，先锁 Document 再锁 SHA。绝不持 SHA 锁等待 Document 行锁；锁后发现新的目标文档则释放并重试。删除前在互斥边界内重查全部文档和原始文件版本引用。失败项只能在事务回滚后持锁复查并回收，不能在锁外仅凭一次引用查询直接删除 Blob；崩溃留下的孤立对象允许待后续受控清理，不以牺牲引用安全换即时回收。
虚拟 Folder 可独立移动文档；移动只更新文档 `Folder`，不改 `SourceId` 或
`ExternalKey`，后续同步仍按来源身份更新原文档。

## 持久化与迁移

增加 Source、SourceSyncRun、SourceDocumentBindingEntity、DocumentFileVersionEntity
及对应 EF 映射。Source 行参照
Utopia，包含 `id`、`kb_id`、`kind`、`name`、`config`、计划、最近同步摘要、图标、token
和创建时间；不增加 Folder 路径字段。Source 上的最近同步状态允许
`never/queued/running/ok/failed`；独立运行记录仅使用 `running/ok/failed`，并记录起止
时间、新增/更新数和错误。每个 Source 仅保留最近 50 条运行记录。删除 Source 对文档外键
使用 `ON DELETE SET NULL`，对同步历史使用级联删除。

Document 保留现有 KS 级 `(KnowledgeSystemId, Sha256)` 唯一约束，新增可空
`SourceId`、`ExternalKey`、`MissingSince` 和非空 `IsManualUpload`（迁移的旧文档与手动上传
为 true，仅同步新建为 false），并添加部分唯一索引
`(SourceId, ExternalKey) WHERE ExternalKey IS NOT NULL`。此索引仅约束可空的主归属
字段，不作为同步身份查询入口。绑定表具有 Source FK 级联、Document FK 限制删除
（删除文档前在同一数据库事务中清理绑定；来源仍存在时下次完整扫描可重新摄入）、
唯一 `(SourceId, ExternalKey)`、按 DocumentId 查询的索引以及
绑定级 `MissingSince`。Source 与绑定指向的 Document 必须属于同一 KS，由协调器在写入
事务中验证；同内容不会跨 Source 产生独立文档行。同步 upsert/改绑先锁定并复核所属 Source，再锁定并重读现存目标 Document，最后操作绑定；仅删除文档时先锁 Document 并清除绑定，不能在清完绑定与删 Document 之间允许新绑定插入。需同时锁多个 Document 时按 ID 排序，遇到被删文档按唯一约束/外键冲突重试条目或返回明确结果，不遗留孤立绑定。

迁移为每个 KS 创建一个默认 `folder` Source，并把现有 KS 文档关联到该默认 Source；保留
现有 `Folder`、SHA-256、Blob 和解析版本，手动上传文档的 `ExternalKey` 保持 NULL。已有
孤立文档继续保持 `SourceId = NULL`。迁移不复制 Blob，不合并或删除文档。旧版 config
或 token 中的明文凭据在配置加密密钥可用时迁移为封存格式。

## 同步架构

Source 操作按 KS 隔离，并遵循现有授权与服务约定。操作包括来源列表、详情、创建/配置、
删除、手动触发、同步历史，以及 token 轮换和受限查看。Editor 可管理或触发 Source；
Viewer 可读取来源配置和状态。只有 Editor/Owner 可以查看 token 明文；查看操作需审计。
轮换 token 后，旧 token 立即失效。

每种 kind 使用独立 adapter 发现来源条目，并返回规范化的外部文档信息：external key、
文件名、MIME type、字节流和可用的来源时间戳。同步协调器负责绑定级 source-key upsert、原始
Blob 版本记录、缺失条目对账、运行计数和解析任务派发；不自行实现格式解析。

手动上传继续遵循现有的显式解析流程。定时/手动来源同步和 API push 发现的新文档或变更
文档，会自动派发到现有持久解析任务。解析状态与来源同步状态相互独立。

同步通过持久化 job 排队；Source 的 `last_sync_status` 可为
`never/queued/running/ok/failed`。worker 开始执行时创建 source-run 行，运行状态仅为
`running/ok/failed`，并记录时间戳、新增/更新数量及错误详情。专用 Source worker 原子
认领排队任务，并阻止同一 Source 同时运行多个同步；不同 Source 可在配置的 worker 并发
限制内并行执行。定时运行支持互斥的 interval 或标准五段 cron 配置；两者均未设置时仅
手动运行。每个 Source 仅保留最近 50 条运行历史。

每个条目独立提交，单个 URL 或文件失败不回滚其他条目的成功结果。部分条目成功、部分
失败时，保留成功的数据和计数，将本次 run 标记为 `failed` 并记录条目错误。按
source/external key 重试时保持幂等。只有发现和所有条目操作均无错误时，run 才标记为
`ok`。仅完整成功的扫描可以把先前已见条目标记为缺失；部分或失败的扫描绝不进行缺失
条目对账。

`statements` push 不创建 Document：写事务先锁定并复核所属 Source 和 token，再取得 KS 的 ABox 写锁；每条上游 statement id 在 Source 范围内唯一，
第一次写入时保存 payload hash、KS、规范事实键与来源快照；同一 id 同内容重试不增加
图谱事实或来源记录，同一 id 不同内容返回 409（本轮不支持用同一 id 更新/撤回事实）。
图谱 ABox 事实与按 Source/item id 的事实来源关联在同一 PostgreSQL 事务中写入；
所有写 ABox 的入口（含人工编辑、RDF merge/replace 导入及 Release 恢复）须在读取旧状态前取得相同的 KS 写锁，并在事务内完成写入；merge 不得用过期快照覆盖并发 push，显式 replace/恢复保留原有替换语义。
多个 Source 声明相同事实时只存一条事实、分别保留来源关联，不覆盖已有按 FactKey
单行的人工/抽取溯源。删除 Source 后保留图谱事实与来源快照，后续无法再以该 Source
推送。失败/取消时事务回滚图谱写入、摄入记录与来源关联，重试可再次提交。

## 安全与失败边界

- 凭据使用 AES-256-GCM 封存，密钥由部署环境配置；未配置密钥时非机密 folder 操作可用，封存配置、token 接口及需要凭据的连接器失败关闭，非空密钥格式或长度无效时启动失败；普通 Source DTO、日志和审计详情不得
  暴露凭据。API ingest token 只通过受限专用接口查看或轮换，查看/轮换要求 Editor/Owner
  权限并审计。
- API 与 statements push 的动作单独允许匿名进入，由服务端从 Authorization Bearer 头校验
  对应 KS、Source 和 kind 的 Source token 后才能写入；即使启用 SSO，默认 JWT/Session 认证
  成功、已有只读 API token 或用户 cookie 均不能代替 Source token。其余 Source 管理及 token
  查看/轮换路由保持 KS 用户角色授权，不随 push 动作开放匿名。
- URL/RSS adapter 仅允许 HTTP(S)。默认拦截 loopback、link-local 和私有地址；运维显式
  配置允许 CIDR 后才可访问相应网段。解析并校验每个目标地址，且对每次重定向重新校验，
  防止跳转到不允许的网络。带凭据请求只允许同 `(scheme, host, port)` origin 的重定向保留凭据；
  禁止 HTTPS 到 HTTP 的降级，也不得将 Authorization、Cookie 等机密头转发到其他 origin。
- 可回显的 URL 配置字段在创建与 PATCH 时拒绝 userinfo 和未获连接器非敏感参数白名单允许的 query；已列白名单的参数也须校验值不含凭据，默认白名单为空。上游条目链接不属于可编辑的 Source 配置，不据此改变其稳定 key 语义。
- WebDAV 的 PROPFIND 与文件 GET 走同一受控 HTTP 客户端和逐跳网络策略，不向 adapter 暴露
  可绕过策略的原始客户端；PROPFIND 重定向不得隐式改写成 GET。
- 受控 GET/PROPFIND 接收受限请求选项：Authorization/Cookie 仅从服务端封存配置解密后提供，
  普通请求头只允许 Accept、If-None-Match、Notion-Version；Host、Forwarded、Depth 等非白名单
  头不得由 adapter 设置，PROPFIND 的 Depth 由受控客户端校验。GitHub/Jira token 与 Notion
  固定版本头均通过该入口发送；逐跳重定向不得把凭据转发到其他 origin 或降级地址。
- Adapter 设置超时、响应大小、内容类型、重定向次数和最大条目数限制，以约束不可信输入。
  取消任务时将 run 标记失败，且不把未出现的条目标记为缺失。
- 当前文件版本解析失败记录在文档及解析任务上；过期文件版本的失败只记录在其解析任务上，不改变当前文档或已成功的来源同步 run 状态。
- 删除 Source 将关联文档的 `SourceId` 置空；文档、Blob、图谱事实、Folder 和溯源记录均
  保留，Source 的绑定与同步历史级联删除。删除须先与同 Source 的 job 入队/认领协调，
  禁止已删除 Source 的 worker 继续写入。

## 用户界面

文档工作区保留现有 Folder 导航，并增加独立的 Source 管理入口，可查看 Source kind/状态、
配置、立即同步、最近运行记录和 token 管理。文档上传控件提供 folder Source 选择器；默认
选中 KS 默认 Source。虚拟 Folder 导航继续由文档 Folder 字段驱动，与 Source 列表相互
独立。文档移动只改变 Folder，不因文档是否由 Source 同步而禁用。

## 测试与验收

满足以下条件后，功能方可验收：

- 迁移测试验证每个 KS 默认 folder Source 创建、现有文档关联、原 Folder/SHA/Blob/解析
  版本保留，以及孤立文档保持未关联；现有 DocumentVersionEntity 和 chunks 作为解析快照
  原样保留，不伪造其与历史原始文件版本的关联；可从当前 Document 元数据识别的文件版本
  回填到 DocumentFileVersionEntity；
- PostgreSQL 集成测试验证 Source 字段/计划约束、绑定级 external key 唯一性、KS 级 SHA
  去重、跨 Source 共享后的分叉/改绑、仅所有绑定缺失才标记文档缺失、Source 删除置空
  关联及绑定/同步历史级联、DocumentFileVersionEntity 的初始版本/递增/
  原始文件元数据、多对多解析快照关联，以及解析快照的 chunks；
- Adapter 测试覆盖本轮已注册的可创建 kind 的配置校验及分派；URL/RSS/custom/工单/
  S3/GCS/WebDAV/Notion/API 的稳定身份；statements 的结构化图谱写入；`azure_blob`
  不注册 adapter，创建与同步均被拒绝；
- source-run 测试覆盖幂等重试、内容变更、完整成功时标记缺失、失败时不标记缺失、部分
  失败计数、最近 50 条历史修剪、调度和并发认领；
- 授权测试覆盖 viewer/editor/owner/admin，并确保普通 DTO 和日志中不出现 token；
- 安全测试验证 AES-256-GCM 凭据封存、普通 DTO 不泄密、token 查看/轮换授权和审计、不
  允许的 CIDR 与跳转到被拦截地址的重定向；
- UI 测试覆盖独立 Source 管理和 Folder 导航、默认 Source 上传、可选其他 folder Source、
  同步状态/历史及独立移动文档；
- 现有上传、解析、文档历史和抽取流程继续通过测试。

## 已确认决策

- Source 与虚拟 Folder 是独立维度，一个 KS 可以有多个 Source 和多个 Folder。
- 每个 KS 创建一个默认 `folder` Source；手动上传默认使用它，也可选择其他 folder Source。
- 手动上传文档的 `external_key` 为 NULL；同步文档用上游稳定 key。
- Source 删除时保留文档，仅将其 Source 关联置空；来源绑定和同步历史随 Source 删除。
- 同一 KS 的 SHA-256 去重约束保持不变；跨 Source 命中相同 SHA 时复用已有文档，不新建
  文档行，也不覆盖已有文档的来源归属；各 Source/key 保留独立绑定，发生内容分歧时
  将变更方的绑定改指向新建或已有文档，不覆盖仍被其他绑定引用的文档。
- DocumentFileVersionEntity 记录不可变原始文件版本；现有 DocumentVersionEntity 继续记录
  解析文本/chunk 快照，并关联到可识别的文件版本。相同来源 key 的独占文档原地更新
  并递增文件版本，共享文档改绑。
- 来源中消失的文档予以保留；仅完整成功扫描后才标记缺失。
- 文档可独立移动虚拟 Folder，不改变来源身份。
- 运行时沿用 ISEStudio .NET parser；Utopia Rust crate 仅作参考，不作为运行时依赖。
- `azure_blob` 的模型标识保留，但本轮不引入 Azure SDK 或实现连接器；注册表、API 和
  前端均不得把它当作可创建或可同步的 kind。

用户审阅并批准本 spec 后，再编写实施计划。
