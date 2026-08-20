# OnToPilot .NET 契约验证与可观测性实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐项执行。步骤使用 `- [ ]` 跟踪。

**目标：** 用隔离数据副本证明 .NET 后端与 Python 后端的协议和关键用户流程一致，并交付带结构化日志、追踪、指标和健康检查的生产镜像。

**架构：** Python 与 .NET 分别使用独立 PostgreSQL、RDF 和 blob 副本；差分 runner 对同一 fixture 场景比较状态码、header 和规范化 JSON。OpenTelemetry 在应用边界统一埋点，Docker Compose 只在验证环境并行运行两套后端。

**技术栈：** xUnit、PowerShell、Playwright、Serilog、OpenTelemetry、Docker Compose

## 全局约束

- 差分测试不得让两套后端打开同一个 RocksDB 目录。
- 动态字段仅允许按明确 allowlist 规范化：时间戳、随机 token、trace ID；业务字段不得忽略。
- 日志不得包含密码、API key、bearer token、session token 或文档正文。
- 健康检查路径固定为 `/api/health`，Dockerfile 与 Compose 不使用规格中的错误 `/health` 路径。
- Frontend 源码保持不变；仅新增测试配置和测试用例。

---

### 任务 1：实现 Python/.NET 差分契约 Runner

**文件：**

- 创建：`migration/scripts/Invoke-ContractComparison.ps1`
- 创建：`migration/contracts/scenarios.json`
- 创建：`migration/contracts/normalization.json`
- 创建：`src/OnToPilot.ApiContract.Tests/DifferentialContractTests.cs`
- 创建：`docs/migration/contract-difference-policy.md`

**接口：**

- 输入：`-PythonUrl`、`-DotNetUrl`、场景文件。
- 输出：`migration/actual/contract-comparison.json`，每个 operation 包含 status/header/body diff。

- [ ] **步骤 1：写规范化器失败测试**

```csharp
[Fact]
public void Normalizer_only_removes_allowlisted_dynamic_fields()
{
    var normalized = Normalizer.Apply("""{"id":7,"created_at":"now","name":"Pump"}""");
    Assert.Equal(7, normalized.GetProperty("id").GetInt32());
    Assert.Equal("Pump", normalized.GetProperty("name").GetString());
    Assert.False(normalized.TryGetProperty("created_at", out _));
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter FullyQualifiedName~DifferentialContract`
预期：失败，Normalizer/runner 不存在。

- [ ] **步骤 3：实现场景协议与严格 diff**

```json
{
  "name": "knowledge-list-authenticated",
  "method": "GET",
  "path": "/api/knowledge",
  "auth": "owner-session",
  "compareHeaders": ["content-type"],
  "expectedStatus": 200
}
```

- [ ] **步骤 4：在隔离副本上运行差分**

运行：`pwsh migration/scripts/Invoke-ContractComparison.ps1 -PythonUrl http://localhost:18000 -DotNetUrl http://localhost:18080`
预期：所有基线 operation 被场景或 schema 检查覆盖；报告中无未批准差异。

- [ ] **步骤 5：提交**

```bash
git add migration/contracts migration/scripts/Invoke-ContractComparison.ps1 src/OnToPilot.ApiContract.Tests docs/migration/contract-difference-policy.md
git commit -m "test: add python dotnet contract comparison"
```

### 任务 2：实现关键 E2E 与 MCP Inspector 测试

**文件：**

- 创建：`frontend/e2e/dotnet/upload-extract-publish.spec.ts`
- 创建：`frontend/e2e/dotnet/vocabulary.spec.ts`
- 创建：`frontend/e2e/dotnet/session.spec.ts`
- 创建：`migration/scripts/Test-McpEndpoint.ps1`
- 创建：`migration/contracts/mcp-smoke.json`

**接口：**

- 输出：上传 PDF 到发布、SKOS CRUD、登录/退出、MCP discovery/read/preview/apply 的自动化证据。

- [ ] **步骤 1：写上传到发布失败测试**

```typescript
test("upload, extract, review and publish against dotnet", async ({ page }) => {
  await loginAsAdmin(page);
  await page.getByRole("link", { name: "Documents" }).click();
  await page.getByLabel("Upload files").setInputFiles("e2e/fixtures/pump.pdf");
  await expect(page.getByText("parsed")).toBeVisible();
  await page.getByRole("button", { name: "Extract" }).click();
  await expect(page.getByText("completed")).toBeVisible({ timeout: 60_000 });
  await publishCurrentDraft(page);
  await expect(page.getByText("published")).toBeVisible();
});
```

- [ ] **步骤 2：运行并确认当前环境失败**

运行：`pnpm --dir frontend exec playwright test e2e/dotnet`
预期：在 .NET 服务未启动或端点未完成时失败，失败步骤明确。

- [ ] **步骤 3：实现 MCP 协议 smoke**

```powershell
$tools = Invoke-McpRequest -Method 'tools/list' -Token $Token
Compare-Object $BaselineToolNames ($tools.result.tools.name) | ForEach-Object { throw "MCP inventory mismatch: $_" }
Invoke-McpRequest -Method 'tools/call' -Params @{ name = 'get_ontology'; arguments = @{} } -Token $Token
```

- [ ] **步骤 4：运行 E2E 与 MCP 测试**

运行：`pnpm --dir frontend exec playwright test e2e/dotnet; pwsh migration/scripts/Test-McpEndpoint.ps1 -Url http://localhost:18080/mcp`
预期：三条浏览器流程和 MCP discovery/auth/read/preview-clean/apply 均通过。

- [ ] **步骤 5：提交**

```bash
git add frontend/e2e/dotnet migration/scripts/Test-McpEndpoint.ps1 migration/contracts/mcp-smoke.json
git commit -m "test: cover dotnet end to end workflows"
```

### 任务 3：加入结构化日志、追踪和指标

**文件：**

- 创建：`src/OnToPilot/Observability/Telemetry.cs`
- 创建：`src/OnToPilot/Observability/TelemetryExtensions.cs`
- 创建：`src/OnToPilot/Observability/SecretRedactionProcessor.cs`
- 修改：`src/OnToPilot/Program.cs`
- 测试：`src/OnToPilot.Tests/Observability/TelemetryTests.cs`

**接口：**

- 输出：`Llm.Extract`、`Rdf.StoreWrapper.*`、`Rdf.Shacl.Validate`、`Parsing.Parse`、`Storage.Minio.*`、`Mcp.Tool.*` Activity 与 extraction metrics。

- [ ] **步骤 1：写标签和脱敏失败测试**

```csharp
[Fact]
public async Task Llm_activity_records_provider_without_secret_or_prompt()
{
    using var listener = TestActivityListener.Capture(Telemetry.LlmSourceName);
    await Service.ExtractAsync(Request, CancellationToken.None);
    var activity = Assert.Single(listener.Activities);
    Assert.Equal("fake", activity.GetTagItem("llm.provider"));
    Assert.DoesNotContain(activity.TagObjects, tag => tag.Key.Contains("key") || tag.Key.Contains("prompt"));
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~Telemetry`
预期：失败，Telemetry 类型不存在。

- [ ] **步骤 3：注册 Serilog/OpenTelemetry**

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("OnToPilot.*").AddAspNetCoreInstrumentation().AddNpgsql())
    .WithMetrics(metrics => metrics.AddMeter("OnToPilot").AddAspNetCoreInstrumentation());
```

- [ ] **步骤 4：验证 Activity、metric 与日志脱敏**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~Telemetry`
预期：各关键路径 success/error 标签、duration histogram、计数器和 secret redaction 通过。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Observability src/OnToPilot/Program.cs src/OnToPilot.Tests/Observability
git commit -m "feat: add backend observability"
```

### 任务 4：交付生产 Docker 镜像与 Compose 拓扑

**文件：**

- 修改：`backend/Dockerfile`
- 修改：`docker-compose.yml`
- 修改：`backend/.env.example`
- 创建：`migration/compose/docker-compose.shadow.yml`
- 创建：`migration/scripts/Test-ContainerHealth.ps1`
- 测试：`src/OnToPilot.IntegrationTests/Deployment/ContainerSmokeTests.cs`

**接口：**

- 输出：.NET 10 多阶段镜像、PostgreSQL/MinIO 依赖、`/api/health` healthcheck、仅供副本验证的 shadow Compose。

- [ ] **步骤 1：写镜像 smoke 失败测试**

```csharp
[Fact]
public async Task Production_container_becomes_healthy_on_api_health()
{
    var response = await Retry.UntilSuccessAsync(() => Client.GetAsync("/api/health"), TimeSpan.FromMinutes(2));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

- [ ] **步骤 2：构建当前镜像并确认尚未满足 .NET smoke**

运行：`docker compose build backend; dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~ContainerSmoke`
预期：测试失败，当前 backend 仍是 Python 镜像或缺少 MinIO。

- [ ] **步骤 3：实现多阶段 Dockerfile 与健康依赖**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./src/
RUN dotnet publish src/OnToPilot/OnToPilot.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OnToPilot.dll"]
```

- [ ] **步骤 4：验证镜像和未修改前端**

运行：`docker compose up -d --build postgres minio backend frontend; pwsh migration/scripts/Test-ContainerHealth.ps1; pnpm --dir frontend build`
预期：服务 healthy，前端构建通过，代理访问 `/api/health` 成功。

- [ ] **步骤 5：运行阶段门禁并提交**

运行：`dotnet test src/OnToPilot.sln --configuration Release; dotnet format src/OnToPilot.sln --verify-no-changes`
预期：全部通过。

```bash
git add backend/Dockerfile backend/.env.example docker-compose.yml migration/compose migration/scripts/Test-ContainerHealth.ps1 src/OnToPilot.IntegrationTests
git commit -m "build: package dotnet backend for production"
```
