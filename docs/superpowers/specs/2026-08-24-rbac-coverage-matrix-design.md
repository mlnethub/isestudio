# RBAC coverage matrix: 端点 × 角色完整覆盖(.NET 端)

**状态**: 设计阶段(待用户 review)
**日期**: 2026-08-24
**分支**: `dotnet`
**范围**: `Authorization/KSRoleAuthorizeAttribute.cs`(新)+ `Program.cs`(`AddAuthorization` 注入 policy)+ 3 个 controller(`ProvidersController` / `SettingsController` / `AuthController`)+ 测试 harness(新增 `KSRoleAuthorizeFilterTests` + `AdminPolicyTests` + `EndpointRoleMatrixTests`)+ 文档

---

## 1. 背景

OnToPilot .NET 端当前 RBAC 是"两层"架构,跟 Python baseline 不对齐:

| 层 | 现状 | Python baseline |
|---|---|---|
| **外层 — AuthenticationHandler** | 三个 scheme(`Session` / `ApiBearer` / `ExternalToken`),仅做身份认证(401 / 403 envelope) | `app/security.py:current_user` 同样只解 user,无角色 |
| **外层 — Controller `[Authorize]`** | 全 controller 级 `[Authorize]`,**无 role hint** — OpenAPI 不反映权限契约 | 每个 endpoint 直接 `Depends(ks_reader/writer/owner)`,OpenAPI 自动反映 |
| **内层 — Service guard** | 8+ 处 `RequireRoleAsync` 复制(ABoxService / KnowledgeService / DocumentService / PromptService / HistoryService / ResolutionService / OntologyService / OntologyProvenanceService)| 一个 `_require(min_role)` 工厂返回 dep,3 行复用 |
| **MCP 通道** | `McpPrincipalAccessor.RequireRoleAsync`(125 行)独立 decision;已有"实时查库"的正确语义 | 不适用 |
| **`AddAuthorization()`** | `Program.cs:544` 零参注册,**零 policy** | 不适用 |

关键痛点:

1. **DRY 违反** — 8+ 处 service 各写一遍 `RequireRoleAsync`,新增 endpoint 必须手工复制 guard,容易遗漏。
2. **OpenAPI 不可见** — controller 只 `[Authorize]`,前端 / 集成方看不到哪些 endpoint 要 Editor / Owner。
3. **角色字符串散落** — `KnowledgeService.cs:710` 的 `RoleName()` 直接吐 `"viewer"/"editor"/"owner"`,散落 DB 列(`ks_grants.role`),**无集中 enum↔string 映射**,易拼写错。
4. **无端点 × 角色矩阵测试** — `KnowledgeSystemAccessTests` 只测 service 层,HTTP 层唯一 RBAC 测试是 `AuthAdminApiTests`(admin × settings/users,4 个端点 × 2 个 actor = 8 期望);**Editor-can-upload / Viewer-cannot-upload 全部缺**。
5. **`dotnet-gap-2026-08-23.md` / `adr-gap-2026-08-23.md` 把 RBAC 列为 🔴 长周期项**,但目前**无 in-progress ticket**。

`backend/app/permissions.py` 全文 74 行,核心是 `_require(min_role)` 工厂 + 3 个预制 dep(`ks_reader = _require("viewer")` / `ks_writer = _require("editor")` / `ks_owner = _require("owner")`)。错误文案统一 `"Insufficient permissions"`(vs `effective_role` None 时 `"You don't have access to this knowledge system"`)。

## 2. 目标

把"加 RBAC"从 8+ 处手工复制收口为"一个 attribute + 一个 policy name",并把"端点 × 角色"做成机器可验证的回归矩阵:

| 目标 | 验收 |
|---|---|
| **单点决策** | `[KSRoleAuthorize(Minimum = KSRole.Editor)]` 一个 attribute 取代 8+ 处 service guard |
| **OpenAPI 可见** | 每个 dispatch endpoint 在 OpenAPI v1.json 携带 `x-onto-pilot-roles` 扩展,前端能枚举 |
| **集中 policy** | `AdminOnly` / `KSOwnerOnly` 两个 named policy,替代字面量 `[Authorize(Roles="Admin")]` |
| **矩阵测试** | `EndpointRoleMatrixTests` 反射枚举所有 `[Route]` action,`(anonymous / viewer / editor / owner / admin)` 五种 actor 跑一遍,CI 防回归 |
| **错误文案对齐** | 403 detail 与 Python 一致:`"You don't have access to this knowledge system"`(无 grant)/ `"Insufficient permissions"`(role 不足) |

## 3. 决策

### 3.1 `[KSRoleAuthorize]` Action Filter(Step 1,commit `feat(rbac): KSRoleAuthorize filter`)

**形态**: `Attribute, IAsyncAuthorizationFilter`,挂在 controller action 上,从路由 `{id:guid}` 或 query `?publicId=...` 解析 KS,查 `KnowledgeSystemAccessService.GetEffectiveRoleAsync`,未达 role 抛 403 envelope。

**签名**:

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class KSRoleAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter
{
    public KSRole Minimum { get; }
    /// <summary>Route argument holding the KS Guid. Defaults to "id".</summary>
    public string RouteArgument { get; init; } = "id";
    /// <summary>If true, allows ExternalToken scheme to bypass (for /api/v1/* published). Default false.</summary>
    public bool AllowExternalToken { get; init; }

    public KSRoleAuthorizeAttribute(KSRole minimum) { Minimum = minimum; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // 1. Pull actor from HttpContext.Items["auth.user"] (Session handler injects UserEntity)
        // 2. If AllowExternalToken && principal is ClaimsPrincipal with ExternalToken scheme → bypass
        // 3. Extract Guid from route (context.ActionArguments[RouteArgument])
        // 4. Resolve KS via KnowledgeSystemAccessService (DI)
        // 5. Compare role; emit 403 {"detail": "Insufficient permissions"} if < Minimum
        // 6. Unknown KS → 404 {"detail": "Knowledge system not found"} (mirrors Python)
    }
}
```

**关键细节**:

- **依赖注入**: `IAsyncAuthorizationFilter` 在 DI 容器外执行,filter 实例不能直接 DI;走 `context.HttpContext.RequestServices.GetRequiredService<KnowledgeSystemAccessService>()` 解析(scoped,每个请求 fresh)。
- **路径绑定兼容**: 现有 endpoint 多用 `{id:guid}`(Guid,PK),少量 `{publicId}` 字符串(publicId)。filter 接受 `RouteArgument` 参数,默认 `"id"`;`publicId` 路径需 explicit `RouteArgument = "publicId"`。
- **KSGuid ↔ DB Guid 对齐**: 自 IRI Phase 3(commit 700b76e)起所有内部路径用 `Guid`,`KnowledgeSystem.PublicId` 仍为字符串供 token / 公开 URL 用 — filter 内部解析时,先看是否 Guid 路由,是 → 直接 `db.KnowledgeSystems.FindAsync(guid)`;否 → `db.KnowledgeSystems.FirstOrDefaultAsync(k => k.PublicId == value)`。
- **错误响应形态**: 与 Python 一致 — 404 `{"detail":"Knowledge system not found"}` / 403 无 grant `{"detail":"You don't have access to this knowledge system"}` / 403 角色不足 `{"detail":"Insufficient permissions"}`。FastApiErrorMiddleware 已统一 envelope,filter 只需 throw 对应 exception 或 `context.Result = new ObjectResult(...)`。
- **不变 behavior**: 不动 service 内 guard —— 保留 8+ 处 `RequireRoleAsync` 调用,Step 1 只在 controller 加 attribute;Step 4(可选 follow-up)再删 service 内部 guard。本切片最大风险面是"attribute 与 service guard 同时存在,各自独立决策";两者必然同时通过(都是同一 `KnowledgeSystemAccessService` 实例),语义无歧义。

**挂点(Step 1 必挂 + 选挂)**:

| Controller | Action | 建议 minimum |
|---|---|---|
| `ABoxController` | 所有 `{id:guid}/abox/*` | Editor(已在 ABoxService guard,挂 attribute 是"双保险"过渡) |
| `KnowledgeController` | list / detail / stats | Viewer |
| `KnowledgeController` | create / update / refresh_stats / extract | Editor |
| `KnowledgeController` | delete / members | Owner |
| `ConflictsController` | read | Viewer |
| `ConflictsController` | apply / set_property_union / merge_properties / subordinate_properties | Editor |
| `DocumentsController` | list / detail / chunk | Viewer |
| `DocumentsController` | upload / delete | Editor |
| `ExtractionController` | start / cancel | Editor |
| `PromptsController` | list / render | Viewer |
| `PromptsController` | create / update / delete | Editor |
| `HistoryController` | list / diff / revert | Viewer(只读) |
| `ResolutionController` | list / apply | Editor |
| `ReleasesController` | list / detail / export | Viewer |
| `ReleasesController` | create / publish / cutover | Owner |
| `RdfImportController` | import | Editor |
| `VocabularyController` | read | Viewer |
| `VocabularyController` | mutate | Editor |
| `OntologyController` | sources / external / published — read | Viewer |
| `OntologyController` | sources / external / published — write | Editor(具体看 route) |
| `TokensController` / `McpTokensController` | list / revoke | Owner |
| `TokensController` / `McpTokensController` | create | Editor |
| `SettingsController` / `ProvidersController` | 全部 | `[Authorize(Policy="AdminOnly")]`(Step 2 收口) |
| `PublishedController` / `ExternalApiController` | 全部 | `[Authorize(AuthenticationSchemes=ExternalToken)]` only — **不挂 KSRoleAuthorize** |
| `ApiBearerController` | 全部 | `[Authorize(AuthenticationSchemes=ApiBearer)]` only — 不挂 |

Step 1 先挂必挂 12 个 controller(必挂表),其余 Step 3 矩阵测试驱动逐步收敛。

### 3.2 `AddPolicy` 收口 Admin(Step 2,commit `feat(rbac): AddPolicy AdminOnly + KSOwnerOnly`)

**形态**: `Program.cs:544` 的 `AddAuthorization()` 改为 lambda 注册两个 policy;`SessionAuthenticationHandler.cs:98` 的 `Role="Admin"` claim 注入**保留**(policy 可直接消费),但加注释说明依赖关系。

**签名**:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Admin"));
    options.AddPolicy("KSOwnerOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx =>
              {
                  // ctx.User is the ClaimsPrincipal from Session scheme
                  // KSRole is computed inside [KSRoleAuthorize] attribute; policy is
                  // a thin "must already have role≥Owner" gate (e.g. for tokens creation)
                  return ctx.User.IsInRole("Admin");  // Step 2 范围:仅 AdminOnly 实质生效,KSOwnerOnly 留 hook
              }));
});
```

**改动点**:

| 文件 | 改动 |
|---|---|
| `Program.cs:544` | `AddAuthorization()` → `AddAuthorization(options => { options.AddPolicy(...); })` |
| `ProvidersController.cs:13` | `[Authorize(Roles="Admin")]` → `[Authorize(Policy="AdminOnly")]` |
| `SettingsController.cs:13` | 同上 |
| `AuthController.cs:211, 219, 227, 235`(4 处 users admin 端点)| 同上 |

**为什么 Step 2 单独成 commit**: policy name 与字面量 `Roles="Admin"` 的语义等价,迁移是纯 refactor,无行为变更;CI 校验"4 个 admin 端点 × (admin/non-admin/anonymous) 期望码"必须 100% 通过。

### 3.3 endpoint × role 矩阵测试(Step 3,commit `test(rbac): endpoint×role full-matrix HTTP test`)

**形态**: `EndpointRoleMatrixTests.cs` 在 `OnToPilot.Tests/Authorization/` 下,用 `WebApplicationFactory<Program>`(已就位,`AuthAdminApiTests` 在用)反射枚举所有 `[Route]` controller + action,每个 action 跑 `(anonymous, viewer, editor, owner, admin)` 五种 actor,记录期望状态码。

**测试生成**:

```csharp
[Theory]
[MemberData(nameof(AllEndpointMatrix))]
public async Task Each_endpoint_respects_role_matrix((string method, string path, Type expectedException) tc)
{
    foreach (var (actor, setup) in new[] {
        (Anonymous:    (HttpClient)AnonClient(),    NoSetup),
        (Viewer:       SeedGrant(Viewer),           (HttpClient)ClientForUser(_viewer.Id)),
        (Editor:       SeedGrant(Editor),           (HttpClient)ClientForUser(_editor.Id)),
        (Owner:        SeedGrant(Owner),            (HttpClient)ClientForUser(_owner.Id)),
        (Admin:        SetIsAdmin(true),            (HttpClient)ClientForUser(_admin.Id)),
    })
    {
        var resp = await actor.Invoke(tc.method, tc.path);
        // 期望码来自 MemberData 中的 (Anonymous, Viewer, Editor, Owner, Admin) 5 元组
        Assert.Equal(expected, resp.StatusCode);
    }
}
```

**MemberData 来源**: 反射枚举 + 手工维护 `rbac_matrix_expected.json`(持久化为测试资源,改动需显式 review):

```json
{
  "GET /api/knowledge/{id:guid}/abox/individuals": {
    "anonymous": 401,
    "viewer": 200,
    "editor": 200,
    "owner": 200,
    "admin": 200
  },
  "POST /api/knowledge/{id:guid}/abox/individuals": {
    "anonymous": 401,
    "viewer": 403,
    "editor": 200,
    "owner": 200,
    "admin": 200
  }
}
```

**不做**(避免变成无底洞):
- 不枚举 `ExternalApiController` / `PublishedController`(已用 token scheme,RBAC 矩阵是 token-scope 维度)
- 不枚举 `HealthController`(public)
- 不枚举 MCP endpoint(transport 不同,见 3.1 注)
- 不测 token scheme 的 matrix(token 测试已有 `ApiBearerTests`)

**与现有 harness 对齐**: `OnToPilot.ApiContract.Tests` 已有反射枚举模式;`EndpointRoleMatrixTests` 复用 `WebApplicationFactory` + SQLite `EnsureCreated`,不新增 fixture。

**Step 3 输出物**:
- `tests/Authorization/rbac_matrix_expected.json`(持久化,作为期望 source-of-truth)
- `tests/Authorization/EndpointRoleMatrixTests.cs`(反射枚举 + 5 actor HTTP 验证)
- `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md`(本文件第 6 节提取,作为人类可读矩阵表)
- 新 PR description 链接 `dotnet-gap-2026-08-23.md` 的 RBAC 项,正式关闭 🔴

## 4. 范围

| Commit | 文件 | 说明 |
|---|---|---|
| `feat(rbac): KSRoleAuthorize filter` | `src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs`(新)| filter 实现 |
| | `src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs`(内嵌 `403Envelope` / `404Envelope`)| 错误响应复用 FastAPI 文案 |
| | 12 个 controller(必挂表)+ `services.AddScoped<KSRoleAuthorizeAttribute>()` 注册 | attribute 挂点 |
| | `src/OnToPilot.Tests/Authorization/KSRoleAuthorizeFilterTests.cs`(新)| filter 单元 + WebApplicationFactory 集成 |
| `feat(rbac): AddPolicy AdminOnly + KSOwnerOnly` | `src/OnToPilot/Program.cs`(改 `AddAuthorization`)| policy 注册 |
| | 3 个 controller × 4 处字面量替换 | `[Authorize(Roles="Admin")]` → `[Authorize(Policy="AdminOnly")]` |
| | `src/OnToPilot.Tests/Authorization/AdminPolicyTests.cs`(新)| admin 端点 × actor 矩阵 |
| `test(rbac): endpoint×role full-matrix HTTP test` | `src/OnToPilot.Tests/Authorization/EndpointRoleMatrixTests.cs`(新)| 反射枚举 + 5 actor 矩阵 |
| | `src/OnToPilot.Tests/Authorization/rbac_matrix_expected.json`(新,测试资源)| 期望 source-of-truth |
| | `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md`(新,人类可读版)| 文档化矩阵 |

**总改动**: 3 commits,8 新文件 + 16 修改文件,预估 +800 行 / -50 行(其中 rbac_matrix_expected.json ~600 行手工维护数据)。

## 5. 不在范围

- **删除 8+ 处 service `RequireRoleAsync`** — Step 1-3 不动 service guard,filter 与 guard 共存(双保险);service guard 删除需独立 follow-up(无功能影响,纯 DRY 收益)。
- **`KSRole` ↔ DB 字符串映射统一化** — `KnowledgeService.RoleName()` / `KSGrants.Role` / 各种 `switch (role)` 散落多处。统一需要 schema 微调(枚举列 / 静态常量),跨 schema-3 长周期,不在 RBAC 切片内。
- **per-KS `accessible_ks_ids` 性能优化** — `backend/app/permissions.py:39` 的"返回 None 表全可见,否则 set"用于 list-endpoint 过滤;.NET 端 `KnowledgeController.List` 走 `accessibleKsIds` SQL filter 已有等价物(`actor`-driven `KnowledgeRepository.ListAsync`),不重复做。
- **RBAC 跨 token 撤销实时性** — `McpPrincipalAccessor` 已每调用重查;HTTP filter 走 `HttpContext.Items["auth.user"]`(session 注入的 UserEntity 快照,无实时性)。token 撤销实时性属于 SessionAuthenticationHandler 重构,不在本切片。
- **新增 RBAC 端点 / 编辑权限矩阵** — Step 3 矩阵只是"验证当前契约",不调整任何 endpoint 的当前角色(若有产品级"viewer 应可上传"等需求,走独立 P-ticket)。
- **OpenAPI 自动生成 `x-onto-pilot-roles`** — Step 1 attribute 在运行时拦截,但 OpenAPI doc 由 Swashbuckle 生成;让 attribute 自动驱动 `x-onto-pilot-roles` 需要 `IOperationFilter`,属 Step 4 候选 follow-up,本切片手动维护预期 JSON。
- **Audit log RBAC 失败原因** — 当前 403 不打 audit;Step 4 候选 follow-up。

## 6. endpoint × role 完整矩阵(初版,Step 3 持久化到 JSON)

> 标记 **[v]** = viewer 可见,**[e]** = editor,**[o]** = owner,**[a]** = admin(全程)
> **N/A** = 不适用(已是 admin 端点 / token 端点 / public 端点)

| Method+Path | Anon | Viewer | Editor | Owner | Admin |
|---|---|---|---|---|---|
| `POST /api/auth/login` | 200 | — | — | — | — |
| `GET /api/auth/me` | 401 | 200 | 200 | 200 | 200 |
| `* /api/auth/users` | 401 | 403 | 403 | 403 | 200 |
| `GET /api/health` | 200 | — | — | — | — |
| `GET/POST /api/providers` | 401 | 403 | 403 | 403 | 200 |
| `GET/PUT /api/settings` | 401 | 403 | 403 | 403 | 200 |
| `GET /api/knowledge` | 401 | 200(仅可访问 KS)| 200 | 200 | 200(全部)|
| `POST /api/knowledge` | 401 | 403 | 200 | 200 | 200 |
| `GET /api/knowledge/{id:guid}` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `PUT /api/knowledge/{id:guid}` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `DELETE /api/knowledge/{id:guid}` | 401 | 403 | 403 | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/members` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/members` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `DELETE /api/knowledge/{id:guid}/members/{userId}` | 401 | 403 | 403 | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/refresh_stats` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/abox/*` (read) | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST/PUT/DELETE /api/knowledge/{id:guid}/abox/*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/conflicts/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/conflicts/*/apply` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/conflicts/set_property_union` 等 3 op | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/documents/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/documents/*/upload` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `DELETE /api/knowledge/{id:guid}/documents/*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/rdf/import` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/extract*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `DELETE /api/knowledge/{id:guid}/extract*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/prompts/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST/PUT/DELETE /api/knowledge/{id:guid}/prompts/*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/history/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/history/*/revert` | 401 | 200[v](只读)→ 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/resolution/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/resolution/*/apply` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/releases/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/releases` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/releases/*/publish` | 401 | 403 | 403 | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/releases/*/cutover` | 401 | 403 | 403 | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/vocabulary/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST/PUT /api/knowledge/{id:guid}/vocabulary/*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/ontology/sources/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/ontology/sources/*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/ontology/external/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/ontology/external/*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/ontology/published/*` | 401 | 200[v] | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/ontology/published/*` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/tokens` | 401 | 200[v](?)/ 403 | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/tokens` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `DELETE /api/knowledge/{id:guid}/tokens/{tokenId}` | 401 | 403 | 403 | 200[o] | 200[a] |
| `GET /api/knowledge/{id:guid}/mcp_tokens` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `POST /api/knowledge/{id:guid}/mcp_tokens` | 401 | 403 | 200[e] | 200[o] | 200[a] |
| `DELETE /api/knowledge/{id:guid}/mcp_tokens/{tokenId}` | 401 | 403 | 403 | 200[o] | 200[a] |
| `* /api/v1/knowledge-systems/{publicId}/*` (Published/External) | N/A — token scheme |
| `* /mcp` | N/A — MCP transport |
| `GET /api/bearer/whoami/{publicId}` | N/A — ApiBearer scheme |

> **争议项**: `GET /api/knowledge/{id:guid}/tokens` 当前 service 内未明确最低级别 — 需 service 审计(Step 1 attribute 挂 Owner 防御,Step 3 测试驱动收敛)。Python 端 `tokens.*` 走 `ks_writer`,即最低 Editor。本表暂定 Owner(保守),Step 3 实施时按真实 service guard 调整 JSON。

## 7. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| **attribute + service guard 双保险导致 403 文案不一致** | UI 显示混乱 | Step 1 先挂 attribute,但 1 周观察期内不动 service guard,矩阵测试校验文案统一;Step 4(可选)再删 service guard |
| **filter 注入路径 `RequestServices` 解析 scope 失败** | 500 错误 | 在 `OnAuthorizationAsync` 入口 try-catch `InvalidOperationException`,fallback 到 503(`{"detail":"Authorization subsystem unavailable"}`)|
| **Step 3 rbac_matrix_expected.json 漂移** | 矩阵测试无意义 | PR review 必须显式 review JSON 变更;另加 `rbac_matrix_expected.json` 的 snapshot commit,任何差异必须对应 controller action 变更 |
| **Step 2 policy 名 "AdminOnly" 误用** | 4 个 admin 端点 + 后续端点不一致 | 在 `OnToPilot.Authorization` 命名空间暴露 `Policies.AdminOnly` 常量,避免字面量拼写 |
| **MCP 通道与 HTTP filter 语义割裂** | MCP 与 HTTP 不同 actor 走不同决策路径 | 不动 McpPrincipalAccessor(它已是正确语义);filter 仅覆盖 HTTP controller,MCP 路径在文档第 5 节明确 N/A |
| **`Admin` role claim 与 `IsAdmin` 不一致** | policy 拿不到 Admin 角色 | Step 2 保留 `SessionAuthenticationHandler.cs:98` claim 注入;Step 4 候选迁移"从 claim 改查 UserEntity.IsAdmin",不在本切片 |
| **`KSOwnerOnly` policy hook 暂时不实装** | 未来需要"全局 KSOwner"端点时再补 | 注释明示 hook,Step 4 follow-up |

## 8. 验证

| 阶段 | 命令 | 期望 |
|---|---|---|
| **Step 1 编译** | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 错误 |
| **Step 1 单元 + 集成** | `dotnet test src/OnToPilot.Tests/ --filter "FullyQualifiedName~Authorization"` | 现有 `KnowledgeSystemAccessTests` 继续绿 + 新 `KSRoleAuthorizeFilterTests` 全绿 |
| **Step 1 全量回归** | `dotnet test src/OnToPilot.Tests/` | 期望 736 + N + 0 regress |
| **Step 1 契约** | `dotnet test src/OnToPilot.ApiContract.Tests/` | 167/167(契约无变化)|
| **Step 2 编译 + admin 矩阵** | `dotnet test --filter "FullyQualifiedName~AdminPolicyTests"` | 12 期望(4 端点 × 3 actor)全绿 |
| **Step 3 矩阵** | `dotnet test --filter "FullyQualifiedName~EndpointRoleMatrixTests"` | 全绿(~250 期望 = 50 action × 5 actor)|
| **Step 3 全量回归** | `dotnet test src/OnToPilot.Tests/` | 期望 736 + Step1 N + Step2 12 + Step3 250 + 0 regress |
| **集成(若 docker)** | `dotnet test src/OnToPilot.IntegrationTests/ --filter "Category!=Container"` | 现有基线 + 0 regress |
| **手动**:admin 端点非-admin 访问 | `curl -X GET /api/providers --cookie "session=non-admin"` | 403 `{"detail":"..."}` |
| **手动**:viewer 调 editor 端点 | `curl -X POST /api/knowledge/{id}/abox/individuals --cookie "session=viewer"` | 403 `{"detail":"Insufficient permissions"}` |
| **手动**:token scheme 不受影响 | `curl -X GET /api/v1/knowledge-systems/{pub}/sparql --header "Authorization: Bearer ext-token"` | 200 |

## 9. 实施切片(3 commits,顺序执行)

1. **commit 1** — `feat(rbac): KSRoleAuthorize filter + 12 controller 挂点`
   - 新增 `src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs`
   - 12 controller 挂 attribute(必挂表)
   - 新增 `src/OnToPilot.Tests/Authorization/KSRoleAuthorizeFilterTests.cs`
   - `dotnet build` + `dotnet test` 全绿

2. **commit 2** — `feat(rbac): AddPolicy AdminOnly + KSOwnerOnly, retire inline Roles="Admin"`
   - `Program.cs:544` 改 lambda 注册
   - `ProvidersController` / `SettingsController` / `AuthController` × 4 处 改 policy
   - 新增 `src/OnToPilot.Tests/Authorization/AdminPolicyTests.cs`
   - 全绿

3. **commit 3** — `test(rbac): endpoint×role full-matrix HTTP test + docs`
   - 新增 `EndpointRoleMatrixTests.cs` + `rbac_matrix_expected.json`
   - 新增 `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md`(第 6 节表作为人类可读版)
   - 更新 `ontopilot-dotnet-gap-2026-08-23.md`(RBAC 项:🔴 → 🟢) + `ontopilot-adr-gap-2026-08-23.md`(同步) + `MEMORY.md`(新条目 `ontopilot-rbac-coverage-matrix`)
   - 全绿

## 10. 参考

- [ontopilot-dotnet-gap-2026-08-23.md](memory/ontopilot-dotnet-gap-2026-08-23.md) — RBAC 列为 🔴 长周期项的源
- [ontopilot-adr-gap-2026-08-23.md](memory/ontopilot-adr-gap-2026-08-23.md) — 17 篇 ADR 缺口登记
- `backend/app/permissions.py:52-73` — Python `_require` 工厂 + 3 个 dep 的精确语义
- `src/OnToPilot/Authorization/KnowledgeSystemAccessService.cs:33-86` — 已存在的 KSRole 决策逻辑(无修改)
- `src/OnToPilot/Mcp/McpPrincipalAccessor.cs:125-137` — MCP 通道的 RequireRoleAsync,作 filter 设计参考(实时查库语义保留)
- `src/OnToPilot/Authentication/SessionAuthenticationHandler.cs:92-103` — claim 注入位置(`Role="Admin"` 第 98 行)
- `src/OnToPilot/Program.cs:534-544` — `AddAuthentication` / `AddAuthorization` 注入点
- `src/OnToPilot/Controllers/InternalControllerBase.cs:68-83` — actor 解析锚点(`HttpContext.Items["auth.user"]`)
- `src/OnToPilot/Api/ReadOnlySparqlPolicy.cs` — 唯一的"policy 类"参考(虽基于 query 字符串,结构可参考)
- `src/OnToPilot.Tests/Authentication/AuthAdminApiTests.cs` — 现有 HTTP 层 RBAC 测试模式(Step 2/3 直接复用)
- [[ontopilot-iri-phase3]] — IRI Phase 3(KSGuid ↔ DB Guid 对齐来源)
- [[ontopilot-allocator-atomic]] — 类似的"单点决策收口"模式参考(allocator AtomicAlloc+Persist)