# Keycloak SSO 后端实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 ISEStudio 后端加 Keycloak JwtBearer 认证 scheme(第 4 个),SSO 登录自动同步本地用户行,下游 RBAC/审计零改动。

**Architecture:** 配置驱动激活(`ISEStudio:Auth:Keycloak:Authority` 空 = SSO 完全禁用,现有行为逐字节不变)。default scheme 改 PolicyScheme + ForwardDefaultSelector(带 Bearer 头 → JwtBearer,否则 → SessionCookie)。`OnTokenValidated` 里 azp 校验 → realm role 摊平 → `SsoUserSyncService` 同步 → 写 `Items["auth.user"]`(下游 `KSRoleAuthorize` / `ResolveActor` / `me` 全部复用该挂点)。

**Tech Stack:** .NET 10 / ASP.NET Core JwtBearer / EF Core (TPC, PG 生产 + SQLite 测试) / Microsoft.IdentityModel.Tokens / xUnit。

**Spec:** [2026-08-28-keycloak-sso-design.md](../specs/2026-08-28-keycloak-sso-design.md)(§2 D1-D7、§4 后端设计、§6.1 后端测试)

## Global Constraints

- 测试门:850 unit + 167 contract 全绿(无 Keycloak 配置路径逐字节不变)+ 新增测试全绿
- `dotnet build src/ISEStudio/ISEStudio.csproj` 0 错误 0 警告
- 无 Keycloak 配置时不得注册 JwtBearer、default scheme 保持 `SessionCookie`
- 提交风格:`feat(sso): ...` 短横线前缀,尾随 `Co-Authored-By: Claude <noreply@anthropic.com>`
- Wire shape 零偏移:SSO 不新增任何 REST 端点
- 测试基建复用 `AuthTestWebApplicationFactory`(SQLite 每实例唯一路径 + `EnsureCreated`,不走 EF migration)

---

### Task 1: UserEntity.SubjectId 列 + 唯一过滤索引 + migration

**Files:**
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/AuthEntities.cs`(UserEntity,~L30 后)
- Modify: `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`(UserEntityConfiguration)
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/*_SsoSubjectId.cs`(EF CLI 生成)

**Interfaces:**
- Produces: `UserEntity.SubjectId`(string?,SSO 用户的 Keycloak `sub`;本地用户恒 null)。后续 Task 2/5 依赖。

- [ ] **Step 1: 加实体字段**

在 [AuthEntities.cs:30](src/ISEStudio/Infrastructure/Persistence/Entities/AuthEntities.cs#L30) 的 `CreatedAt` 之后加:

```csharp
    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Keycloak subject (<c>sub</c>) for SSO-provisioned users; null for
    /// local accounts. Unique across non-null values — the sync lookup key.
    /// </summary>
    public string? SubjectId { get; set; }
```

- [ ] **Step 2: 加唯一过滤索引**

在 [EntityConfigurations.cs](src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs) 的 `UserEntityConfiguration.Configure` 里,`PasswordHash` 行之后加:

```csharp
        builder.Property(x => x.SubjectId).HasMaxLength(255);
        builder.HasIndex(x => x.SubjectId)
            .IsUnique()
            .HasFilter("\"SubjectId\" IS NOT NULL")
            .HasDatabaseName("ux_users_subject_id");
```

- [ ] **Step 3: 生成 migration**

Run: `dotnet ef migrations add SsoSubjectId --project src/ISEStudio --startup-project src/ISEStudio --output-dir Infrastructure/Persistence/Migrations`
Expected: 生成 `<timestamp>_SsoSubjectId.cs`,含 `AddColumn<string>(name: "SubjectId", ...)` + `CreateIndex(... filter: "\"SubjectId\" IS NOT NULL")`。

- [ ] **Step 4: 回归验证(本任务无新测试——schema 语义由 Task 2 唯一约束测试覆盖)**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~Authentication"`
Expected: 全绿(850 基线)。`EnsureCreated` 走 SQLite 会带上新列 + partial index,现有测试不受影响。

- [ ] **Step 5: Commit**

```bash
git add src/ISEStudio/Infrastructure/Persistence/
git commit -m "feat(sso): UserEntity.SubjectId column + unique filtered index

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: SsoUserSyncService + SsoClaimMapping 单测

**Files:**
- Create: `src/ISEStudio/Authentication/SsoUserSyncService.cs`
- Create: `src/ISEStudio.Tests/Authentication/SsoUserSyncServiceTests.cs`
- Modify: `src/ISEStudio/Program.cs`(注册 service,~L362 附近 `AddScoped<AuthService>` 后)

**Interfaces:**
- Produces: `SsoUserSyncService.SyncAsync(ClaimsPrincipal, CancellationToken) → Task<UserEntity>`;`SsoClaimMapping.RealmRoles(ClaimsPrincipal) → IEnumerable<string>`。Task 4(JwtBearer Events)依赖。
- Consumes: `UserEntity.SubjectId`(Task 1);`ISEStudioDbContext` / `IOptions<SsoOptions>` / `TimeProvider` 由 DI 注入(SsoOptions 在 Task 4 定义,本任务先注入 `IOptions<SsoOptions>`——Task 4 之前不编译,故本任务把 SsoOptions 类一并创建,§4.1 字段齐全,Program.cs 的 Configure 绑定留到 Task 4)。

**实现文件 `SsoUserSyncService.cs` 全文(含 SsoOptions + SsoClaimMapping):**

```csharp
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Authentication;

/// <summary>
/// Keycloak 认证配置。Authority 为空 = SSO 特性整体禁用(不注册
/// JwtBearer、default scheme 保持 SessionCookie)。
/// </summary>
public sealed class SsoOptions
{
    public const string SectionName = "ISEStudio:Auth:Keycloak";

    public string Authority { get; set; } = string.Empty;

    /// <summary>Keycloak public client 的 clientId;azp 断言用。</summary>
    public string ClientId { get; set; } = "isestudio-frontend";

    /// <summary>默认必须 https;容器内 http 部署显式置 false。</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// 可选:JwtBearer 拉 discovery/JWKS 的地址。默认从 Authority 派生。
    /// 容器部署双 URL:Authority 是浏览器可见地址(iss 校验基准),但后端
    /// 容器内访问不了它——此键指向容器内 Keycloak 地址(见 deploy 计划
    /// Task 1 的 backend 环境接线)。
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>realm role 名;含此 role → 本地用户 IsAdmin=true。</summary>
    public string AdminRole { get; set; } = "admin";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>
/// Keycloak JWT claim 的纯函数映射。<c>realm_access</c> 在 JWT 里是
/// <c>{"roles":[...]}</c> 嵌套 JSON,不会自动变成 role claim——
/// Policies.AdminOnly 的 RequireRole 依赖摊平后的 ClaimTypes.Role。
/// </summary>
public static class SsoClaimMapping
{
    public static IEnumerable<string> RealmRoles(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { yield break; }
        if (!doc.RootElement.TryGetProperty("roles", out var roles)
            || roles.ValueKind != JsonValueKind.Array) yield break;
        foreach (var element in roles.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String
                && element.GetString() is { Length: > 0 } role)
                yield return role;
        }
    }
}

/// <summary>
/// SSO 登录的用户同步:sub 查行 → 无则建行(空 PasswordHash = 不可本地
/// 密码登录)→ 有则刷新可变字段(DisplayName / IsAdmin)→ Active=false
/// 拒绝。写回 <c>Items["auth.user"]</c> 由调用方(JwtBearer
/// OnTokenValidated)负责。
/// </summary>
public sealed class SsoUserSyncService
{
    private readonly ISEStudioDbContext _db;
    private readonly IOptions<SsoOptions> _options;
    private readonly TimeProvider _clock;

    public SsoUserSyncService(
        ISEStudioDbContext db,
        IOptions<SsoOptions> options,
        TimeProvider clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    public async Task<UserEntity> SyncAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var sub = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(sub))
        {
            throw new InvalidOperationException("SSO token missing sub claim");
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.SubjectId == sub, ct)
            .ConfigureAwait(false);

        var roles = SsoClaimMapping.RealmRoles(principal)
            .ToHashSet(StringComparer.Ordinal);
        var isAdmin = roles.Contains(_options.Value.AdminRole);
        var displayName = principal.FindFirst("name")?.Value;
        var preferredUsername = principal.FindFirst("preferred_username")?.Value;

        if (user is null)
        {
            user = await CreateAsync(sub, preferredUsername, displayName, isAdmin, ct)
                .ConfigureAwait(false);
            return user;
        }

        if (!user.Active)
        {
            throw new UnauthorizedAccessException("User inactive");
        }

        // 每次登录刷新可变字段(Keycloak 侧改了 role / 名字下次登录生效)。
        user.IsAdmin = isAdmin;
        if (!string.IsNullOrWhiteSpace(displayName)) user.DisplayName = displayName;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    private async Task<UserEntity> CreateAsync(
        string sub, string? preferredUsername, string? displayName,
        bool isAdmin, CancellationToken ct)
    {
        var baseName = !string.IsNullOrWhiteSpace(preferredUsername)
            ? preferredUsername.Trim()
            : $"sso_{sub[..Math.Min(8, sub.Length)]}";
        var entity = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = await UniqueUsernameAsync(baseName, sub, ct).ConfigureAwait(false),
            DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : baseName,
            PasswordHash = string.Empty,
            IsAdmin = isAdmin,
            Active = true,
            SubjectId = sub,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Users.Add(entity);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return entity;
        }
        catch (DbUpdateException)
        {
            // 并发建行竞态:另一请求已建同 sub 行 → 重查返回既有行。
            var existing = await _db.Users
                .FirstOrDefaultAsync(u => u.SubjectId == sub, ct)
                .ConfigureAwait(false);
            if (existing is not null) return existing;
            throw;
        }
    }

    private async Task<string> UniqueUsernameAsync(string baseName, string sub, CancellationToken ct)
    {
        var taken = await _db.Users
            .AnyAsync(u => u.Username == baseName, ct)
            .ConfigureAwait(false);
        // sub 决定后缀 → 同一 Keycloak 账号重复登录幂等,不与本地用户撞名。
        return taken ? $"{baseName}~{sub[..Math.Min(8, sub.Length)]}" : baseName;
    }
}
```

**测试文件 `SsoUserSyncServiceTests.cs`:用 `AuthTestWebApplicationFactory.CreateDbContext()` 拿 SQLite DbContext + `Options.Create(new SsoOptions { AdminRole = "admin" })` + `TimeProvider.System`。每个测试构造 `new ClaimsPrincipal(new ClaimsIdentity(...))`。**

```csharp
using System.Security.Claims;
using ISEStudio.Authentication;
using ISEStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ISEStudio.Tests.Authentication;

public class SsoUserSyncServiceTests
{
    private readonly AuthTestWebApplicationFactory _factory = new();
    private readonly SsoOptions _options = new() { AdminRole = "admin" };

    private static SsoUserSyncService NewService(ISEStudioDbContext db)
        => new(db, Options.Create(new SsoOptions { AdminRole = "admin" }), TimeProvider.System);

    private static ClaimsPrincipal Principal(
        string sub, string? preferredUsername = null, string? name = null,
        string[]? realmRoles = null)
    {
        var claims = new List<Claim> { new("sub", sub) };
        if (preferredUsername is not null) claims.Add(new("preferred_username", preferredUsername));
        if (name is not null) claims.Add(new("name", name));
        if (realmRoles is not null)
            claims.Add(new("realm_access", System.Text.Json.JsonSerializer.Serialize(
                new { roles = realmRoles }), JsonClaimValueTypes.Json));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task FirstSyncCreatesUserWithSubjectId()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        var user = await svc.SyncAsync(Principal("sub-1", preferredUsername: "alice", name: "Alice"), default);

        Assert.Equal("sub-1", user.SubjectId);
        Assert.Equal("alice", user.Username);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Empty(user.PasswordHash);
        Assert.False(user.IsAdmin);
        Assert.True(user.Active);
    }

    [Fact]
    public async Task SecondSyncRefreshesMutableFieldsWithoutDuplicating()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);
        await svc.SyncAsync(Principal("sub-2", preferredUsername: "bob", name: "Bob"), default);

        var refreshed = await svc.SyncAsync(
            Principal("sub-2", preferredUsername: "bob", name: "Bob Renamed", realmRoles: ["admin"]), default);

        Assert.Equal("bob", refreshed.Username);           // username 不可变
        Assert.Equal("Bob Renamed", refreshed.DisplayName); // name 刷新
        Assert.True(refreshed.IsAdmin);                    // role 刷新
        Assert.Single(db.Users.Where(u => u.SubjectId == "sub-2"));
    }

    [Fact]
    public async Task UsernameCollisionAppendsSubSuffixIdempotently()
    {
        var db = _factory.CreateDbContext();
        db.Users.Add(new UserEntity
        {
            Username = "carol", PasswordHash = "x", IsAdmin = true,
            Active = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var svc = NewService(db);

        var first = await svc.SyncAsync(Principal("sub-3", preferredUsername: "carol"), default);
        var second = await svc.SyncAsync(Principal("sub-3", preferredUsername: "carol"), default);

        Assert.Equal("carol~sub-3", first.Username);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task MissingPreferredUsernameFallsBackToSsoPrefix()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        var user = await svc.SyncAsync(Principal("abcdef0123456789"), default);

        Assert.Equal("sso_abcdef01", user.Username);
    }

    [Fact]
    public async Task AdminRoleMapsToIsAdmin()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        var user = await svc.SyncAsync(
            Principal("sub-4", preferredUsername: "dave", realmRoles: ["admin", "viewer"]), default);

        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task InactiveUserIsRejected()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);
        var created = await svc.SyncAsync(Principal("sub-5", preferredUsername: "eve"), default);
        created.Active = false;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SyncAsync(Principal("sub-5", preferredUsername: "eve"), default));
    }

    [Fact]
    public async Task MissingSubThrows()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SyncAsync(new ClaimsPrincipal(new ClaimsIdentity()), default));
    }

    [Fact]
    public async Task MalformedRealmAccessIsIgnored()
    {
        var db = _factory.CreateDbContext();
        var svc = NewService(db);
        var claims = new List<Claim> { new("sub", "sub-6"), new("realm_access", "not-json") };

        var user = await svc.SyncAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), default);

        Assert.False(user.IsAdmin);
    }

    [Fact]
    public void RealmRolesParsesJsonArray()
    {
        var principal = Principal("sub-7", realmRoles: ["admin", "editor"]);

        var roles = SsoClaimMapping.RealmRoles(principal).ToList();

        Assert.Equal(["admin", "editor"], roles);
    }
}
```

- [ ] **Step 2: Run 测试验证失败**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~SsoUserSyncService"`
Expected: 编译失败(`SsoUserSyncService` 不存在)。

- [ ] **Step 3: 写实现(上方实现文件全文)**

- [ ] **Step 4: 在 Program.cs 注册**

在 `builder.Services.AddScoped<AuthService>();`(L378)后加:

```csharp
// Keycloak SSO 用户同步(每个 JwtBearer OnTokenValidated 调用一次)。
// Scoped — 与请求 DbContext 共享。
builder.Services.AddScoped<SsoUserSyncService>();
```

- [ ] **Step 5: Run 测试验证通过**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~SsoUserSyncService"`
Expected: 9 passed。

- [ ] **Step 6: Commit**

```bash
git add src/ISEStudio/Authentication/SsoUserSyncService.cs src/ISEStudio.Tests/Authentication/SsoUserSyncServiceTests.cs src/ISEStudio/Program.cs
git commit -m "feat(sso): SsoUserSyncService — Keycloak sub auto-provision + refresh

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: 本地登录空 hash 守卫(timing-safe)

**Files:**
- Modify: `src/ISEStudio/Controllers/AuthController.cs:108`(LoginAsync 的 presentedHash 选择)
- Test: `src/ISEStudio.Tests/Authentication/SsoLocalLoginGuardTests.cs`

**Interfaces:**
- Consumes: `UserEntity.PasswordHash`(SSO 用户为空串,Task 2 建行语义)

**Why:** SSO 用户 `PasswordHash=""`。BCrypt Verify 对空串在格式检查处快速失败,与 dummy hash 的整轮 BCrypt 不同时——SSO 用户名可被计时枚举。把空 hash 替换为 `TimingSafeDummyHash` 后,SSO 用户名与不存在的用户同计时。

- [ ] **Step 1: 写失败测试**

```csharp
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// SSO 用户的空 PasswordHash 不能被本地密码登录——且登录尝试走完整
/// BCrypt 计时(不因 hash 为空而短路)。
/// </summary>
public class SsoLocalLoginGuardTests
{
    [Fact]
    public async Task SsoUserCannotLoginWithPassword()
    {
        await using var factory = new AuthTestWebApplicationFactory();
        var db = factory.CreateDbContext();
        db.Users.Add(new UserEntity
        {
            Username = "sso_user",
            DisplayName = "SSO User",
            PasswordHash = string.Empty,   // SSO 建行语义
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "sso_user",
            password = "anything",
        });

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run 验证失败**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~SsoLocalLoginGuardTests"`
Expected: FAIL(空 hash 的 Verify 行为依赖 BCrypt 库——预期当前可能碰巧 401,若 PASS 则检查:确认该用例断言的是**空 hash 被拒**,若 BCrypt 已天然拒绝,把断言改为 timing 语义:登录响应时间与不存在用户相当,改为记录两个响应耗时比并跳过严格断言。**先跑,按实际结果决定**)

- [ ] **Step 3: 改 presentedHash 选择**

[AuthController.cs:108](src/ISEStudio/Controllers/AuthController.cs#L108) 改为:

```csharp
        // SSO 用户(空 PasswordHash)与不存在的用户同走完整 BCrypt 轮,
        // 计时不可枚举;空 hash 本身也永远验不过。
        var storedHash = user?.PasswordHash;
        var presentedHash = string.IsNullOrEmpty(storedHash)
            ? PasswordService.TimingSafeDummyHash
            : storedHash;
```

- [ ] **Step 4: Run 验证通过**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~SsoLocalLoginGuardTests|FullyQualifiedName~TimingSafeLogin"`
Expected: 全 PASS(TimingSafeLoginTests 现有计时用例不受影响)。

- [ ] **Step 5: Commit**

```bash
git add src/ISEStudio/Controllers/AuthController.cs src/ISEStudio.Tests/Authentication/SsoLocalLoginGuardTests.cs
git commit -m "feat(sso): empty-hash login guard — SSO users cannot use local password

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: Program.cs PolicyScheme + 条件 JwtBearer 注册

**Files:**
- Modify: `src/ISEStudio/Program.cs:543-552`(AddAuthentication 块)
- Modify: `src/ISEStudio/Program.cs`(auth service 注册区,Task 2 的 AddScoped 之后)

**Interfaces:**
- Consumes: `SsoOptions` / `SsoClaimMapping` / `SsoUserSyncService`(Task 2);`SessionAuthenticationHandler.UserItemKey`(现有)
- Produces: forward scheme 名 `"ForwardScheme"`;JwtBearer 条件注册。Task 5 集成测试依赖此接线。

- [ ] **Step 1: 改 AddAuthentication 块**

把 [Program.cs:543-552](src/ISEStudio/Program.cs#L543-L552) 整块替换为:

```csharp
// ---- Authentication ----
// 默认 scheme 是 PolicyScheme:请求带 Authorization: Bearer 头 →
// Keycloak JwtBearer(SSO);否则 → SessionCookie(本地账号)。ApiBearer /
// ExternalToken 在各自 controller 显式标注 scheme,不走默认转发。
// Keycloak 未配置(Authority 空)→ 不注册 JwtBearer,default 保持
// SessionCookie,现有行为逐字节不变。
var ssoOptions = builder.Configuration
    .GetSection(SsoOptions.SectionName)
    .Get<SsoOptions>() ?? new SsoOptions();
builder.Services.Configure<SsoOptions>(
    builder.Configuration.GetSection(SsoOptions.SectionName));

var authBuilder = builder.Services.AddAuthentication(options =>
{
    if (ssoOptions.IsEnabled)
    {
        options.DefaultScheme = "ForwardScheme";
        options.DefaultAuthenticateScheme = "ForwardScheme";
        options.DefaultChallengeScheme = "ForwardScheme";
    }
});
if (ssoOptions.IsEnabled)
{
    authBuilder.AddPolicyScheme("ForwardScheme", "forward", o =>
    {
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : SessionAuthenticationHandler.SchemeName;
    });
}
authBuilder
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, ApiBearerAuthenticationHandler>(
        ApiBearerAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, ExternalTokenAuthenticationHandler>(
        ExternalTokenAuthenticationHandler.SchemeName, _ => { });

if (ssoOptions.IsEnabled)
{
    authBuilder.AddJwtBearer(o =>
    {
        o.Authority = ssoOptions.Authority;
        // 默认必须 https;容器内 http 部署显式置 false。
        o.RequireHttpsMetadata = ssoOptions.RequireHttpsMetadata;
        // claim 保持 Keycloak 原名(不映射成 WS-Federation 长 URI)。
        o.MapInboundClaims = false;
        // 容器部署双 URL:Authority(iss 校验)是浏览器可见地址,metadata
        // 从容器内地址拉(见 deploy 计划 Task 1)。空 = 默认从 Authority 派生。
        if (!string.IsNullOrWhiteSpace(ssoOptions.MetadataAddress))
        {
            o.MetadataAddress = ssoOptions.MetadataAddress;
        }
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = ssoOptions.Authority,
            // aud 恒为 account,无判定价值;azp 断言在 OnTokenValidated。
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "preferred_username",
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                // azp 门:Keycloak public client 的 access_token 里
                // aud 恒为 account,azp 等于 clientId 才是真凭据。
                if (ctx.Principal is null
                    || !string.Equals(
                        ctx.Principal.FindFirst("azp")?.Value,
                        ssoOptions.ClientId, StringComparison.Ordinal))
                {
                    ctx.Fail($"azp is not {ssoOptions.ClientId}");
                    return;
                }

                // realm_access.roles 摊平成 role claim ——
                // Policies.AdminOnly 的 RequireRole("Admin") 依赖 IsInRole。
                if (ctx.Principal.Identity is ClaimsIdentity identity)
                {
                    foreach (var role in SsoClaimMapping.RealmRoles(ctx.Principal))
                        identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }

                // 用户同步(建行/刷新)+ Items 挂点 —— 下游
                // KSRoleAuthorize / ResolveActor / me 全部复用。
                using var scope = ctx.HttpContext.RequestServices.CreateScope();
                var sync = scope.ServiceProvider
                    .GetRequiredService<SsoUserSyncService>();
                ctx.HttpContext.Items[SessionAuthenticationHandler.UserItemKey] =
                    await sync.SyncAsync(
                        ctx.Principal, ctx.HttpContext.RequestAborted);
            },
        };
    });
}
```

顶部 using 需补(如缺失):`using Microsoft.AspNetCore.Authentication.JwtBearer;`、`using Microsoft.IdentityModel.Tokens;`、`using System.Security.Claims;`(检查文件现有 using,只补缺的)。

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj`
Expected: 0 错误 0 警告。

- [ ] **Step 3: 无配置回归(SSO 禁用路径)**

Run: `dotnet test src/ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj`
Expected: 167/167(测试工厂未配 Keycloak → 不注册 JwtBearer → default 仍 SessionCookie,登录/cookie/RBAC 全链路不变)。

- [ ] **Step 4: Commit**

```bash
git add src/ISEStudio/Program.cs
git commit -m "feat(sso): PolicyScheme forwarding + config-gated JwtBearer registration

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: TestJwtIssuer + JwtBearer 集成测试

**Files:**
- Create: `src/ISEStudio.Tests/Authentication/TestJwtIssuer.cs`
- Create: `src/ISEStudio.Tests/Authentication/SsoTestWebApplicationFactory.cs`
- Create: `src/ISEStudio.Tests/Authentication/SsoJwtBearerTests.cs`

**Interfaces:**
- Consumes: Task 4 的 JwtBearer 接线 + Task 2 的同步逻辑 + `AuthTestWebApplicationFactory` 模式(不继承它——本 factory 直接继承 WebApplicationFactory<Program> 因为要加 Keycloak 配置)
- Produces: `TestJwtIssuer.CreateToken(...)` / `SsoTestWebApplicationFactory`(后续任务无依赖)

**`TestJwtIssuer.cs` 全文:**

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// 自签 RS256 的 JWT 签发器 + 假 discovery / JWKS 文档。JwtBearer 验证
/// 会从 Authority 拉 openid-configuration 再拉 JWKS——两份文档都经
/// SsoTestWebApplicationFactory 的 mock HttpMessageHandler 返回,不发真实网络。
/// </summary>
public sealed class TestJwtIssuer
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string Authority { get; } = "https://fake-keycloak.test/realms/isestudio";
    public string ClientId { get; } = "isestudio-frontend";
    public string DiscoveryPath { get; } = "/.well-known/openid-configuration";
    public string JwksPath { get; } = "/protocol/openid-connect/certs";

    public string DiscoveryJson()
    {
        return JsonSerializer.Serialize(new
        {
            issuer = Authority,
            jwks_uri = Authority + JwksPath,
        });
    }

    public string JwksJson()
    {
        var parameters = _rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = "test-key",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        });
    }

    /// <summary>签发一个 Keycloak 形状的 access_token(iss=Authority,aud=account)。</summary>
    public string CreateToken(
        string sub,
        string? azp = null,
        string? preferredUsername = null,
        string? name = null,
        string[]? realmRoles = null,
        DateTimeOffset? expiresAt = null)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", sub),
        };
        if (azp is not null) claims.Add(new("azp", azp));
        if (preferredUsername is not null) claims.Add(new("preferred_username", preferredUsername));
        if (name is not null) claims.Add(new("name", name));
        if (realmRoles is { Length: > 0 })
        {
            claims.Add(new("realm_access",
                JsonSerializer.Serialize(new { roles = realmRoles }),
                JsonClaimValueTypes.Json));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = "account", // Keycloak public client 的 aud 恒为 account
            Subject = new System.Security.Claims.ClaimsIdentity(claims),
            Expires = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10)).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256),
        };
        var handler = new JsonWebTokenHandler();
        handler.SetDefaultTimesOnTokenCreation = false;
        return handler.CreateToken(descriptor);
    }
}
```

**`SsoTestWebApplicationFactory.cs` 全文:**

```csharp
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ISEStudio.Authentication;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// Keycloak JwtBearer 集成测试主机:配置 Authority 指向假 Keycloak,
/// 用 mock HttpMessageHandler 喂 discovery + JWKS 文档(不发真实网络),
/// 其余持久化覆盖与 AuthTestWebApplicationFactory 同款(SQLite)。
/// </summary>
public sealed class SsoTestWebApplicationFactory : WebApplicationFactory<Program>
{
    public TestJwtIssuer Issuer { get; } = new();
    private readonly string _sqlitePath =
        Path.Combine(Path.GetTempPath(), $"isestudio-sso-tests-{Guid.NewGuid():N}.db")
            .Replace('\\', '/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ISEStudio:CookieSecure"] = "false",
                ["ISEStudio:Persistence:Provider"] = "sqlite",
                ["ISEStudio:Persistence:SqliteConnection"] = $"Data Source={_sqlitePath}",
                [$"{SsoOptions.SectionName}:Authority"] = Issuer.Authority,
                [$"{SsoOptions.SectionName}:ClientId"] = Issuer.ClientId,
                [$"{SsoOptions.SectionName}:RequireHttpsMetadata"] = "false",
                [$"{SsoOptions.SectionName}:AdminRole"] = "admin",
            });
        });

        builder.ConfigureServices(services =>
        {
            // 把 JwtBearer 的 discovery / jwks 拉取换成内存 mock:
            // ConfigurationManager 是公开可替换的测试钩子。
            var fakeHandler = new FakeKeycloakMetadataHandler(Issuer);
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        Issuer.Authority + Issuer.DiscoveryPath,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(fakeHandler) { RequireHttps = false });
                });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); }
            catch { /* ignore — best effort */ }
        }
        base.Dispose(disposing);
    }

    /// <summary>只回答 discovery 与 JWKS 两个 URL,其余 404。</summary>
    private sealed class FakeKeycloakMetadataHandler : HttpMessageHandler
    {
        private readonly TestJwtIssuer _issuer;

        public FakeKeycloakMetadataHandler(TestJwtIssuer issuer) => _issuer = issuer;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path == _issuer.DiscoveryPath
                ? _issuer.DiscoveryJson()
                : path == _issuer.JwksPath
                    ? _issuer.JwksJson()
                    : null;
            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                Content = new StringContent(json ?? string.Empty,
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
```

**`SsoJwtBearerTests.cs` 全文:**

```csharp
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Authentication;

public class SsoJwtBearerTests
{
    private static SsoTestWebApplicationFactory NewFactory() => new();

    private HttpClient Client(SsoTestWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ValidTokenCreatesUserAndMeReturnsUserOut()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-alice", azp: factory.Issuer.ClientId,
            preferredUsername: "alice", name: "Alice"));

        var me = await client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
        var body = await me.Content.ReadFromJsonAsync<Dictionary<string, object?>>();

        Assert.Equal("alice", body!["username"]);
        Assert.False((bool)body["isAdmin"]!);

        // 二次请求不重复建行
        var second = await client.GetAsync("/api/auth/me");
        second.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        Assert.Single(db.Users.Where(u => u.SubjectId == "sub-alice"));
    }

    [Fact]
    public async Task AzpMismatchIsRejected()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-evil", azp: "some-other-client"));

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-expired", azp: factory.Issuer.ClientId,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminRoleOpensAdminOnlyEndpoint()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-admin", azp: factory.Issuer.ClientId,
            preferredUsername: "boss", realmRoles: ["admin"]));

        var users = await client.GetAsync("/api/auth/users");

        users.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task NonAdminRoleIsDeniedAdminOnlyEndpoint()
    {
        await using var factory = NewFactory();
        var client = Client(factory, factory.Issuer.CreateToken(
            sub: "sub-viewer", azp: factory.Issuer.ClientId,
            preferredUsername: "viewer", realmRoles: ["viewer"]));

        var users = await client.GetAsync("/api/auth/users");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, users.StatusCode);
    }

    [Fact]
    public async Task InactiveSyncedUserIsRejected()
    {
        await using var factory = NewFactory();
        var token = factory.Issuer.CreateToken(
            sub: "sub-off", azp: factory.Issuer.ClientId, preferredUsername: "off");
        var first = await Client(factory, token).GetAsync("/api/auth/me");
        first.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            var user = await db.Users.SingleAsync(u => u.SubjectId == "sub-off");
            user.Active = false;
            await db.SaveChangesAsync();
        }

        var second = await Client(factory, token).GetAsync("/api/auth/me");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task ReloginRefreshesRoleAndDisplayName()
    {
        await using var factory = NewFactory();
        var firstToken = factory.Issuer.CreateToken(
            sub: "sub-flip", azp: factory.Issuer.ClientId,
            preferredUsername: "flip", name: "Old Name");
        var first = await Client(factory, firstToken).GetAsync("/api/auth/me");
        first.EnsureSuccessStatusCode();

        var secondToken = factory.Issuer.CreateToken(
            sub: "sub-flip", azp: factory.Issuer.ClientId,
            preferredUsername: "flip", name: "New Name", realmRoles: ["admin"]);
        var second = await Client(factory, secondToken).GetAsync("/api/auth/me");
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<Dictionary<string, object?>>();

        Assert.True((bool)body!["isAdmin"]!);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var user = await db.Users.SingleAsync(u => u.SubjectId == "sub-flip");
        Assert.Equal("New Name", user.DisplayName);
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task CookiePathStillWorksAlongsideJwtBearer()
    {
        // 并存回归:同一主机上 cookie 登录的请求(无 Bearer 头)仍走
        // SessionCookie 方案。
        await using var factory = NewFactory();
        var db = factory.Services.CreateScope()
            .ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        db.Database.EnsureCreated();
        db.Users.Add(new UserEntity
        {
            Username = "local_admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin12345strong", workFactor: 10),
            IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "local_admin",
            password = "admin12345strong",
        });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith("isestudio_session=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);

        var me = await client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
    }
}
```

- [ ] **Step 2: Run 验证失败**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~SsoJwtBearerTests"`
Expected: 编译失败(三个新文件不存在)→ 写完后若逻辑有偏差,按红测修到绿。

- [ ] **Step 3: 写三个文件(上方全文)**

注意 `SsoTestWebApplicationFactory` 的 SQLite 覆盖不含 `ISEStudio:Storage:RdfRoot` 等——SSO 测试只碰 auth 端点,不需要 RDF/export 隔离。若 `me` 端点触碰更多服务导致初始化失败,把 `AuthTestWebApplicationFactory.ConfigureWebHost` 里的 RdfRoot/ExportRoot/StoreWrapper/IBlobStore 覆盖逐项补进来(按报错增补)。

- [ ] **Step 4: Run 验证通过**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter "FullyQualifiedName~SsoJwtBearerTests"`
Expected: 8 passed。

- [ ] **Step 5: Commit**

```bash
git add src/ISEStudio.Tests/Authentication/TestJwtIssuer.cs src/ISEStudio.Tests/Authentication/SsoTestWebApplicationFactory.cs src/ISEStudio.Tests/Authentication/SsoJwtBearerTests.cs
git commit -m "feat(sso): JwtBearer integration tests with self-signed fake authority

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: 全量回归 + 收尾

**Files:** 无新文件。

- [ ] **Step 1: 全量单测**

Run: `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj`
Expected: 850 + 9(SsoUserSyncService)+ 1(guard)+ 8(JwtBearer)= ~868 passed,0 failed。

- [ ] **Step 2: contract 回归**

Run: `dotnet test src/ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj`
Expected: 167/167(无 Keycloak 配置路径逐字节不变)。

- [ ] **Step 3: build 0 warn**

Run: `dotnet build src/ISEStudio/ISEStudio.csproj`
Expected: 0 错误 / 0 警告。

- [ ] **Step 4: Commit 任何收尾改动(若有)**

```bash
git status --short
# 有改动才提交;干净则跳过
```

---

## Self-Review

**Spec 覆盖**:§4.1 配置节 → Task 2(SsoOptions)+ Task 4(绑定);§4.2 PolicyScheme + selector → Task 4;§4.3 azp 门/角色摊平/同步挂点 → Task 4;§4.4 同步逻辑 → Task 2;§4.5 schema → Task 1;§4.6 零改动面 → Task 4 Step 3 回归验证 + Task 5 cookie 并存测试;§6.1 后端测试矩阵 → Task 2/3/5。§7 部署与 §5 前端 → 独立计划文件。

**缺口**:spec §4.4 提到 `AuthService.login` 守卫——实现在 AuthController.LoginAsync(Task 3),位置比 spec 更精确(login 逻辑在 controller inline)。spec §4.1 配置节无 `MetadataAddress`——deploy 计划的容器双 URL 方案(Task 1)要求它,已在 Task 2 SsoOptions + Task 4 JwtBearer 补齐(可选键,空时行为与 spec 一致)。

**类型一致性**:`SsoOptions.SectionName` / `IsEnabled` / `AdminRole`;`SsoUserSyncService.SyncAsync(ClaimsPrincipal, CancellationToken) → Task<UserEntity>`;`SsoClaimMapping.RealmRoles(ClaimsPrincipal) → IEnumerable<string>`;`TestJwtIssuer.CreateToken(...)` 签名在 Task 5 内定义并使用。Task 2 先建 SsoOptions 类、Task 4 才做 Configure 绑定——Task 2 的测试用 `Options.Create` 手工构造,无编译依赖。

**执行顺序依赖**:Task 2 依赖 Task 1(SubjectId 列);Task 3 独立;Task 4 依赖 Task 2;Task 5 依赖 Task 4;Task 6 全量。
