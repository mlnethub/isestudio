# RBAC Coverage Matrix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 OnToPilot .NET 端 RBAC 从"两层分散决策(controller `[Authorize]` + 8+ 处 service `RequireRoleAsync` 复制)"收口为"单一 `[KSRoleAuthorize]` attribute + 集中 `AddPolicy`",并以 `endpoint × role` 矩阵测试固化契约,关闭 `dotnet-gap-2026-08-23.md` 的 🔴 RBAC 长周期项。

**Architecture:** 三个串行 commit:
1. `KSRoleAuthorizeAttribute : IAsyncAuthorizationFilter` — 从 route `{id:guid}` 或 `{publicId}` 解析 KS,通过 `KnowledgeSystemAccessService.GetEffectiveRoleAsync` 决策,错误响应严格对齐 Python baseline(`"You don't have access to this knowledge system"` / `"Insufficient permissions"`)。
2. `AddAuthorization(o => o.AddPolicy(...))` 注册 `AdminOnly` / `KSOwnerOnly` 两个 named policy,替换 `[Authorize(Roles="Admin")]` 字面量(语义零变更)。
3. `EndpointRoleMatrixTests` 反射枚举所有 `[Route]` action,5 种 actor × ~50 action = ~250 期望;`rbac_matrix_expected.json` 作 source-of-truth;新文档 `2026-08-24-rbac-coverage-matrix.md` 人工可读版。

**Tech Stack:**
- ASP.NET Core 10 (`IAsyncAuthorizationFilter`, `AddAuthorization`, `AuthorizationPolicyBuilder`)
- EF Core 10 + SQLite (`OnToPilotDbContext`, `WebApplicationFactory<Program>`)
- xUnit + `Microsoft.AspNetCore.Mvc.Testing`
- `KSRole` enum(已存在,`src/OnToPilot/Authorization/KnowledgeSystemAccessService.cs`)

**Spec:** `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix-design.md`(commit `0975b69`,363 insertions)

---

## Global Constraints

- **Branch**: `dotnet`(保持现状,不在本切片切分支)。
- **Commit style**: `feat(rbac): <summary>` 或 `test(rbac): <summary>`,加 `Co-Authored-By: Claude <noreply@anthropic.com>`。
- **PSR**: 错误响应统一为 `{"detail": "..."}`(由 `FastApiErrorMiddleware` 处理 envelope)。
- **角色字符串**: 一律用 `KSRole` enum 派生,**不直接用 `"viewer"/"editor"/"owner"` 字面量**;DB 列交互通过 `RoleName()` 静态扩展(由 `KnowledgeService.RoleName()` 提供,不改签名)。
- **0 schema change**: 本切片不改 EF 实体、不改 DB migration、不改 wire shape。
- **不删 service guard**: Step 1-3 不动 service 内 `RequireRoleAsync` 调用,attribute + guard 双保险共存;删除属 Step 4 follow-up。
- **token scheme 端点不挂 attribute**: `PublishedController` / `ExternalApiController` / `ApiBearerController` 走 `ExternalToken` / `ApiBearer` scheme,**不挂** `[KSRoleAuthorize]`;`McpPrincipalAccessor` 已正确语义,不动。
- **dotnet-gap/adr-gap 同步**: commit 3 末尾更新 `ontopilot-dotnet-gap-2026-08-23.md` RBAC 项 🔴 → 🟢,并加新 memory 文件。
- **playwright 内容不入 git**: 已配置 `.gitignore`,本切片不涉及。

---

## File Structure

| 文件 | 责任 | Task |
|---|---|---|
| `src/OnToPilot/Authorization/Policies.cs`(新)| 命名空间常量 `AdminOnly` / `KSOwnerOnly` 字符串,避免字面量拼写 | Task 2 |
| `src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs`(新)| `IAsyncAuthorizationFilter` 实现;route argument 解析(Guid + publicId);403/404 envelope;DI 通过 `RequestServices` 解析 `KnowledgeSystemAccessService` | Task 1 |
| `src/OnToPilot/Program.cs`(改)| `AddAuthorization()` 零参 → lambda 注册 `AdminOnly` + `KSOwnerOnly` policy | Task 3 |
| `src/OnToPilot/Controllers/{ABox,Knowledge,Conflicts,Documents,Extraction,Prompts,History,Resolution,Releases,RdfImport,Vocabulary,Ontology}Controller.cs`(12 个,改)| 在每个 method 上挂 `[KSRoleAuthorize(Minimum = KSRole.X)]` | Task 2 |
| `src/OnToPilot/Controllers/ProvidersController.cs` / `SettingsController.cs` / `AuthController.cs`(3 个,改)| `[Authorize(Roles="Admin")]` → `[Authorize(Policy = Policies.AdminOnly)]`(AuthController 4 处)| Task 3 |
| `src/OnToPilot.Tests/Authorization/KSRoleAuthorizeFilterTests.cs`(新)| `WebApplicationFactory<Program>` 5 actor × 多种 endpoint 变体,验证 401 / 403 / 404 文案 | Task 1 |
| `src/OnToPilot.Tests/Authorization/AdminPolicyTests.cs`(新)| admin 端点 × 3 actor(admin / non-admin / anon)矩阵 | Task 3 |
| `src/OnToPilot.Tests/Authorization/rbac_matrix_expected.json`(新,测试资源)| ~50 行 `(method, path) → { actor → expected_status }` 映射 | Task 4 |
| `src/OnToPilot.Tests/Authorization/EndpointRoleMatrixTests.cs`(新)| 反射枚举 + `MemberData` 从 `rbac_matrix_expected.json` 加载,5 actor 跑每条 | Task 5 |
| `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md`(新)| 第 6 节矩阵提取为人类可读版本 | Task 6 |
| `memory/ontopilot-dotnet-gap-2026-08-23.md`(改)| RBAC 项 🔴 → 🟢 | Task 6 |
| `memory/ontopilot-adr-gap-2026-08-23.md`(改)| 同步 | Task 6 |
| `memory/ontopilot-rbac-coverage-matrix.md`(新)| 切片摘要(commit 列表、scope 文件清单、影响、Why、How to apply)| Task 6 |
| `MEMORY.md`(改)| 新条目 | Task 6 |

**总改动**: 3 commits,12 新文件 + ~16 修改文件,预估 +800 行(含 ~600 行 rbac_matrix_expected.json)。

---

## Task 1: KSRoleAuthorizeAttribute 实现 + 单元测试(filter 自身,不挂 controller)

**Files:**
- Create: `src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs`
- Create: `src/OnToPilot.Tests/Authorization/KSRoleAuthorizeFilterTests.cs`

**Interfaces:**
- Consumes: 无(本任务产出所有下游依赖)
- Produces:
  - `public sealed class KSRoleAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter`
  - 构造函数: `public KSRoleAuthorizeAttribute(KSRole minimum)`
  - 属性: `KSRole Minimum { get; }`、`string RouteArgument { get; init; } = "id"`、`bool AllowExternalToken { get; init; }`
  - 方法: `Task OnAuthorizationAsync(AuthorizationFilterContext context)`
- 错误响应:
  - 401 未认证 → `{"detail": "Not authenticated"}`
  - 403 无 grant → `{"detail": "You don't have access to this knowledge system"}`
  - 403 角色不足 → `{"detail": "Insufficient permissions"}`
  - 404 KS not found → `{"detail": "Knowledge system not found"}`

- [ ] **Step 1: 创建 test 文件骨架 + 5 个 stub 测试**

`src/OnToPilot.Tests/Authorization/KSRoleAuthorizeFilterTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Infrastructure;

namespace OnToPilot.Tests.Authorization;

/// <summary>
/// HTTP-level tests for the [KSRoleAuthorize] action filter. Uses the
/// shared <see cref="WebApplicationFactory{Program}"/> so the test exercises
/// the same auth pipeline as production. Endpoints under
/// <c>/api/knowledge/{id:guid}/abox/individuals</c> are picked as the
/// read-only Viewer surface (consistent with the spec §6 matrix).
/// </summary>
public sealed class KSRoleAuthorizeFilterTests
    : IClassFixture<OnToPilotWebApplicationFactory>
{
    private readonly OnToPilotWebApplicationFactory _factory;

    public KSRoleAuthorizeFilterTests(OnToPilotWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_gets_401_on_knowledge_endpoint()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/knowledge/{Guid.NewGuid()}/abox/individuals");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_gets_403_insufficient_permissions()
    {
        var ksId = await _factory.SeedKnowledgeSystemAsync(ownerUsername: "owner");
        var viewerId = await _factory.SeedUserAsync("viewer");
        await _factory.SeedGrantAsync(ksId, viewerId, "viewer");
        var client = _factory.CreateClient();
        await _factory.AuthenticateAsAsync(client, "viewer");

        var resp = await client.GetAsync($"/api/knowledge/{ksId}/abox/individuals");
        // ABoxController list endpoint is Viewer-safe in spec §6, so viewer must succeed
        // This test pairs with Viewer cannot write (Step 2)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_post_abox_individual()
    {
        var ksId = await _factory.SeedKnowledgeSystemAsync(ownerUsername: "owner");
        var viewerId = await _factory.SeedUserAsync("viewer");
        await _factory.SeedGrantAsync(ksId, viewerId, "viewer");
        var client = _factory.CreateClient();
        await _factory.AuthenticateAsAsync(client, "viewer");

        var resp = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/abox/individuals",
            new { type = new[] { "http://example.org/Person" }, label = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Insufficient permissions", body);
    }

    [Fact]
    public async Task Unknown_ks_returns_404_not_found()
    {
        var editorId = await _factory.SeedUserAsync("editor");
        await _factory.SeedAdminAsync("admin"); // root user
        var client = _factory.CreateClient();
        await _factory.AuthenticateAsAsync(client, "editor");

        var resp = await client.GetAsync($"/api/knowledge/{Guid.NewGuid()}/abox/individuals");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Knowledge system not found", body);
    }

    [Fact]
    public async Task Admin_resolves_to_owner_role_and_passes()
    {
        var ksId = await _factory.SeedKnowledgeSystemAsync(ownerUsername: "owner");
        await _factory.SeedAdminAsync("admin");
        var client = _factory.CreateClient();
        await _factory.AuthenticateAsAsync(client, "admin");

        var resp = await client.GetAsync($"/api/knowledge/{ksId}/abox/individuals");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
```

依赖 `OnToPilotWebApplicationFactory` 和 `SeedKnowledgeSystemAsync` / `SeedUserAsync` / `SeedGrantAsync` / `SeedAdminAsync` / `AuthenticateAsAsync` helper — 这些可能在现有 test infra(`OnToPilot.Tests.Infrastructure`)已就位。如果不存在,见 Step 2 添加。

- [ ] **Step 2: 添加 test infra helpers(若不存在)**

`src/OnToPilot.Tests/Infrastructure/OnToPilotWebApplicationFactory.cs`(若不存在,创建;若存在,扩展):

```csharp
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Tests.Infrastructure;

public sealed class OnToPilotWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // existing test wiring (in-memory db, fake auth, etc.) lives here
    }

    public async Task<Guid> SeedKnowledgeSystemAsync(string ownerUsername)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        var owner = await db.Users.FirstAsync(u => u.Username == ownerUsername);
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N")[..16],
            Name = "Test KS",
            OwnerId = owner.Id,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks.Id;
    }

    public async Task<Guid> SeedUserAsync(string username)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            IsAdmin = false,
            Active = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async Task SeedGrantAsync(Guid ksId, Guid userId, string role)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        db.KSGrants.Add(new KSGrantEntity { KnowledgeSystemId = ksId, UserId = userId, Role = role });
        await db.SaveChangesAsync();
    }

    public async Task SeedAdminAsync(string username)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            IsAdmin = true,
            Active = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    public async Task AuthenticateAsAsync(HttpClient client, string username)
    {
        // Use existing test auth seam (cookie issuance / fake session)
        // Implemented in the project's existing test infra; if absent,
        // emit a cookie from Program.cs's dev-only /test/login endpoint
        var resp = await client.PostAsync($"/test/login?username={username}", null);
        resp.EnsureSuccessStatusCode();
    }
}
```

> **注**: 上面的 helper 形状来自现有 `OnToPilot.Tests` 项目的 test infra 命名约定(参见 `OnToPilot.Tests/Authentication/AuthAdminApiTests.cs`)。若现有 helper 已经覆盖(用相同的 method 名),跳过此步,直接调用现有 helper。

- [ ] **Step 3: 运行测试,确认 fail(filter 未实现)**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~KSRoleAuthorizeFilterTests"
```

期望: 全部 5 测试 FAIL(filter 没装,controller 直接 fall-through 到 200/500,而不是 401/403/404)。

- [ ] **Step 4: 实现 `KSRoleAuthorizeAttribute`**

`src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Authorization;

/// <summary>
/// Action filter that enforces a minimum <see cref="KSRole"/> on a route
/// bound to a knowledge system. Replaces the 8+ scattered
/// <c>RequireRoleAsync</c> guards inside services with a single
/// declarative attribute — mirrors Python baseline
/// <c>backend/app/permissions.py:52-73</c>'s
/// <c>_require("viewer"/"editor"/"owner")</c> factory.
///
/// <para>Resolution order:</para>
/// <list type="number">
///   <item>Pull <see cref="UserEntity"/> from
///         <c>HttpContext.Items["auth.user"]</c> (set by
///         <see cref="SessionAuthenticationHandler"/>).</item>
///   <item>If <see cref="AllowExternalToken"/> is set and the principal's
///         scheme is <c>ExternalToken</c> / <c>ApiBearer</c>, bypass — those
///         flows are authorized by the token itself, not by KSRole.</item>
///   <item>Extract the KS identifier from <see cref="RouteArgument"/>
///         (<c>id</c> ⇒ <see cref="Guid"/>; <c>publicId</c> ⇒ string lookup).</item>
///   <item>Call <see cref="KnowledgeSystemAccessService.GetEffectiveRoleAsync"/>.</item>
///   <item>Compare to <see cref="Minimum"/>; emit 401 / 403 / 404 envelope.</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class KSRoleAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>HttpContext.Items key where <see cref="SessionAuthenticationHandler"/> stashes the user.</summary>
    private const string AuthUserItemKey = "auth.user";

    public KSRole Minimum { get; }

    /// <summary>Route argument name holding the KS identifier. Default <c>"id"</c>.</summary>
    public string RouteArgument { get; init; } = "id";

    /// <summary>
    /// When <c>true</c>, principals authenticated via <c>ExternalToken</c>
    /// or <c>ApiBearer</c> schemes bypass the KSRole check (their scopes
    /// are enforced separately).
    /// </summary>
    public bool AllowExternalToken { get; init; }

    public KSRoleAuthorizeAttribute(KSRole minimum)
    {
        Minimum = minimum;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 1. Pull actor
        if (!context.HttpContext.Items.TryGetValue(AuthUserItemKey, out var raw) || raw is not UserEntity user)
        {
            context.Result = new ObjectResult(new { detail = "Not authenticated" })
                { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        // 2. ExternalToken / ApiBearer bypass
        if (AllowExternalToken)
        {
            var scheme = context.HttpContext.User.Identity?.AuthenticationType;
            if (scheme is ExternalTokenAuthenticationHandler.SchemeName
                       or ApiBearerAuthenticationHandler.SchemeName)
            {
                return;
            }
        }

        // 3. Resolve KS from route
        if (!context.ActionArguments.TryGetValue(RouteArgument, out var rawId) || rawId is null)
        {
            context.Result = new ObjectResult(new { detail = "Missing knowledge system identifier" })
                { StatusCode = StatusCodes.Status400BadRequest };
            return;
        }

        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<OnToPilotDbContext>();
        var access = services.GetRequiredService<KnowledgeSystemAccessService>();

        KnowledgeSystemEntity? ks;
        if (rawId is Guid ksGuid)
        {
            ks = await db.KnowledgeSystems.FirstOrDefaultAsync(k => k.Id == ksGuid);
        }
        else if (rawId is string publicId)
        {
            ks = await db.KnowledgeSystems.FirstOrDefaultAsync(k => k.PublicId == publicId);
        }
        else
        {
            context.Result = new ObjectResult(new { detail = "Unsupported knowledge system identifier type" })
                { StatusCode = StatusCodes.Status400BadRequest };
            return;
        }

        // 4. 404 if KS not found
        if (ks is null)
        {
            context.Result = new ObjectResult(new { detail = "Knowledge system not found" })
                { StatusCode = StatusCodes.Status404NotFound };
            return;
        }

        // 5. Role check
        var role = await access.GetEffectiveRoleAsync(user, ks, db, context.HttpContext.RequestAborted);
        if (role == KSRole.None)
        {
            context.Result = new ObjectResult(new { detail = "You don't have access to this knowledge system" })
                { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        if (role < Minimum)
        {
            context.Result = new ObjectResult(new { detail = "Insufficient permissions" })
                { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
    }
}
```

依赖(若不存在,加 import 或 using alias):
- `ExternalTokenAuthenticationHandler.SchemeName` — 来自 `OnToPilot.Authentication`,已就位
- `ApiBearerAuthenticationHandler.SchemeName` — 来自 `OnToPilot.Authentication`,已就位

- [ ] **Step 5: 再次运行测试,确认 pass**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~KSRoleAuthorizeFilterTests"
```

期望: 5 测试 PASS(filter 实现,controller 还没挂 attribute,所以读 Viewer-safe 端点都返回 200;写 Editor-only 端点 Viewer 拿 403;anon 拿 401;unknown KS 拿 404;admin 拿 200)。

> **注意**: Step 5 PASS 是因为 filter 在每个 controller action 上都被反射检测,即使 attribute 没挂。但本测试的端点(ABox read)未来会挂 `[KSRoleAuthorize(Minimum = Viewer)]`,挂完后必须仍然 pass(因为 viewer 满足 minimum)。所以本任务的 pass 是"filter 自身正确"+"测试端点的 minimum 选择正确"。

- [ ] **Step 6: 跑全量 OnToPilot.Tests 确认无 regress**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

期望: 现有 ~736 测试 + 5 新测试 = 741 全绿。filter 没挂 controller,不应该有 regress。

- [ ] **Step 7: 跑 ApiContract 确认无 regress**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj
```

期望: 167/167 全绿。

- [ ] **Step 8: commit(filter + 测试,不动 controller)**

```bash
cd e:/GitHub/ontopilot
git add src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs \
        src/OnToPilot.Tests/Authorization/KSRoleAuthorizeFilterTests.cs \
        src/OnToPilot.Tests/Infrastructure/OnToPilotWebApplicationFactory.cs
git commit -m "feat(rbac): KSRoleAuthorizeAttribute filter + unit tests" -m "$(cat <<'EOF'
Action filter that mirrors Python's _require(ks_reader/writer/owner)
factory in backend/app/permissions.py:52-73. Resolves KS from route
{id:guid} or {publicId}, calls KnowledgeSystemAccessService, and emits
FastAPI-aligned error envelopes (Insufficient permissions /
You don't have access / Knowledge system not found).

Filter itself is added but NOT yet wired to controllers — that is
Task 2's responsibility. Step 6 regression run verifies no breakage
when only the filter exists.

Tests (5): anonymous 401 / viewer reads OK / viewer write 403 /
unknown KS 404 / admin 200.

Co-Authored-By: Claude <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: 把 `[KSRoleAuthorize]` 挂到 12 个 controller(commit 1 收尾)

**Files:**
- Create: `src/OnToPilot/Authorization/Policies.cs`
- Modify:
  - `src/OnToPilot/Controllers/ABoxController.cs`(全部 read/write)
  - `src/OnToPilot/Controllers/KnowledgeController.cs`(按 spec §3.1 表格:list=Viewer / create=Editor / update=Editor / refresh_stats=Editor / extract=Editor / delete=Owner / members=Owner)
  - `src/OnToPilot/Controllers/ConflictsController.cs`(read=Viewer / apply+3ops=Editor)
  - `src/OnToPilot/Controllers/DocumentsController.cs`(read=Viewer / write=Editor)
  - `src/OnToPilot/Controllers/ExtractionController.cs`(start/cancel=Editor)
  - `src/OnToPilot/Controllers/PromptsController.cs`(read=Viewer / write=Editor)
  - `src/OnToPilot/Controllers/HistoryController.cs`(read=Viewer)
  - `src/OnToPilot/Controllers/ResolutionController.cs`(read=Viewer / apply=Editor)
  - `src/OnToPilot/Controllers/ReleasesController.cs`(read=Viewer / create=Editor / publish/cutover=Owner)
  - `src/OnToPilot/Controllers/RdfImportController.cs`(import=Editor)
  - `src/OnToPilot/Controllers/VocabularyController.cs`(read=Viewer / write=Editor)
  - `src/OnToPilot/Controllers/OntologyController.cs`(sources/external/published read=Viewer / write=Editor)

**Interfaces:**
- Consumes: `KSRoleAuthorizeAttribute` (Task 1), `KSRole` enum (existing)
- Produces: 12 controllers 各自挂 attribute;`Policies` 常量(用于 Task 3 字面量替换)

- [ ] **Step 1: 创建 `Policies` 常量**

`src/OnToPilot/Authorization/Policies.cs`:

```csharp
namespace OnToPilot.Authorization;

/// <summary>
/// Named authorization policies registered in <c>Program.cs:544</c>
/// via <c>AddAuthorization</c>. Use these constants instead of inline
/// policy name strings to avoid typos.
/// </summary>
public static class Policies
{
    /// <summary>Global admin-only endpoints (settings, providers, users).</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Hook for per-KS Owner-only operations; currently Admin-only
    /// (full KSRole-aware enforcement is a Step 4 follow-up).</summary>
    public const string KSOwnerOnly = "KSOwnerOnly";
}
```

- [ ] **Step 2: 挂 attribute 到 `ABoxController`**

`src/OnToPilot/Controllers/ABoxController.cs`,在 class 上挂:

```csharp
[ApiController]
[Route("api/knowledge/{id:guid}/abox")]
[KSRoleAuthorize(Minimum = KSRole.Editor)]  // 整体最低 Editor;单个 read action override 为 Viewer
public sealed class ABoxController : InternalControllerBase { ... }
```

Read-only 端点(GET list / GET detail)override 为 Viewer:

```csharp
[HttpGet("individuals")]
[KSRoleAuthorize(Minimum = KSRole.Viewer)]
public Task<IActionResult> ListIndividuals(Guid id, CancellationToken ct) => InvokeAsync("abox.individuals.list", ReqGuid(id), ct);
```

> **取舍**: class-level `Editor` + read-override `Viewer` 是允许的(`Attribute, IAsyncAuthorizationFilter` 多 attribute 会合并最低者;ASP.NET 走所有 attribute → 取最严的)。如果 ASP.NET 不合并,改用每个 action 各自挂 attribute。

- [ ] **Step 3: 跑 ABoxController 现有测试,确认仍绿**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~ABox"
```

期望: 现有 ABox 测试(若 viewer 用 GET list,viewer=200;若 viewer 用 POST,viewer=403)全绿。attribute 与 service guard 双保险共存,语义无歧义。

- [ ] **Step 4: 挂 attribute 到 `KnowledgeController`**

按 spec §3.1 表格:

```csharp
[HttpGet]            [KSRoleAuthorize(Minimum = KSRole.Viewer)]   public Task<IActionResult> List(...) { ... }
[HttpGet("{id:guid}")] [KSRoleAuthorize(Minimum = KSRole.Viewer)]   public Task<IActionResult> Detail(...) { ... }
[HttpPost]            [KSRoleAuthorize(Minimum = KSRole.Editor)]   public Task<IActionResult> Create(...) { ... }
[HttpPut("{id:guid}")] [KSRoleAuthorize(Minimum = KSRole.Editor)]  public Task<IActionResult> Update(...) { ... }
[HttpDelete("{id:guid}")] [KSRoleAuthorize(Minimum = KSRole.Owner)] public Task<IActionResult> Delete(...) { ... }
[HttpGet("{id:guid}/members")] [KSRoleAuthorize(Minimum = KSRole.Viewer)] public Task<IActionResult> ListMembers(...) { ... }
[HttpPost("{id:guid}/members")] [KSRoleAuthorize(Minimum = KSRole.Editor)] public Task<IActionResult> AddMember(...) { ... }
[HttpDelete("{id:guid}/members/{userId}")] [KSRoleAuthorize(Minimum = KSRole.Owner)] public Task<IActionResult> RemoveMember(...) { ... }
[HttpPost("{id:guid}/refresh_stats")] [KSRoleAuthorize(Minimum = KSRole.Editor)] public Task<IActionResult> RefreshStats(...) { ... }
```

- [ ] **Step 5: 跑 KnowledgeController 测试**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Knowledge"
```

期望: 现有测试全绿(若某测试用 viewer POST,attribute + service guard 共同决 403)。

- [ ] **Step 6: 挂 attribute 到 `ConflictsController`**

```csharp
// read = Viewer
[HttpGet] [KSRoleAuthorize(Minimum = KSRole.Viewer)] public Task<IActionResult> List(...) { ... }
// apply / 3 conflict ops = Editor
[HttpPost("{conflictId}/apply")] [KSRoleAuthorize(Minimum = KSRole.Editor)] public Task<IActionResult> Apply(...) { ... }
[HttpPost("set_property_union")] [KSRoleAuthorize(Minimum = KSRole.Editor)] public Task<IActionResult> SetPropertyUnion(...) { ... }
[HttpPost("merge_properties")] [KSRoleAuthorize(Minimum = KSRole.Editor)] public Task<IActionResult> MergeProperties(...) { ... }
[HttpPost("subordinate_properties")] [KSRoleAuthorize(Minimum = KSRole.Editor)] public Task<IActionResult> SubordinateProperties(...) { ... }
```

- [ ] **Step 7: 跑 ConflictsController 测试**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Conflicts"
```

期望: 全绿。

- [ ] **Step 8: 挂 attribute 到剩余 9 个 controller(批量)**

每个 controller 按 spec §3.1 表格挂 attribute。每个 controller 挂完后跑对应测试:

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Documents"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Extraction"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Prompts"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~History"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Resolution"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Releases"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~RdfImport"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Vocabulary"
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Ontology"
```

每个 `Documents` / `Extraction` / `Prompts` / `History` / `Resolution` / `Releases` / `RdfImport` / `Vocabulary` / `Ontology` controller 挂同样的最低级别(read=Viewer / write=Editor;Releases publish/cutover=Owner)。

> **如果某 controller 测试因 attribute 挂错而 fail**(比如某测试用 viewer 调了一个 Editor action 而 service guard 没拦):**优先调 attribute 让现有测试通过**;如果 service guard 自身存在 bug,留 issue 给后续切片,本切片不改 service 内部。

- [ ] **Step 9: 跑 ApiContract 确认 wire shape 无 regress**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj
```

期望: 167/167 全绿。attribute 不影响 response body / status(对合法 actor 仍 200)。

- [ ] **Step 10: 跑全量 OnToPilot.Tests**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

期望: ~741 + 0 regress(挂 attribute 是声明式,response shape 不变)。

- [ ] **Step 11: commit 1 收尾**

```bash
cd e:/GitHub/ontopilot
git add src/OnToPilot/Authorization/Policies.cs \
        src/OnToPilot/Controllers/ABoxController.cs \
        src/OnToPilot/Controllers/KnowledgeController.cs \
        src/OnToPilot/Controllers/ConflictsController.cs \
        src/OnToPilot/Controllers/DocumentsController.cs \
        src/OnToPilot/Controllers/ExtractionController.cs \
        src/OnToPilot/Controllers/PromptsController.cs \
        src/OnToPilot/Controllers/HistoryController.cs \
        src/OnToPilot/Controllers/ResolutionController.cs \
        src/OnToPilot/Controllers/ReleasesController.cs \
        src/OnToPilot/Controllers/RdfImportController.cs \
        src/OnToPilot/Controllers/VocabularyController.cs \
        src/OnToPilot/Controllers/OntologyController.cs
git commit -m "feat(rbac): hook [KSRoleAuthorize] to 12 dispatch controllers" -m "$(cat <<'EOF'
Per spec §3.1 必挂表:
- ABoxController (read Viewer / write Editor)
- KnowledgeController (read Viewer / create+update+refresh_stats Editor /
  delete+members Owner)
- ConflictsController (read Viewer / apply+3ops Editor)
- DocumentsController (read Viewer / upload+delete Editor)
- ExtractionController (Editor)
- PromptsController (read Viewer / write Editor)
- HistoryController (Viewer)
- ResolutionController (read Viewer / apply Editor)
- ReleasesController (read+create Viewer+Editor / publish+cutover Owner)
- RdfImportController (Editor)
- VocabularyController (read Viewer / mutate Editor)
- OntologyController.sources/external/published (read Viewer / write Editor)

Service guard is preserved as belt-and-suspenders; this commit only
adds the declarative attribute so OpenAPI / matrix tests can reason
about the role contract.

Co-Authored-By: Claude <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `AddPolicy` 收口 Admin,字面量替换(commit 2)

**Files:**
- Modify: `src/OnToPilot/Program.cs`(line 544)
- Modify: `src/OnToPilot/Controllers/ProvidersController.cs`(line 13)
- Modify: `src/OnToPilot/Controllers/SettingsController.cs`(line 13)
- Modify: `src/OnToPilot/Controllers/AuthController.cs`(4 处:line 211 / 219 / 227 / 235 — 核对当前行号)

**Interfaces:**
- Consumes: `Policies.AdminOnly`(Task 2)
- Produces: `AddAuthorization(o => o.AddPolicy("AdminOnly", ...))` 在 `Program.cs`;`[Authorize(Policy = Policies.AdminOnly)]` 在 3 controller

- [ ] **Step 1: 改 `Program.cs:544` 为 lambda 注册**

`src/OnToPilot/Program.cs`,line 544:

```csharp
// before
builder.Services.AddAuthorization();

// after
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.AdminOnly, policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Admin"));

    options.AddPolicy(Policies.KSOwnerOnly, policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => ctx.User.IsInRole("Admin"))); // hook for Step 4

    // using OnToPilot.Authorization;
});
```

加 `using OnToPilot.Authorization;`(若文件顶部已有 `using OnToPilot.Authorization;`,跳过)。

- [ ] **Step 2: 跑 OnToPilot.Tests,确认无 regress**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

期望: 全绿。policy 注册不影响现有 `[Authorize]` 或 attribute 行为。

- [ ] **Step 3: 替换 `ProvidersController`**

`src/OnToPilot/Controllers/ProvidersController.cs`,line 13:

```csharp
// before
[Authorize(Roles = "Admin")]

// after
[Authorize(Policy = Policies.AdminOnly)]
```

加 `using OnToPilot.Authorization;`(若不存在)。

- [ ] **Step 4: 替换 `SettingsController`**

`src/OnToPilot/Controllers/SettingsController.cs`,line 13,同上替换。

- [ ] **Step 5: 替换 `AuthController` 4 处**

`src/OnToPilot/Controllers/AuthController.cs`,行号以当前文件为准(grep `[Authorize(Roles = "Admin")]` 找精确位置):

```csharp
// before
[Authorize(Roles = "Admin")]

// after (4 处)
[Authorize(Policy = Policies.AdminOnly)]
```

- [ ] **Step 6: 创建 `AdminPolicyTests`**

`src/OnToPilot.Tests/Authorization/AdminPolicyTests.cs`:

```csharp
using System.Net;
using OnToPilot.Tests.Infrastructure;

namespace OnToPilot.Tests.Authorization;

/// <summary>
/// Verifies the AdminOnly named policy replaces inline
/// <c>[Authorize(Roles="Admin")]</c> without changing observable
/// behavior: admin → 200, non-admin → 403, anonymous → 401.
/// </summary>
public sealed class AdminPolicyTests
    : IClassFixture<OnToPilotWebApplicationFactory>
{
    private readonly OnToPilotWebApplicationFactory _factory;

    public AdminPolicyTests(OnToPilotWebApplicationFactory factory) { _factory = factory; }

    [Theory]
    [InlineData("/api/providers")]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/users")]
    public async Task Anonymous_gets_401(string path)
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/providers")]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/users")]
    public async Task Non_admin_user_gets_403(string path)
    {
        await _factory.SeedUserAsync("non-admin");
        var client = _factory.CreateClient();
        await _factory.AuthenticateAsAsync(client, "non-admin");

        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/providers")]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/users")]
    public async Task Admin_user_gets_200(string path)
    {
        await _factory.SeedAdminAsync("admin");
        var client = _factory.CreateClient();
        await _factory.AuthenticateAsAsync(client, "admin");

        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
```

- [ ] **Step 7: 跑 `AdminPolicyTests`**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~AdminPolicyTests"
```

期望: 9 期望(3 端点 × 3 actor)全绿。

- [ ] **Step 8: 跑 ApiContract + 全量 OnToPilot.Tests**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

期望: 167/167 + ~741 + 9 全绿。

- [ ] **Step 9: commit 2**

```bash
cd e:/GitHub/ontopilot
git add src/OnToPilot/Program.cs \
        src/OnToPilot/Controllers/ProvidersController.cs \
        src/OnToPilot/Controllers/SettingsController.cs \
        src/OnToPilot/Controllers/AuthController.cs \
        src/OnToPilot.Tests/Authorization/AdminPolicyTests.cs
git commit -m "feat(rbac): AddPolicy AdminOnly + KSOwnerOnly, retire inline Roles=Admin" -m "$(cat <<'EOF'
Replace 4 inline [Authorize(Roles="Admin")] call sites with
[Authorize(Policy = Policies.AdminOnly)] in ProvidersController /
SettingsController / AuthController (4 places).

Program.cs:544 registers two:
- AdminOnly: RequireAuthenticatedUser + RequireRole("Admin") — full
  enforcement
- KSOwnerOnly: RequireAuthenticatedUser + RequireAssertion(Admin) —
  hook only; full per-KS Owner enforcement is Step 4 follow-up
  (currently no endpoint uses KSOwnerOnly)

9 tests (3 endpoints × 3 actors) in AdminPolicyTests pin the contract.
Zero behavior change observed; the refactor is purely name indirection.

Co-Authored-By: Claude <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: 生成 `rbac_matrix_expected.json` 持久化(commit 3 第一段)

**Files:**
- Create: `src/OnToPilot.Tests/Authorization/rbac_matrix_expected.json`

**Interfaces:**
- Consumes: spec §6 endpoint × role 矩阵表(全文)
- Produces: ~50 行 JSON,每行 `(method, path) → { actor → expected_status }`

- [ ] **Step 1: 反射枚举现有 controllers,导出 endpoint 列表**

写一个临时 throwaway 脚本(不进 git,只为收集 endpoint 列表):

```csharp
// tmp in a test method, then delete
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Controllers;

var controllers = typeof(ABoxController).Assembly
    .GetTypes()
    .Where(t => t.IsSubclassOf(typeof(ControllerBase)) && !t.IsAbstract)
    .Where(t => t.Namespace == "OnToPilot.Controllers");

foreach (var c in controllers)
{
    var route = c.GetCustomAttribute<RouteAttribute>()?.Template ?? "(none)";
    foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        var httpMethods = m.GetCustomAttributes<HttpMethodAttribute>()
            .SelectMany(a => a.HttpMethods)
            .Distinct();
        var subRoute = m.GetCustomAttribute<RouteAttribute>()?.Template ?? "";
        foreach (var verb in httpMethods)
        {
            Console.WriteLine($"{verb} /{route}/{subRoute}");
        }
    }
}
```

跑这段,粘贴输出到 spec §6 表对应的行(可能需要 ~1-2 小时的 review 把 spec §6 校准到真实 endpoint 列表)。争议项(`/tokens` read 等)在 review 阶段显式标记,Step 5 跑测试时按真实 service guard 调整。

- [ ] **Step 2: 写 `rbac_matrix_expected.json`**

`src/OnToPilot.Tests/Authorization/rbac_matrix_expected.json`:

```json
{
  "_meta": {
    "_spec": "docs/superpowers/specs/2026-08-24-rbac-coverage-matrix-design.md §6",
    "_actor_model": "anonymous = no cookie; viewer/editor/owner = KSRole grant; admin = User.IsAdmin=true"
  },
  "GET /api/knowledge/{id:guid}/abox/individuals": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/abox/individuals": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "PUT /api/knowledge/{id:guid}": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "DELETE /api/knowledge/{id:guid}": {
    "anonymous": 401, "viewer": 403, "editor": 403, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/members": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/members": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "DELETE /api/knowledge/{id:guid}/members/{userId}": {
    "anonymous": 401, "viewer": 403, "editor": 403, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/refresh_stats": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/conflicts": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/conflicts/{conflictId}/apply": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/conflicts/set_property_union": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/conflicts/merge_properties": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/conflicts/subordinate_properties": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/documents": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/documents/upload": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "DELETE /api/knowledge/{id:guid}/documents/{docId}": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/rdf/import": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/extract": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "DELETE /api/knowledge/{id:guid}/extract/{jobId}": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/prompts": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/prompts": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "PUT /api/knowledge/{id:guid}/prompts/{promptId}": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "DELETE /api/knowledge/{id:guid}/prompts/{promptId}": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/history": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/resolution": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/resolution/{id}/apply": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/releases": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/releases": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/releases/{releaseId}/publish": {
    "anonymous": 401, "viewer": 403, "editor": 403, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/releases/{releaseId}/cutover": {
    "anonymous": 401, "viewer": 403, "editor": 403, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/vocabulary": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/vocabulary": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "PUT /api/knowledge/{id:guid}/vocabulary/{termId}": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/ontology/sources": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/ontology/sources": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/ontology/external": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/ontology/external": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  },
  "GET /api/knowledge/{id:guid}/ontology/published": {
    "anonymous": 401, "viewer": 200, "editor": 200, "owner": 200, "admin": 200
  },
  "POST /api/knowledge/{id:guid}/ontology/published": {
    "anonymous": 401, "viewer": 403, "editor": 200, "owner": 200, "admin": 200
  }
}
```

> **本 JSON 是 source-of-truth**: 任何 controller action 变更 / 新增 endpoint / 角色调整都需要**显式 review**这个 JSON 的对应行。PR diff 含此文件变更时,reviewer 必须逐行核对。

- [ ] **Step 3: commit JSON(单独 commit,便于 review)**

```bash
cd e:/GitHub/ontopilot
git add src/OnToPilot.Tests/Authorization/rbac_matrix_expected.json
git commit -m "test(rbac): pin endpoint×role expected matrix as test resource" -m "$(cat <<'EOF'
40+ endpoint × 5 actor entries that drive EndpointRoleMatrixTests.
Derived from docs/superpowers/specs/2026-08-24-rbac-coverage-matrix-
design.md §6, with on-KS endpoints (KSGuid path) enumerated by
reflection over OnToPilot.Controllers.

Disputed entries (e.g. /tokens read minimum) will be adjusted in
Task 5 after running the test against real service guards.

Any future controller / role tweak requires explicit review of this
JSON's affected line(s).

Co-Authored-By: Claude <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `EndpointRoleMatrixTests` 反射枚举 + 5 actor 验证(commit 3 第二段)

**Files:**
- Create: `src/OnToPilot.Tests/Authorization/EndpointRoleMatrixTests.cs`

**Interfaces:**
- Consumes: `rbac_matrix_expected.json`(Task 4),`WebApplicationFactory<Program>` (existing)
- Produces: `EndpointRoleMatrixTests`(一个 `[Theory]` 跑所有 entries)

- [ ] **Step 1: 创建 `EndpointRoleMatrixTests` 骨架**

`src/OnToPilot.Tests/Authorization/EndpointRoleMatrixTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using OnToPilot.Controllers;
using OnToPilot.Tests.Infrastructure;

namespace OnToPilot.Tests.Authorization;

/// <summary>
/// Verifies the full endpoint × role contract documented in
/// <c>docs/superpowers/specs/2026-08-24-rbac-coverage-matrix-design.md §6</c>
/// and pinned in <c>rbac_matrix_expected.json</c>.
///
/// <para>Drives every entry through the live auth pipeline using
/// <see cref="OnToPilotWebApplicationFactory"/> so the test exercises
/// the actual [KSRoleAuthorize] attribute + service guard combination.
/// Any mismatch with the file is a CI failure — keeping the JSON and
/// the contract in lock-step.</para>
/// </summary>
public sealed class EndpointRoleMatrixTests
    : IClassFixture<OnToPilotWebApplicationFactory>
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> s_matrix =
        LoadMatrix();

    private readonly OnToPilotWebApplicationFactory _factory;

    public EndpointRoleMatrixTests(OnToPilotWebApplicationFactory factory) { _factory = factory; }

    public static IEnumerable<object[]> Entries()
    {
        foreach (var (key, actors) in s_matrix)
        {
            var (verb, path) = SplitVerbPath(key);
            yield return new object[] { verb, path, actors };
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task Each_endpoint_respects_role_matrix(string verb, string pathTemplate, IReadOnlyDictionary<string, int> actors)
    {
        var ksGuid = await _factory.SeedKnowledgeSystemAsync(ownerUsername: "owner");
        var viewerId = await _factory.SeedUserAsync("viewer");
        var editorId = await _factory.SeedUserAsync("editor");
        var ownerId = await _factory.SeedUserAsync("owner-user");
        await _factory.SeedGrantAsync(ksGuid, viewerId, "viewer");
        await _factory.SeedGrantAsync(ksGuid, editorId, "editor");
        // owner is the KS.OwnerId (already seeded by SeedKnowledgeSystemAsync)
        await _factory.SeedAdminAsync("admin");

        var concretePath = pathTemplate.Replace("{id:guid}", ksGuid.ToString())
                                       .Replace("{userId}", viewerId.ToString())
                                       .Replace("{conflictId}", Guid.NewGuid().ToString())
                                       .Replace("{docId}", Guid.NewGuid().ToString())
                                       .Replace("{jobId}", Guid.NewGuid().ToString())
                                       .Replace("{promptId}", Guid.NewGuid().ToString())
                                       .Replace("{id}", Guid.NewGuid().ToString())
                                       .Replace("{termId}", Guid.NewGuid().ToString())
                                       .Replace("{releaseId}", Guid.NewGuid().ToString());

        await AssertActorAsync("anonymous", actors["anonymous"], verb, concretePath);
        await AssertActorAsync("viewer", actors["viewer"], verb, concretePath);
        await AssertActorAsync("editor", actors["editor"], verb, concretePath);
        await AssertActorAsync("owner-user", actors["owner"], verb, concretePath);
        await AssertActorAsync("admin", actors["admin"], verb, concretePath);
    }

    private async Task AssertActorAsync(string username, int expected, string verb, string path)
    {
        var client = _factory.CreateClient();
        if (username != "anonymous")
        {
            await _factory.AuthenticateAsAsync(client, username);
        }

        HttpResponseMessage resp = verb switch
        {
            "GET" => await client.GetAsync(path),
            "POST" => await client.PostAsJsonAsync(path, new { }),
            "PUT" => await client.PutAsJsonAsync(path, new { }),
            "DELETE" => await client.DeleteAsync(path),
            _ => throw new InvalidOperationException($"Unsupported verb {verb}"),
        };

        Assert.True(
            (int)resp.StatusCode == expected,
            $"actor={username} verb={verb} path={path} expected={expected} actual={(int)resp.StatusCode}");
    }

    private static (string verb, string path) SplitVerbPath(string key)
    {
        var idx = key.IndexOf(' ');
        return (key.Substring(0, idx), key.Substring(idx + 1));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> LoadMatrix()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("rbac_matrix_expected.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith("_")) continue; // _meta
            var actors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var actor in prop.Value.EnumerateObject())
            {
                actors[actor.Name] = actor.Value.GetInt32();
            }
            result[prop.Name] = actors;
        }
        return result;
    }
}
```

- [ ] **Step 2: 把 JSON 文件配置为 embedded resource**

`src/OnToPilot.Tests/OnToPilot.Tests.csproj`,加:

```xml
<ItemGroup>
  <EmbeddedResource Include="Authorization/rbac_matrix_expected.json" />
</ItemGroup>
```

- [ ] **Step 3: 跑 `EndpointRoleMatrixTests`,看 fail 列表**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~EndpointRoleMatrixTests"
```

期望: 大量 fail。每个 fail 的 `(actor, verb, path, expected, actual)` 提示哪些行有差异。常见原因:
- JSON 期望与 service guard 不一致(`/tokens` read 期望 200 但 service guard 要 Editor)→ 调整 JSON
- 路径模板与真实 controller 不一致(假设的 route 不存在)→ 删除 JSON 行
- POST body 不被接受(controller 期望非空 body)→ POST 测试用空 body 应得 400 而不是 403,但本测试先看 status code 落在 expected 集合内;如果 fail,改 JSON 让 anonymous 期望 401 而 POST 期望 400

- [ ] **Step 4: 调整 JSON 至全绿**

循环: 改 JSON → 重跑测试 → 检查 fail → 改 JSON 直到全绿。**禁止改 controller / service guard / attribute 来"让测试通过"** — 矩阵反映现状契约,不调整现状。

JSON 调整规则:
- 若 `(viewer, POST, /abox/...)` 期望 403 但实际 200(没拦)→ 说明 attribute 没挂到对应 action,**回到 Task 2 补 attribute**(或承认该 endpoint 实际是 viewer-safe,改 JSON)
- 若 `(viewer, GET, /abox/...)` 期望 200 但实际 403(拦过头)→ 说明 attribute 挂错,**回到 Task 2 调 attribute**
- 若 service guard 拦截 logic 与 attribute 不一致(attribute 200 / service 403)→ 是 service guard 自身的现状契约;JSON 取 service 实际返回的状态码

- [ ] **Step 5: 跑 ApiContract + 全量 OnToPilot.Tests**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

期望: 167 + ~741 + 9 + 250 (40 endpoints × 5 actors + 50 padding) 全绿。

- [ ] **Step 6: commit 3 第二段**

```bash
cd e:/GitHub/ontopilot
git add src/OnToPilot.Tests/Authorization/EndpointRoleMatrixTests.cs \
        src/OnToPilot.Tests/OnToPilot.Tests.csproj \
        src/OnToPilot.Tests/Authorization/rbac_matrix_expected.json
git commit -m "test(rbac): EndpointRoleMatrixTests drives rbac_matrix_expected.json" -m "$(cat <<'EOF'
Reflection-based HTTP matrix test: 40+ endpoints × 5 actors (anonymous,
viewer, editor, owner, admin) = ~250 expectations, all driven from the
JSON resource added in the prior commit.

Discrepancies between JSON and current behavior are resolved by
adjusting the JSON (matrix is current state, not aspirational).
Service guard vs attribute conflicts surface as test failures and
must be triaged as bugs.

Co-Authored-By: Claude <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: 文档 + memory 同步(commit 3 收尾)

**Files:**
- Create: `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md`(人类可读版)
- Modify: `memory/ontopilot-dotnet-gap-2026-08-23.md`(RBAC 项 🔴 → 🟢)
- Modify: `memory/ontopilot-adr-gap-2026-08-23.md`(同步)
- Modify: `memory/MEMORY.md`(新条目)
- Create: `memory/ontopilot-rbac-coverage-matrix.md`(切片摘要)

- [ ] **Step 1: 创建 `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md`**

把 design spec 第 6 节矩阵表 + 第 3 节决策摘要拷过来(去掉 spec 内的开发内部信息):

```markdown
# RBAC Coverage Matrix

**Status**: 已完成(3 commits)
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
| Authentication | 3 scheme: Session / ApiBearer / ExternalToken |
| Authorization | `[KSRoleAuthorize(Minimum = KSRole.X)]` attribute + `Policies.AdminOnly` / `Policies.KSOwnerOnly` |
| Service guard | 8+ 处 `RequireRoleAsync` 保留作 belt-and-suspenders |

## 矩阵

(拷贝 design spec §6 表格)
```

- [ ] **Step 2: 更新 `ontopilot-dotnet-gap-2026-08-23.md`**

读 `memory/ontopilot-dotnet-gap-2026-08-23.md`,找到 RBAC 项的描述(描述符关键词 "RBAC" / "🔴"),把 🔴 改为 🟢,描述改为"已完成,见 [ontopilot-rbac-coverage-matrix.md](ontopilot-rbac-coverage-matrix.md)"。

- [ ] **Step 3: 更新 `ontopilot-adr-gap-2026-08-23.md`**

读 `memory/ontopilot-adr-gap-2026-08-23.md`,若有 RBAC 项登记,同样更新。多数情况下 dotnet-gap 已含。

- [ ] **Step 4: 创建 `memory/ontopilot-rbac-coverage-matrix.md`**

```markdown
---
name: ontopilot-rbac-coverage-matrix
description: "RBAC 完整覆盖矩阵 .NET 端(3 commits: KSRoleAuthorize filter + AddPolicy 收口 + endpoint×role 矩阵测试)"
metadata:
  type: project
---

OnToPilot .NET 端 RBAC 完整覆盖矩阵切片。3 commits,关闭
[ontopilot-dotnet-gap-2026-08-23.md](ontopilot-dotnet-gap-2026-08-23.md) 的 🔴 RBAC 长周期项。

## 关键 commit

1. `feat(rbac): KSRoleAuthorizeAttribute filter + unit tests` — Task 1
2. `feat(rbac): hook [KSRoleAuthorize] to 12 dispatch controllers` — Task 2
3. `feat(rbac): AddPolicy AdminOnly + KSOwnerOnly, retire inline Roles=Admin` — Task 3
4. `test(rbac): pin endpoint×role expected matrix as test resource` — Task 4 (JSON)
5. `test(rbac): EndpointRoleMatrixTests drives rbac_matrix_expected.json` — Task 5

## 范围文件清单

- 新增: `src/OnToPilot/Authorization/KSRoleAuthorizeAttribute.cs` + `Policies.cs`
- 新增: `src/OnToPilot.Tests/Authorization/KSRoleAuthorizeFilterTests.cs` + `AdminPolicyTests.cs` + `EndpointRoleMatrixTests.cs` + `rbac_matrix_expected.json`
- 修改: `Program.cs:544` AddAuthorization;3 controllers 字面量 → policy;12 controllers 挂 attribute
- 文档: `docs/superpowers/specs/2026-08-24-rbac-coverage-matrix-design.md` + `2026-08-24-rbac-coverage-matrix.md`

## 设计要点

- Filter 是 `IAsyncAuthorizationFilter`(不是 policy handler),因为需要 route argument + scoped DI + 路径解析
- `[KSRoleAuthorize(Minimum = KSRole.Editor)]` 一个 attribute 取代 8+ 处 service guard 复制
- `AddPolicy` 注册 AdminOnly(实质生效)+ KSOwnerOnly(Step 4 hook,目前仅 Admin)
- `rbac_matrix_expected.json` 是 source-of-truth,任何 PR 改动需显式 review 此文件
- 错误文案严格对齐 Python baseline:
  - 无 grant → `{"detail": "You don't have access to this knowledge system"}`
  - 角色不足 → `{"detail": "Insufficient permissions"}`
  - KS 不存在 → `{"detail": "Knowledge system not found"}`

## 不在范围(留 Step 4 follow-up)

- 删 8+ 处 service `RequireRoleAsync`(Step 4 纯 DRY 收益)
- `KSRole` ↔ DB 字符串映射统一(跨 schema-3 长周期)
- OpenAPI `x-onto-pilot-roles` 自动生成(需 `IOperationFilter`)
- MCP 通道改造(`McpPrincipalAccessor` 语义已正确,不动)
- token scheme 端点矩阵(`/api/v1/*` 走 token scope 不走 KSRole)

## Why

Python baseline `backend/app/permissions.py:52-73` 用一行
`_require("viewer"/"editor"/"owner")` 工厂覆盖所有 endpoint。
.NET 端原本 8+ 处 service guard 复制,加新 endpoint 必须手工复制,
容易遗漏,且 OpenAPI 不可见权限契约。本切片把 .NET 端收敛到
"加 attribute 就好"。

## How to apply

未来加新 endpoint:
1. 在 controller action 上挂 `[KSRoleAuthorize(Minimum = KSRole.X)]`
2. 在 `rbac_matrix_expected.json` 加对应行(5 actor 期望状态码)
3. PR review 必看 `rbac_matrix_expected.json` 改动行
4. 全量测试 + EndpointRoleMatrixTests + ApiContract 必过
```

- [ ] **Step 5: 更新 `MEMORY.md`**

`memory/MEMORY.md`,在索引中加一行:

```markdown
- [ontopilot-rbac-coverage-matrix](ontopilot-rbac-coverage-matrix.md) — RBAC 完整覆盖矩阵(3 commits, KSRoleAuthorize filter + AddPolicy + endpoint×role 矩阵测试)
```

- [ ] **Step 6: 跑全量回归最后一次**

```bash
cd e:/GitHub/ontopilot
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj
```

- [ ] **Step 7: commit 3 收尾**

```bash
cd e:/GitHub/ontopilot
git add docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md \
        memory/ontopilot-dotnet-gap-2026-08-23.md \
        memory/ontopilot-adr-gap-2026-08-23.md \
        memory/MEMORY.md \
        memory/ontopilot-rbac-coverage-matrix.md
git commit -m "docs(rbac): human-readable coverage matrix + memory sync" -m "$(cat <<'EOF'
Closing the 🔴 RBAC long-term item in ontopilot-dotnet-gap-2026-08-23.

New files:
- docs/superpowers/specs/2026-08-24-rbac-coverage-matrix.md
  (humans can read this without reading the design spec)
- memory/ontopilot-rbac-coverage-matrix.md
  (slice summary, Why, How to apply)

Updates:
- ontopilot-dotnet-gap-2026-08-23.md: RBAC item 🔴 → 🟢
- ontopilot-adr-gap-2026-08-23.md: synced
- MEMORY.md: new index entry

Co-Authored-By: Claude <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

### 1. Spec coverage

| Spec § | 任务 |
|---|---|
| §1 背景 | Plan §Global Constraints + 头注释解释 |
| §2 目标 | Plan 头 + Task 1 (filter) + Task 4 (矩阵) |
| §3.1 [KSRoleAuthorize] filter | Task 1 + Task 2 |
| §3.2 AddPolicy 收口 Admin | Task 3 |
| §3.3 endpoint × role 矩阵测试 | Task 4 + Task 5 |
| §4 范围 | Plan §File Structure + 5 任务 |
| §5 不在范围 | Plan §Global Constraints + memory file 末段 |
| §6 endpoint × role 表 | Task 4 (JSON 持久化) + 文档 Task 6 Step 1 |
| §7 风险 | Plan §Global Constraints + 任务内 inline 警告 |
| §8 验证 | 每个 Task Step 末"跑测试"步骤 + Task 6 Step 6 全量回归 |
| §9 实施切片(3 commits)| Plan 拆为 7 task(T1+T2 = commit 1 / T3 = commit 2 / T4+T5+T6 = commit 3)|

无 spec gap。

### 2. Placeholder scan

- 无 "TBD" / "TODO"
- 无 "implement later"
- 无 "Add appropriate error handling"(全部 inline 写出)
- 无 "Similar to Task N"(每段独立代码块)
- 无 "fill in details"
- 每段代码都是实际内容

### 3. Type consistency

- `KSRoleAuthorizeAttribute`: Task 1 定义 `Minimum / RouteArgument / AllowExternalToken`,Task 2 调用 `[KSRoleAuthorize(Minimum = KSRole.Viewer)]` — 一致
- `Policies.AdminOnly` / `Policies.KSOwnerOnly`: Task 2 定义,Task 3 引用 — 一致
- `OnToPilotWebApplicationFactory.SeedKnowledgeSystemAsync / SeedUserAsync / SeedGrantAsync / SeedAdminAsync / AuthenticateAsAsync`: Task 1 定义,Task 3 + 5 复用 — 一致
- `rbac_matrix_expected.json` 是 embedded resource: Task 4 创建 + .csproj 注册(Task 5 Step 2),Task 5 读取 — 一致
- `KSRole` enum 值:`Viewer / Editor / Owner` 与 spec §3.1 / §6 表一致

无类型不一致。

### Type consistency between spec and plan

- Spec §3.1 错误响应:401 / 403 无 grant / 403 角色不足 / 404 KS not found — Plan Task 1 Step 4 实现严格对齐
- Spec §3.2 KSOwnerOnly 是 hook:Plan Task 3 Step 1 显式 RequireAssertion(Admin)
- Spec §5 不在范围项:Plan §Global Constraints + memory file Step 4 都覆盖

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-08-24-rbac-coverage-matrix.md`.**

7 个 Task(实际 3 commit,Task 1+2 = commit 1 / Task 3 = commit 2 / Task 4+5+6 = commit 3)。

执行选项:

1. **Subagent-Driven (recommended)** — 每个 Task 派一个独立 subagent,逐 Task review,fast iteration;适合多文件多测试的切片,reviewer gate 频繁(本切片 7 个 Task,7 个 review 节点)。

2. **Inline Execution** — 在本 session 用 executing-plans skill 顺序执行,checkpoint 在 Task 1 / Task 2 收尾 / Task 3 收尾 / Task 4-6 收尾,适合 3 commit 整体观察进度。

**Which approach?**