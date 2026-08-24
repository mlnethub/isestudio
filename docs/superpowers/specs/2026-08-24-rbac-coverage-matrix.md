# RBAC Coverage Matrix

**Status**: 已完成(5 个实施 commit + 本文档收尾 commit)
**Date**: 2026-08-24
**Branch**: `dotnet`

## 角色模型

- **Anonymous** — 无 cookie / token
- **Viewer** — 显式 `viewer` role grant;只读
- **Editor** — 显式 `editor` role grant 或 KS owner;内容修改
- **Owner** — KS owner;manage / delete + editor/viewer
- **Admin** — `User.IsAdmin=true`;全程有效

## 决策链

| 层 | 现状 |
|---|---|
| Authentication | 3 scheme: `Session` / `ApiBearer` / `ExternalToken` |
| Authorization | `[KSRoleAuthorize(Minimum = KSRole.X)]` attribute + `Policies.AdminOnly` / `Policies.KSOwnerOnly` |
| Service guard | 8+ 处 `RequireRoleAsync` 保留作 belt-and-suspenders(与 filter 共存,双保险) |

**错误文案**(`KSRoleAuthorizeAttribute` 统一输出,对齐 Python baseline `permissions.py`):

| 场景 | 响应 |
|---|---|
| 未认证 | 401 `{"detail": "Not authenticated"}` |
| 已认证但无任何 grant | 403 `{"detail": "You don't have access to this knowledge system"}` |
| 角色低于 Minimum | 403 `{"detail": "Insufficient permissions"}` |
| KS 不存在 | 404 `{"detail": "Knowledge system not found"}` |

## 实施 commits(实际 5 个实施 commit,非 design spec §9 计划的 3 个)

| Commit | 内容 |
|---|---|
| `0975b69` | `docs(rbac)`: 设计文档(本切片 design spec) |
| `b1ac163` | `feat(rbac)`: `KSRoleAuthorizeAttribute` filter + 单元测试(Task 1) |
| `c32445f` | `feat(rbac)`: 12 个 dispatch controller 挂 `[KSRoleAuthorize]`(Task 2) |
| `c949e44` | `feat(rbac)`: `AddPolicy` `AdminOnly` + `KSOwnerOnly`,退役内联 `Roles="Admin"`(Task 3) |
| `a6596ef` | `test(rbac)`: endpoint×role 期望矩阵钉为测试资源 JSON(Task 4) |
| `a0b01e4` | `test(rbac)`: `EndpointRoleMatrixTests` 驱动 `rbac_matrix_expected.json`(Task 5) |

测试规模: **103 行 × 5 actor**(anonymous / viewer / editor / owner / admin),共享 host + 每 actor 独立世界(独立 KS + grant + 一个 seed 文档);期望值 source-of-truth 为 `src/OnToPilot.Tests/Authorization/rbac_matrix_expected.json`(embedded resource)。

## 矩阵(端点 × 角色,实测校准)

> 本表是 **calibrated actuals** — 由 `EndpointRoleMatrixTests` 对运行时真实行为校准(84 cells / 32 行相对初版修正),**不是** design spec §6 的愿景表。任何 PR 改动 controller 的 role 契约,必须同步改 `rbac_matrix_expected.json` 并被显式 review。
>
> 测试探针约定: GET 不带 body;POST/PUT/PATCH 发空 JSON `{}`;DELETE 发空 JSON 对象;`/rdf/import` 发空 multipart。`{id:guid}` / `{user_id}` / `{document_id:guid}` 是真实 seed 实体,其余 `{cid}` / `{rid}` / `{did}` / `{job_id}` / `{event_id}` / `{res_id}` / `{release_id}` / `{proposal_id}` / `{token_id}` / `{prompt_key}` / `{filename}` 未 seed(落到 not-found / 空 body 行为)。

| Method + Path | Anon | Viewer | Editor | Owner | Admin |
|---|---|---|---|---|---|
| `POST /api/knowledge/{id:guid}/abox/assertions` | 401 | 200 | 500 | 500 | 500 |
| `POST /api/knowledge/{id:guid}/abox/assertions/delete` | 401 | 403 | 500 | 500 | 500 |
| `GET /api/knowledge/{id:guid}/abox/classes` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/abox/individual` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/abox/individuals` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/abox/individuals` | 401 | 200 | 500 | 500 | 500 |
| `POST /api/knowledge/{id:guid}/abox/individuals/delete` | 401 | 403 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/abox/reset` | 401 | 403 | 500 | 500 | 500 |
| `GET /api/knowledge/{id:guid}/abox/validate` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/abox/validate/fix` | 401 | 403 | 500 | 500 | 500 |
| `GET /api/knowledge/{id:guid}/validation/decisions` | 401 | 200 | 200 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/validation/decisions/{did}` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge` | 401 | 400 | 400 | 400 | 400 |
| `DELETE /api/knowledge/{id:guid}` | 401 | 403 | 403 | 200 | 200 |
| `GET /api/knowledge/{id:guid}` | 401 | 200 | 200 | 200 | 200 |
| `PATCH /api/knowledge/{id:guid}` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/members` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/members` | 401 | 403 | 403 | 400 | 400 |
| `GET /api/knowledge/{id:guid}/members/candidates` | 401 | 400 | 400 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/members/{user_id}` | 401 | 403 | 403 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/members/{user_id}/detail` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/review/counts` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/refresh_stats` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/conflicts` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/conflicts/detect` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/conflicts/{cid}` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/conflicts/{cid}/dismiss` | 401 | 403 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/conflicts/{cid}/reopen` | 401 | 403 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/conflicts/{cid}/resolve` | 401 | 403 | 500 | 500 | 500 |
| `GET /api/knowledge/{id:guid}/reconciliations` | 401 | 200 | 200 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/reconciliations/{rid}` | 401 | 403 | 200 | 200 | 200 |
| `PATCH /api/knowledge/{id:guid}/reconciliations/{rid}` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/documents` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/documents/page` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/documents/parse-batch` | 401 | 403 | 500 | 500 | 500 |
| `POST /api/knowledge/{id:guid}/documents/upload` | 401 | 403 | 400 | 400 | 400 |
| `GET /api/knowledge/{id:guid}/documents/{document_id:guid}` | 401 | 200 | 200 | 200 | 200 |
| `PATCH /api/knowledge/{id:guid}/documents/{document_id:guid}` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/documents/{document_id:guid}/chunks` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/documents/{document_id:guid}/contribution` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/documents/{document_id:guid}/delete` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/documents/{document_id:guid}/impact` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/documents/{document_id:guid}/parse` | 401 | 403 | 500 | 500 | 500 |
| `POST /api/knowledge/{id:guid}/extract` | 401 | 403 | 500 | 500 | 500 |
| `POST /api/knowledge/{id:guid}/extract-all` | 401 | 403 | 500 | 500 | 500 |
| `POST /api/knowledge/{id:guid}/extract-instances` | 401 | 403 | 500 | 500 | 500 |
| `GET /api/knowledge/{id:guid}/jobs` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/jobs/{job_id}` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/prompts` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/prompts/restore-all` | 401 | 403 | 204 | 204 | 204 |
| `DELETE /api/knowledge/{id:guid}/prompts/{prompt_key}` | 401 | 403 | 404 | 404 | 404 |
| `PUT /api/knowledge/{id:guid}/prompts/{prompt_key}` | 401 | 403 | 400 | 400 | 400 |
| `GET /api/knowledge/{id:guid}/history` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/history/{event_id}/rollback` | 401 | 500 | 404 | 404 | 404 |
| `GET /api/knowledge/{id:guid}/resolution/decisions` | 401 | 200 | 200 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/resolution/decisions/{res_id}` | 401 | 403 | 200 | 200 | 200 |
| `PATCH /api/knowledge/{id:guid}/resolution/decisions/{res_id}` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/resolution/queue` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/resolution/{res_id}/resolve` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/exports` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/exports` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/exports/{job_id}` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/exports/{job_id}/files/{filename}` | 401 | 404 | 404 | 404 | 404 |
| `GET /api/knowledge/{id:guid}/releases` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/releases` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/releases/diff` | 401 | 200 | 200 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/releases/{release_id}` | 401 | 403 | 403 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/releases/{release_id}/deployment` | 401 | 403 | 403 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/releases/{release_id}/deployment` | 401 | 403 | 403 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/releases/{release_id}/publish` | 401 | 403 | 403 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/releases/{release_id}/review` | 401 | 403 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/releases/{release_id}/rollback` | 401 | 403 | 403 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/rdf/import` | 401 | 403 | 400 | 400 | 400 |
| `GET /api/knowledge/{id:guid}/vocabulary` | 401 | 200 | 200 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/vocabulary/concepts` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/vocabulary/concepts` | 401 | 200 | 200 | 200 | 200 |
| `PATCH /api/knowledge/{id:guid}/vocabulary/concepts` | 401 | 403 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/vocabulary/concepts` | 401 | 403 | 500 | 500 | 500 |
| `GET /api/knowledge/{id:guid}/vocabulary/export` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/vocabulary/proposals` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/vocabulary/proposals/{proposal_id}/accept` | 401 | 403 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/vocabulary/proposals/{proposal_id}/reject` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/vocabulary/resolve` | 401 | 200 | 200 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/vocabulary/schemes` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/vocabulary/schemes` | 401 | 200 | 200 | 200 | 200 |
| `PATCH /api/knowledge/{id:guid}/vocabulary/schemes` | 401 | 403 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/vocabulary/schemes` | 401 | 403 | 422 | 422 | 422 |
| `POST /api/knowledge/{id:guid}/vocabulary/suggest` | 401 | 403 | 404 | 404 | 404 |
| `POST /api/knowledge/{id:guid}/vocabulary/sync` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/ontology` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/ontology/edit` | 401 | 500 | 500 | 500 | 500 |
| `GET /api/knowledge/{id:guid}/ontology/export` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/ontology/reset` | 401 | 403 | 500 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/provenance` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/sources` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/tokens` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/tokens` | 401 | 400 | 400 | 400 | 400 |
| `DELETE /api/knowledge/{id:guid}/tokens/{token_id}` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/tokens/{token_id}/reveal` | 401 | 200 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}/mcp/tokens` | 401 | 200 | 200 | 200 | 200 |
| `POST /api/knowledge/{id:guid}/mcp/tokens` | 401 | 200 | 200 | 200 | 200 |
| `DELETE /api/knowledge/{id:guid}/mcp/tokens/{token_id}` | 401 | 200 | 200 | 200 | 200 |

## 已知分歧(矩阵 ≠ 理想角色契约)

本切片只记录与校准,不改任何 controller 行为。以下为与"角色契约理想态"的偏差,均带 follow-up 指针:

### (a) 3 行 guard-divergence:期望 403,实际 500 / 400

service guard 抛异常(而非返回 403),被 `FastApiErrorMiddleware` 转成 500 / 400:

| 行 | 实际 | 原因 | Follow-up |
|---|---|---|---|
| `POST …/history/{event_id}/rollback` | viewer → **500** | guard 抛 `InvalidOperationException` → 500 信封 | guard 统一抛 403 信封 |
| `POST …/ontology/reset` | editor → **500** | Owner guard 抛 → 500(owner/admin 正常 200) | 同上 |
| `GET …/members/candidates` | viewer/editor → **400** | guard 抛 `ValidationException` → 400 | guard 改 403 语义 |

### (b) tokens / mcp_tokens 7 行无 role gate

任意已认证 actor(viewer 等同 admin)都可通过 — 安全缺口已如实钉在矩阵:

| 行 | 实际 |
|---|---|
| `GET …/tokens` | 200(全 actor) |
| `POST …/tokens` | 400(空 body 校验;同样无 role gate) |
| `DELETE …/tokens/{token_id}` | 200 |
| `POST …/tokens/{token_id}/reveal` | 200 |
| `GET …/mcp/tokens` | 200 |
| `POST …/mcp/tokens` | 200 |
| `DELETE …/mcp/tokens/{token_id}` | 200 |

Follow-up: Python baseline 这些端点 gate 在 Editor(`ks_writer`);需产品决策 + 补 `[KSRoleAuthorize]`。

### (c) ~40 cells(实 42)500 来自 empty-body NRE probe

15 行中 dispatcher 把空 body `{}` 反序列化为 null DTO → NRE → `FastApiErrorMiddleware` 500 信封。矩阵如实记录"通过角色检查的 actor 拿 500",但 empty-body 500 本身是 API 质量缺陷。
Follow-up: "empty-body → 422/400 信封" 加固 ticket。

### (d) 7 行四 actor 同码(角色区分度丢失)

即 (b) 的同一组 token 端点:viewer / editor / owner / admin 四个已认证 actor 状态码完全相同(200,POST tokens 为 400),角色维度对该组端点完全失效。
Follow-up: 同 (b),恢复角色区分。

> 另注: 另有 4 行非 token 端点也出现四 actor 同码,但属 probe 产物或既有软契约,不按角色缺口计 —
> `POST /api/knowledge`(400,空 body)、`POST …/ontology/edit`(500,空 body NRE)、`GET …/exports/{job_id}/files/{filename}`(404,未 seed job_id)、
> `POST …/documents/{document_id:guid}/delete`(200,deliberately 无 attribute 的既有 HTTP 契约;follow-up: 评估 viewer 可删文档是否契约漏洞)。

## 设计要点

- Filter 是 `IAsyncAuthorizationFilter`(不是 policy handler),因为需要 route argument + scoped DI + 路径解析(`{id:guid}` → Guid PK;`publicId` → 字符串,先按 PublicId 查、再按 Guid 兜底)。
- `[KSRoleAuthorize(Minimum = KSRole.Editor)]` 一个 attribute 取代 8+ 处 service guard 复制;service guard 保留作双保险(删除属纯 DRY follow-up)。
- `AddPolicy` 注册 `AdminOnly`(实质生效)+ `KSOwnerOnly`(hook,目前仅 Admin)。
- `rbac_matrix_expected.json` 是 source-of-truth,任何 PR 改动需显式 review;重新校准用 `RBAC_MATRIX_DUMP=<path>` 环境变量进入 record 模式。
- 错误文案严格对齐 Python baseline(见"错误文案"节)。
- 12 个挂 attribute 的 controller: ABox / Conflicts / Documents / Extraction / History / Knowledge / Ontology / Prompts / RdfImport / Releases / Resolution / Vocabulary;`PublishedController` / `ExternalApiController`(token scheme)、`ApiBearerController`、`HealthController`(public)不挂。

## 不在范围(留 follow-up)

- 删除 8+ 处 service `RequireRoleAsync`(纯 DRY 收益,无功能影响)
- `KSRole` ↔ DB 字符串映射统一(跨 schema-3 长周期)
- OpenAPI `x-onto-pilot-roles` 自动生成(需 `IOperationFilter`)
- MCP 通道改造(`McpPrincipalAccessor` 语义已正确,不动)
- token scheme 端点矩阵(`/api/v1/*` 走 token scope 不走 KSRole)
- 未 seed 实体的 404 / 200-envelope 行深化(扩展 seeder 建立更细契约)

## How to apply(新 endpoint 走法)

1. 在 controller action 挂 `[KSRoleAuthorize(Minimum = KSRole.X)]`
2. 在 `rbac_matrix_expected.json` 加对应行(5 actor 期望状态码)
3. PR review 必看 `rbac_matrix_expected.json` 改动行
4. 全量测试 + `EndpointRoleMatrixTests` + ApiContract 必过
5. 契约行为变化时: `RBAC_MATRIX_DUMP=<path>` 跑 record 模式,回写期望 JSON 后提交
