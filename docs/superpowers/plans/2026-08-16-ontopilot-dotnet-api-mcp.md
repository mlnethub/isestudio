# OnToPilot .NET REST 与 MCP 实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐项执行。步骤使用 `- [ ]` 跟踪。

**目标：** 实现当前 Python OpenAPI 基线中的全部 REST operation 和实际 MCP `tools/list` 基线中的全部 Tool，并保持认证、权限、错误和缓存语义兼容。

**架构：** Controller 只处理协议适配，所有 REST 与 MCP 共享 `IntegrationApiFacade`，避免两套业务逻辑漂移。契约测试从基线 JSON 参数化生成，任何缺失或额外 operation 都必须显式审批。

**技术栈：** ASP.NET Core 10 Controllers、System.Text.Json source generation、ModelContextProtocol.AspNetCore 2.x、WebApplicationFactory、xUnit

## 全局约束

- 路由清单来自 `migration/baseline/openapi-python.json`，不是手工统计值。
- MCP Tool 清单来自认证后的 `tools/list` 基线；源码 decorator 与旧测试的 21/20 分歧必须在任务 1 解决。
- FastAPI 错误信封保持 `{"detail": string|object|array}`。
- external SPARQL 只允许 SELECT/ASK，并禁止 SERVICE、FROM、GRAPH 与 update。
- MCP 每次调用重新检查 token、用户 active 状态和实时 KS role；不得把 role 固化在 token 中。

---

### 任务 1：建立参数化契约测试与共享 Facade

**文件：**

- 创建：`src/OnToPilot.Application/IntegrationApiFacade.cs`
- 创建：`src/OnToPilot.ApiContract.Tests/Baseline/OpenApiInventoryTests.cs`
- 创建：`src/OnToPilot.ApiContract.Tests/Baseline/McpInventoryTests.cs`
- 创建：`src/OnToPilot.ApiContract.Tests/Baseline/BaselineLoader.cs`
- 修改：`docs/migration/dotnet-contract-baseline.md`

**接口：**

- 输出：从 Python 基线读取 operation/tool 的测试数据源；Facade 方法返回协议无关 DTO。

- [ ] **步骤 1：写 inventory 失败测试**

```csharp
[Fact]
public void Dotnet_openapi_contains_every_python_operation()
{
    var expected = BaselineLoader.OpenApiOperations();
    var actual = DotNetOpenApi.ReadOperations();
    Assert.Empty(expected.Except(actual));
    Assert.Empty(actual.Except(expected));
}
```

- [ ] **步骤 2：运行并确认大量差异**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter FullyQualifiedName~Inventory`
预期：失败，并打印缺失 operation 与 MCP Tool 清单。

- [ ] **步骤 3：锁定 MCP 实际结果并记录差异原因**

运行：`cd backend; python scripts/export_contract_baseline.py ../migration/baseline`
预期：`mcp-tools-python.json` 来自真实 `tools/list`；文档记录最终数量和旧 `test_mcp.py` 断言是否需要先在 Python 基线修正。

- [ ] **步骤 4：定义 Facade 边界**

```csharp
public interface IIntegrationApiFacade
{
    Task<OntologyResponse> GetOntologyAsync(long knowledgeSystemId, Actor actor, CancellationToken cancellationToken);
    Task<QueryResponse> QueryAsync(string publicId, string sparql, int maxRows, TokenPrincipal token, CancellationToken cancellationToken);
    Task<ChangePreview> PreviewOntologyChangesAsync(long knowledgeSystemId, IReadOnlyList<EditOperation> operations, Actor actor, CancellationToken cancellationToken);
}
```

- [ ] **步骤 5：提交测试骨架**

```bash
git add src/OnToPilot.Application src/OnToPilot.ApiContract.Tests docs/migration migration/baseline
git commit -m "test: establish generated api contract gates"
```

### 任务 2：实现内部 REST Controller

**文件：**

- 创建：`src/OnToPilot/Controllers/AuthController.cs`
- 创建：`src/OnToPilot/Controllers/KnowledgeController.cs`
- 创建：`src/OnToPilot/Controllers/DocumentsController.cs`
- 创建：`src/OnToPilot/Controllers/OntologyController.cs`
- 创建：`src/OnToPilot/Controllers/ExtractionController.cs`
- 创建：`src/OnToPilot/Controllers/ConflictsController.cs`
- 创建：`src/OnToPilot/Controllers/HistoryController.cs`
- 创建：`src/OnToPilot/Controllers/ABoxController.cs`
- 创建：`src/OnToPilot/Controllers/ResolutionController.cs`
- 创建：`src/OnToPilot/Controllers/VocabularyController.cs`
- 创建：`src/OnToPilot/Controllers/PromptsController.cs`
- 创建：`src/OnToPilot/Controllers/ReleasesController.cs`
- 创建：`src/OnToPilot/Controllers/RdfImportController.cs`
- 创建：`src/OnToPilot/Controllers/ProvidersController.cs`
- 创建：`src/OnToPilot/Controllers/SettingsController.cs`
- 创建：`src/OnToPilot/Controllers/TokensController.cs`
- 创建：`src/OnToPilot/Controllers/McpTokensController.cs`
- 测试：`src/OnToPilot.ApiContract.Tests/InternalApiContractTests.cs`

**接口：**

- 输出：`/api/auth`、`/api/providers`、`/api/knowledge` 与 `/api` settings 下的全部基线 operation。

- [ ] **步骤 1：按模块生成失败用例**

```csharp
[Theory]
[MemberData(nameof(BaselineLoader.InternalOperations), MemberType = typeof(BaselineLoader))]
public async Task Internal_operation_matches_status_and_schema(OperationCase operation)
{
    var response = await Scenario.SendAsync(operation);
    Assert.Equal(operation.ExpectedStatus, response.StatusCode);
    JsonSchemaAssert.Compatible(operation.ResponseSchema, await response.Content.ReadAsStringAsync());
}
```

- [ ] **步骤 2：运行并确认失败清单**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter FullyQualifiedName~InternalApiContract`
预期：失败清单按 17 个内部 Controller 分组，不是单一无法定位的总失败。

- [ ] **步骤 3：实现 DTO source generation 与错误适配**

```csharp
[JsonSerializable(typeof(FastApiError))]
[JsonSerializable(typeof(OntologyResponse))]
[JsonSerializable(typeof(IReadOnlyList<KnowledgeSystemDto>))]
internal partial class OnToPilotJsonContext : JsonSerializerContext;

public sealed record FastApiError([property: JsonPropertyName("detail")] object Detail);
```

- [ ] **步骤 4：逐模块完成并运行契约测试**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter FullyQualifiedName~InternalApiContract`
预期：全部内部 operation 的方法、路径、状态、JSON 字段和权限矩阵通过；抽取进行中的修改返回 409。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Controllers src/OnToPilot/Api src/OnToPilot.ApiContract.Tests/InternalApiContractTests.cs
git commit -m "feat: expose compatible internal rest api"
```

### 任务 3：实现 External 与 Published API

**文件：**

- 创建：`src/OnToPilot/Controllers/ExternalApiController.cs`
- 创建：`src/OnToPilot/Controllers/PublishedController.cs`
- 创建：`src/OnToPilot/Authentication/ExternalTokenAuthenticationHandler.cs`
- 创建：`src/OnToPilot/Api/ReadOnlySparqlPolicy.cs`
- 测试：`src/OnToPilot.ApiContract.Tests/ExternalApiContractTests.cs`
- 测试：`src/OnToPilot.ApiContract.Tests/PublishedCacheContractTests.cs`

**接口：**

- 输出：`/api/v1/knowledge-systems/{public_id}` 下 token scope、查询、当前发布与 pinned release 行为。

- [ ] **步骤 1：写安全与缓存失败测试**

```csharp
[Theory]
[InlineData("INSERT DATA { <a> <b> <c> }", HttpStatusCode.BadRequest)]
[InlineData("SELECT * WHERE { SERVICE <https://example.test> { ?s ?p ?o } }", HttpStatusCode.BadRequest)]
public async Task External_query_rejects_non_read_only_sparql(string sparql, HttpStatusCode status)
{
    var response = await External.PostQueryAsync(sparql);
    Assert.Equal(status, response.StatusCode);
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter "FullyQualifiedName~ExternalApi|FullyQualifiedName~PublishedCache"`
预期：失败，Controller 尚不存在。

- [ ] **步骤 3：实现 scope 与缓存头**

```csharp
Response.Headers["X-OntoPilot-Release"] = release.Version;
Response.Headers.ETag = $"\"{release.ManifestHash}\"";
Response.Headers.CacheControl = pinned
    ? "private, max-age=31536000, immutable"
    : "private, no-cache";
```

- [ ] **步骤 4：验证状态与 header**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter "FullyQualifiedName~ExternalApi|FullyQualifiedName~PublishedCache"`
预期：401 含 `WWW-Authenticate: Bearer`；scope 不足 403；provisioning 503 + `Retry-After: 2`；stopped/failed/deleted 410；ETag/Cache-Control 兼容。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Controllers/ExternalApiController.cs src/OnToPilot/Controllers/PublishedController.cs src/OnToPilot/Authentication src/OnToPilot/Api/ReadOnlySparqlPolicy.cs src/OnToPilot.ApiContract.Tests
git commit -m "feat: expose external and published api"
```

### 任务 4：实现 MCP transport、实时授权和全部 Tool

**文件：**

- 创建：`src/OnToPilot/Mcp/McpTokenAuthenticationMiddleware.cs`
- 创建：`src/OnToPilot/Mcp/McpPrincipalAccessor.cs`
- 创建：`src/OnToPilot/Mcp/OnToPilotMcpTools.cs`
- 创建：`src/OnToPilot/Mcp/OnToPilotMcpResources.cs`
- 创建：`src/OnToPilot/Mcp/OnToPilotMcpPrompts.cs`
- 测试：`src/OnToPilot.ApiContract.Tests/McpDiscoveryContractTests.cs`
- 测试：`src/OnToPilot.ApiContract.Tests/McpAuthorizationTests.cs`
- 测试：`src/OnToPilot.ApiContract.Tests/McpBehaviorTests.cs`

**接口：**

- 输出：`POST /mcp`、stateless Streamable HTTP、1 MiB 请求限制、DNS rebinding protection、基线中的全部 Tool。

- [ ] **步骤 1：写发现与实时角色失败测试**

```csharp
[Fact]
public async Task Existing_token_loses_write_access_after_membership_downgrade()
{
    var token = await Tokens.CreateAsync(Role.Editor, ["mcp:read", "mcp:write"]);
    await Mcp.CallAsync(token, "preview_ontology_changes", PreviewArgs);
    await Membership.ChangeAsync(Role.Viewer);
    var error = await Mcp.CallForErrorAsync(token, "apply_ontology_changes", ApplyArgs);
    Assert.Contains("editor role", error.Message);
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter "FullyQualifiedName~McpDiscovery|FullyQualifiedName~McpAuthorization|FullyQualifiedName~McpBehavior"`
预期：失败，MCP 尚未注册。

- [ ] **步骤 3：注册 transport 并复用 Facade**

```csharp
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "OntoPilot", Version = "1.0.0" })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<OnToPilotMcpTools>()
    .WithResources<OnToPilotMcpResources>()
    .WithPrompts<OnToPilotMcpPrompts>();
```

- [ ] **步骤 4：实现 destructive confirm 与 preview clean 语义**

运行：`dotnet test src/OnToPilot.ApiContract.Tests --filter "FullyQualifiedName~McpDiscovery|FullyQualifiedName~McpAuthorization|FullyQualifiedName~McpBehavior"`
预期：Tool inventory 与基线完全相同；preview 后图不变；最多 50 edits/200 KB；write/manage/owner 权限、confirm 和 ToolError 文案通过。

- [ ] **步骤 5：运行阶段门禁并提交**

运行：`dotnet test src/OnToPilot.ApiContract.Tests; dotnet build src/OnToPilot.sln -warnaserror`
预期：全部通过。

```bash
git add src
git commit -m "feat: expose authenticated mcp transport"
```
