# OnToPilot .NET 基础设施实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐项执行。步骤使用 `- [ ]` 跟踪。

**目标：** 建立可构建的 .NET 10 解决方案，完整映射 24 个关系实体，并兼容现有 Session、权限、启动恢复和健康检查行为。

**架构：** Web 项目只负责 HTTP 与组合根，Domain 保存不依赖 ASP.NET Core 的类型，Infrastructure 承担 EF Core 与认证存储。现有整数 API ID 通过 `LegacyId` 兼容，内部关系使用 GUID。

**技术栈：** .NET 10、ASP.NET Core 10、EF Core 10、Npgsql、xUnit、Testcontainers.PostgreSql、BCrypt.Net-Next

## 全局约束

- 错误响应保持 FastAPI 的 `{"detail": ...}`，不向客户端暴露默认 ProblemDetails。
- Session 是数据库中的 opaque token，不改用 ASP.NET Identity 或内存 Session。
- Cookie 属性保持 `HttpOnly`、`SameSite=Lax`、`Path=/`、可配置 `Secure` 与 max-age。
- 密码至少 12 字符且不超过 72 UTF-8 bytes；空安装禁止默认管理员密码。
- MCP/API bearer token 仅保存 SHA-256；MCP token 不可恢复。

---

### 任务 1：创建解决方案与健康端点

**文件：**

- 创建：`src/OnToPilot.sln`
- 创建：`src/OnToPilot/OnToPilot.csproj`
- 创建：`src/OnToPilot.Domain/OnToPilot.Domain.csproj`
- 创建：`src/OnToPilot.Tests/OnToPilot.Tests.csproj`
- 创建：`src/OnToPilot/Configuration/OnToPilotOptions.cs`
- 创建：`src/OnToPilot/Controllers/HealthController.cs`
- 创建：`src/OnToPilot/Program.cs`
- 测试：`src/OnToPilot.Tests/Api/HealthContractTests.cs`

**接口：**

- 输出：`GET /api/health` 返回 `status`、`system_language`、`extract_model`、`has_llm_key`。

- [ ] **步骤 1：写健康契约失败测试**

```csharp
[Fact]
public async Task Health_uses_the_existing_route_and_shape()
{
    await using var app = new OnToPilotWebApplicationFactory();
    var response = await app.CreateClient().GetAsync("/api/health");
    var json = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("ok", json.GetProperty("status").GetString());
    Assert.True(json.TryGetProperty("system_language", out _));
    Assert.True(json.TryGetProperty("extract_model", out _));
    Assert.True(json.TryGetProperty("has_llm_key", out _));
}
```

- [ ] **步骤 2：运行测试并确认项目尚不存在**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~HealthContract`
预期：失败，提示找不到项目或 `Program`。

- [ ] **步骤 3：创建最小项目与端点**

```csharp
[ApiController]
public sealed class HealthController(IOptions<OnToPilotOptions> options) : ControllerBase
{
    [HttpGet("/api/health")]
    public object Get() => new
    {
        status = "ok",
        system_language = options.Value.SystemLanguage,
        extract_model = options.Value.ExtractModel,
        has_llm_key = !string.IsNullOrWhiteSpace(options.Value.LlmApiKey),
    };
}
```

- [ ] **步骤 4：验证并提交**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~HealthContract; dotnet build src/OnToPilot.sln -warnaserror`
预期：测试与构建通过。

```bash
git add src/OnToPilot.sln src/OnToPilot src/OnToPilot.Domain src/OnToPilot.Tests
git commit -m "feat: scaffold dotnet backend"
```

### 任务 2：映射全部 24 个 EF Core 实体

**文件：**

- 创建：`src/OnToPilot/Infrastructure/Persistence/OnToPilotDbContext.cs`
- 创建：`src/OnToPilot/Infrastructure/Persistence/Entities/AuthEntities.cs`
- 创建：`src/OnToPilot/Infrastructure/Persistence/Entities/WorkspaceEntities.cs`
- 创建：`src/OnToPilot/Infrastructure/Persistence/Entities/ProvenanceEntities.cs`
- 创建：`src/OnToPilot/Infrastructure/Persistence/Entities/ReleaseEntities.cs`
- 创建：`src/OnToPilot/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`
- 创建：`src/OnToPilot/Infrastructure/Persistence/Migrations/InitialCompatibility.cs`
- 测试：`src/OnToPilot.Tests/Persistence/ModelMappingTests.cs`
- 测试：`src/OnToPilot.IntegrationTests/Persistence/PostgresSchemaTests.cs`

**接口：**

- 输出：`User`、`AuthSession`、`KSGrant`、`Document`、`Chunk`、`KnowledgeSystem`、`KnowledgePromptOverride`、`KnowledgeApiToken`、`McpUserToken`、`Provider`、`SystemConfig`、`ExtractionJob`、`AxiomProvenance`、`AboxProvenance`、`AuditEvent`、`OntologyRelease`、`ReleaseDeployment`、`ReleaseStatementProvenance`、`ExportJob`、`Conflict`、`EntityResolution`、`TermProposal`、`TboxReconciliation`、`ValidationDecision` 的完整映射。

- [ ] **步骤 1：写模型元数据失败测试**

```csharp
[Fact]
public void Model_contains_all_legacy_tables_and_compatibility_keys()
{
    using var db = DbContextFactory.CreateSqlite();
    var entities = db.Model.GetEntityTypes().ToDictionary(x => x.ClrType.Name);
    Assert.Equal(24, entities.Count);
    Assert.All(entities.Values, entity => Assert.NotNull(entity.FindPrimaryKey()));
    Assert.True(entities[nameof(KnowledgeSystemEntity)].GetIndexes()
        .Any(index => index.IsUnique && index.Properties.Single().Name == "LegacyId"));
}
```

- [ ] **步骤 2：运行测试并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~ModelMapping`
预期：失败，提示 `OnToPilotDbContext` 或实体不存在。

- [ ] **步骤 3：实现 GUID/LegacyId 基类与关键约束**

```csharp
public abstract class LegacyAddressableEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long LegacyId { get; set; }
}

builder.Entity<DocumentEntity>()
    .HasIndex(x => new { x.KnowledgeSystemId, x.Sha256 })
    .IsUnique();
builder.Entity<OntologyReleaseEntity>()
    .HasIndex(x => new { x.KnowledgeSystemId, x.Version })
    .IsUnique();
builder.Entity<KnowledgePromptOverrideEntity>()
    .HasIndex(x => new { x.KnowledgeSystemId, x.PromptKey })
    .IsUnique();
```

- [ ] **步骤 4：在 PostgreSQL Testcontainer 应用迁移**

运行：`dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~PostgresSchema`
预期：迁移成功；24 张业务表、JSONB、bytea、唯一索引和外键均与断言一致。

- [ ] **步骤 5：运行模型测试并提交**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~ModelMapping`
预期：通过。

```bash
git add src/OnToPilot/Infrastructure/Persistence src/OnToPilot.Tests/Persistence src/OnToPilot.IntegrationTests/Persistence
git commit -m "feat: map compatibility persistence model"
```

### 任务 3：实现 Session、密码和知识系统权限

**文件：**

- 创建：`src/OnToPilot/Authentication/SessionAuthenticationHandler.cs`
- 创建：`src/OnToPilot/Authentication/PasswordService.cs`
- 创建：`src/OnToPilot/Authorization/KnowledgeSystemAccessService.cs`
- 创建：`src/OnToPilot/Api/FastApiErrorMiddleware.cs`
- 创建：`src/OnToPilot/Controllers/AuthController.cs`
- 测试：`src/OnToPilot.Tests/Authentication/AuthenticationContractTests.cs`
- 测试：`src/OnToPilot.Tests/Authorization/KnowledgeSystemAccessTests.cs`

**接口：**

- 输出：`viewer < editor < owner`，admin 等价 owner；401/403/404 文案与 Python 保持一致。

- [ ] **步骤 1：写登录 Cookie 和错误信封失败测试**

```csharp
[Fact]
public async Task Login_sets_compatible_cookie_and_rejects_bad_password()
{
    var bad = await Client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "bad" });
    Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    Assert.Equal("Incorrect username or password", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

    var ok = await Client.PostAsJsonAsync("/api/auth/login", ValidLogin);
    var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
    Assert.Contains("HttpOnly", cookie);
    Assert.Contains("SameSite=Lax", cookie);
    Assert.Contains("Path=/", cookie);
}
```

- [ ] **步骤 2：运行认证与授权测试并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~AuthenticationContract|FullyQualifiedName~KnowledgeSystemAccess"`
预期：失败，提示认证服务尚未注册。

- [ ] **步骤 3：实现自定义 Session 认证**

```csharp
var token = Request.Cookies[options.SessionCookie];
var session = await db.AuthSessions.Include(x => x.User)
    .SingleOrDefaultAsync(x => x.Token == token, cancellationToken);
if (session is null) return AuthenticateResult.NoResult();
if (session.ExpiresAt <= clock.GetUtcNow())
    return AuthenticateResult.Fail("Session expired");
if (!session.User.Active)
    return AuthenticateResult.Fail("User inactive");
```

- [ ] **步骤 4：实现权限矩阵并验证**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~AuthenticationContract|FullyQualifiedName~KnowledgeSystemAccess"`
预期：未认证、过期、inactive、admin、viewer/editor/owner 的全部矩阵通过。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Authentication src/OnToPilot/Authorization src/OnToPilot/Api src/OnToPilot/Controllers/AuthController.cs src/OnToPilot.Tests
git commit -m "feat: preserve session and role authorization"
```

### 任务 4：实现 Token 基元和启动恢复

**文件：**

- 创建：`src/OnToPilot/Authentication/KnowledgeApiTokenService.cs`
- 创建：`src/OnToPilot/Authentication/McpTokenService.cs`
- 创建：`src/OnToPilot/Infrastructure/Startup/BootstrapAdminService.cs`
- 创建：`src/OnToPilot/Infrastructure/Startup/LegacyBackfillService.cs`
- 创建：`src/OnToPilot/Infrastructure/Startup/StaleJobRecoveryService.cs`
- 测试：`src/OnToPilot.Tests/Authentication/TokenServiceTests.cs`
- 测试：`src/OnToPilot.Tests/Infrastructure/StartupRecoveryTests.cs`

**接口：**

- 输出：外部 scope 五项、MCP scope 三项；启动时执行管理员种子、孤儿文档绑定、陈旧任务/导出/部署恢复和术语回填。

- [ ] **步骤 1：写失败测试**

```csharp
[Fact]
public async Task Startup_marks_interrupted_work_failed_without_touching_completed_rows()
{
    await SeedJobsAsync(("running", "extract"), ("completed", "extract"), ("pending", "export"));
    await Recovery.RunAsync(CancellationToken.None);
    Assert.Equal("failed", await StatusOfAsync("running", "extract"));
    Assert.Equal("completed", await StatusOfAsync("completed", "extract"));
    Assert.Equal("failed", await StatusOfAsync("pending", "export"));
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~TokenService|FullyQualifiedName~StartupRecovery"`
预期：失败，服务类型不存在。

- [ ] **步骤 3：实现 Token 摘要与实时状态检查**

```csharp
public static string Digest(string plaintext) =>
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

public bool IsActive(DateTimeOffset now) =>
    RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
```

- [ ] **步骤 4：实现启动 hosted service 并验证幂等性**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~TokenService|FullyQualifiedName~StartupRecovery"`
预期：重复运行不新增重复管理员、Provider 或审计记录，陈旧状态只转换一次。

- [ ] **步骤 5：运行阶段门禁并提交**

运行：`dotnet test src/OnToPilot.sln --configuration Release; dotnet format src/OnToPilot.sln --verify-no-changes`
预期：全部通过。

```bash
git add src
git commit -m "feat: add token primitives and startup recovery"
```
