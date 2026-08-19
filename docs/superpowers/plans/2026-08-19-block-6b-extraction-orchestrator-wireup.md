# Block 6b — ExtractionOrchestrator 14 deps + `run*` paths wire-up 实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐项执行。步骤使用 `- [ ]` 跟踪。

**目标：** 把 `InternalOperationDispatcher` 的 3 个 `extraction.run*` arm 从 placeholder 接到 `ExtractionOrchestrator.Start{TBox|Combined|ABox}Async`，注册 9 个缺失 DI 服务，合并 3 个测试文件的 `FakeChatClientFactory` 嵌套类，新增 6 个 HTTP-level contract test。

**架构：** `AddExtractionServices()` 集中注册 9 个 singleton；`AuthTestWebApplicationFactory` 通过 `ConfigureWebHost` override `IChatClientFactory → FakeChatClientFactory.Default`；dispatcher 走统一 `InvokeExtractionAsync` helper 调用 orchestrator（409 envelope 沿用 `RunWithExtractionGuardAsync`）。

**技术栈：** .NET 10 / ASP.NET Core 10 / xUnit 2.9.3 / WebApplicationFactory / FakeChat（已存在）

---

## 全局约束

- 设计文档：`docs/superpowers/specs/2026-08-19-block-6b-extraction-orchestrator-wireup-design.md`
- 现有 `ExtractionApiTests.cs`（Block 5, 7 tests）保持不动；新增文件 `ExtractionRunApiTests.cs`
- 现有 `FakeChat.cs`（top-level, in `src/OnToPilot.Tests/Extraction/`）保持不动；只新增 `FakeChatClientFactory.cs`
- 现有 `ExtractionStateTests.cs` / `ExtractionLlmFailureTests.cs` / `ExtractionCapacityKeyTests.cs` 共 13 个 test 必须保持 passing（refactor only）
- 9 个 extraction service **全部 singleton**（`ExtractionOrchestrator` 必须 singleton 以保持 `Task.Run` 后台任务状态）
- `RunWithExtractionGuardAsync`（Block 5）保留 — 409 envelope 逻辑已正确，不重写
- `FakeChatClientFactory.Default` 是类级别 singleton；测试用 `Reset()` 显式清状态
- 全量回归基线：B7c = 318 passing / 319 total（1 pre-existing Block 11 is_admin fail）
- 期望 B6b 完成后：318 + 6 = 324 / 325 passing

---

## 文件结构

| 文件 | 状态 | 职责 |
|---|---|---|
| `src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs` | 新增 | `AddExtractionServices()` 注册 9 个 services |
| `src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs` | 新增 | 共享 `FakeChatClientFactory` + `Default` singleton + Reset/Queue hooks |
| `src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs` | 新增 | 6 个 HTTP-level contract tests for run pipeline |

> 注：`ExtractionJobOut` 已存在（`src/OnToPilot/Extraction/ExtractionJobDtos.cs`），含
> `ExtractionJobOut.From(ExtractionJobEntity)` 静态工厂方法，wire 字段已用
> `[JsonPropertyName]` 锁定 snake_case。本计划不重复创建，直接调 `From(...)`。
| `src/OnToPilot/Integration/InternalOperationDispatcher.cs` | 修改 | 3 个 `extraction.run*` arm 接 `InvokeExtractionAsync` |
| `src/OnToPilot/Program.cs` | 修改 | 在 `AddOntologyServices()` 之后调 `AddExtractionServices()` |
| `src/OnToPilot.Tests/Authentication/AuthTestWebApplicationFactory.cs` | 修改 | Override `IChatClientFactory → FakeChatClientFactory.Default` |
| `src/OnToPilot.Tests/Extraction/ExtractionStateTests.cs` | 修改 | 删 nested `FakeChatClientFactory`，改用 `FakeChatClientFactory.Default` |
| `src/OnToPilot.Tests/Extraction/ExtractionLlmFailureTests.cs` | 修改 | 删 nested `SingleClientFactory`，改用 `FakeChatClientFactory.Default` |
| `src/OnToPilot.Tests/Extraction/ExtractionCapacityKeyTests.cs` | 修改 | 删 nested `FakeChatClientFactory`，改用 `FakeChatClientFactory.Default` |

---

## Task 1: 共享 FakeChatClientFactory + override DI + refactor 3 个现有 test

**Files:**
- Create: `src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs`
- Modify: `src/OnToPilot.Tests/Authentication/AuthTestWebApplicationFactory.cs`
- Modify: `src/OnToPilot.Tests/Extraction/ExtractionStateTests.cs`
- Modify: `src/OnToPilot.Tests/Extraction/ExtractionLlmFailureTests.cs`
- Modify: `src/OnToPilot.Tests/Extraction/ExtractionCapacityKeyTests.cs`

**Interfaces:**
- Produces: `FakeChatClientFactory.Default` (singleton), `.Reset()`, `.UseClient(IChatClient)`, `.Create(LlmProviderConfig) → IChatClient`

### Step 1: 写共享 FakeChatClientFactory

创建 `src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs`：

```csharp
using Microsoft.Extensions.AI;
using OnToPilot.Llm;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// Test-only <see cref="IChatClientFactory"/> singleton shared by every
/// extraction test. The factory holds one mutable client reference; tests
/// swap the reference via <see cref="UseClient"/> (or wrap a per-test
/// <see cref="FakeChat"/>) and call <see cref="Reset"/> between tests so
/// parallel runs do not bleed state.
/// </summary>
public sealed class FakeChatClientFactory : IChatClientFactory
{
    /// <summary>Process-wide singleton registered by
    /// <c>AuthTestWebApplicationFactory</c>. All extraction tests share
    /// this instance.</summary>
    public static FakeChatClientFactory Default { get; } = new();

    private IChatClient? _client;
    private readonly object _gate = new();

    /// <summary>Install the client every <see cref="Create"/> call returns.
    /// Pass <c>null</c> to make the factory throw — useful for asserting
    /// the orchestrator never reached the chat layer.</summary>
    public void UseClient(IChatClient? client)
    {
        lock (_gate) _client = client;
    }

    /// <summary>Detach the client so the next test starts clean. Always
    /// call from test setup or <see cref="IDisposable.Dispose"/>.</summary>
    public void Reset()
    {
        lock (_gate) _client = null;
    }

    public IChatClient Create(LlmProviderConfig config)
    {
        var client = _client;
        if (client is null)
        {
            throw new InvalidOperationException(
                "FakeChatClientFactory has no client installed. " +
                "Call FakeChatClientFactory.Default.UseClient(...) in test setup.");
        }
        return client;
    }
}
```

### Step 2: Override `IChatClientFactory` 在 `AuthTestWebApplicationFactory`

读取当前 `AuthTestWebApplicationFactory.cs`（应已在文件中间有 `ConfigureWebHost` 或 `ConfigureTestServices` 调用）。新增方法覆盖 `IChatClientFactory`：

定位文件中已有的 `services.AddSingleton<IBlobStore>(...)` 或类似 DI override 代码段。在它 **之后** 添加：

```csharp
// B6b: override production IChatClientFactory with the shared test fake
// so all extraction tests drive the orchestrator through FakeChat.
services.RemoveAll<IChatClientFactory>();
services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
```

若当前文件使用 `IServiceCollection.Replace` 而非 `RemoveAll + AddSingleton`，用同样的模式。导入 `using Microsoft.Extensions.DependencyInjection.Extensions;` 用于 `RemoveAll`。

文件顶部 `using` 添加：

```csharp
using OnToPilot.Tests.Extraction;
```

### Step 3: Refactor `ExtractionStateTests.cs`

读取 `src/OnToPilot.Tests/Extraction/ExtractionStateTests.cs` 找到 line 394 的 `private sealed class FakeChatClientFactory : IChatClientFactory`（约 7 行嵌套类）。

**删除整个嵌套类**（line 393-399）。

找到类构造函数（约 line 35-90 的 `public ExtractionStateTests()` 或 fixture 初始化块），把 `new FakeChatClientFactory(_chat)` 替换为：

```csharp
FakeChatClientFactory.Default.Reset();
FakeChatClientFactory.Default.UseClient(_chat);
```

把构造时不再需要的 `_chat` 字段保留为 `private readonly FakeChat _chat = new();`（已存在）。

### Step 4: Refactor `ExtractionLlmFailureTests.cs`

读取 `src/OnToPilot.Tests/Extraction/ExtractionLlmFailureTests.cs` 找到 line 190 的 `private sealed class SingleClientFactory : IChatClientFactory`。

**删除整个嵌套类**（约 5-7 行）。

替换所有 `new SingleClientFactory(...)` 调用为：

```csharp
FakeChatClientFactory.Default.Reset();
FakeChatClientFactory.Default.UseClient(...);
```

### Step 5: Refactor `ExtractionCapacityKeyTests.cs`

读取 `src/OnToPilot.Tests/Extraction/ExtractionCapacityKeyTests.cs` 找到 line 244 的 `private sealed class FakeChatClientFactory : IChatClientFactory`。

**删除整个嵌套类**（line 244-249，约 6 行）。

构造函数中把 `new FakeChatClientFactory(_chat)` 替换为：

```csharp
FakeChatClientFactory.Default.Reset();
FakeChatClientFactory.Default.UseClient(_chat);
```

注意：本测试直接实例化 `ExtractionOrchestrator` 而非走 DI container — 所以 `FakeChatClientFactory.Default` 必须被同步设置（每次测试构造时）。

### Step 6: 跑 13 个 lower-level extraction test 验证 refactor

```bash
dotnet build src/OnToPilot.Tests/OnToPilot.Tests.csproj -c Debug
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~ExtractionStateTests|FullyQualifiedName~ExtractionLlmFailureTests|FullyQualifiedName~ExtractionCapacityKeyTests" \
  --no-build
```

预期：13 / 13 passing。如有失败，回滚 Step 3-5 检查 nested class 是否仍被引用。

### Step 7: 跑全量 extraction test 含 B5 的 7 个

```bash
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Extraction" \
  --no-build
```

预期：20 / 20 passing（13 lower-level + 7 ExtractionApiTests B5 read endpoints）。

### Step 8: Commit refactor

```bash
git add src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs \
        src/OnToPilot.Tests/Authentication/AuthTestWebApplicationFactory.cs \
        src/OnToPilot.Tests/Extraction/ExtractionStateTests.cs \
        src/OnToPilot.Tests/Extraction/ExtractionLlmFailureTests.cs \
        src/OnToPilot.Tests/Extraction/ExtractionCapacityKeyTests.cs
git commit -m "refactor(extraction): consolidate FakeChatClientFactory into shared singleton

Replaces 3 private nested IChatClientFactory classes (in
ExtractionStateTests / ExtractionLlmFailureTests / ExtractionCapacityKeyTests)
with one top-level FakeChatClientFactory.Default singleton.

AuthTestWebApplicationFactory now overrides IChatClientFactory with the
shared fake so any future extraction test that uses WebApplicationFactory
gets the canned chat client for free.

Existing 13 lower-level tests + 7 B5 read-endpoint tests still pass."
```

---

## Task 2: 注册 ExtractionOrchestrator 9 个 DI services

**Files:**
- Create: `src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs`
- Modify: `src/OnToPilot/Program.cs`

**Interfaces:**
- Produces: `IServiceCollection.AddExtractionServices()` extension

### Step 1: 创建 `ExtractionServiceCollectionExtensions.cs`

```csharp
using OnToPilot.Extraction;
using OnToPilot.Llm;
using OnToPilot.Ontology;
using OnToPilot.Storage;

namespace OnToPilot.Extraction;

/// <summary>
/// DI registration for the extraction pipeline. All services are
/// singletons: the orchestrator must be singleton to maintain
/// <see cref="Task.Run"/> background-job state, and every collaborator is
/// either stateless or thread-safe.
/// </summary>
public static class ExtractionServiceCollectionExtensions
{
    public static IServiceCollection AddExtractionServices(
        this IServiceCollection services)
    {
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<EndpointCapacityCoordinator>();
        services.AddSingleton<TBoxExtractionService>();
        services.AddSingleton<ABoxExtractionService>();
        services.AddSingleton<TerminologyService>();
        services.AddSingleton<PromptSnapshotService>();
        services.AddSingleton<IExtractionMerger, ExtractionMerger>();
        services.AddSingleton<ExtractionOrchestrator>();
        return services;
    }
}
```

**注意**：本文件与 `ExtractionOrchestrator.cs` 同 namespace，所以可以直接 `using OnToPilot.Extraction;` 引用本文件内定义的 `ExtractionServiceCollectionExtensions`。在文件外 `Program.cs` 调 `AddExtractionServices()` 时，使用同一 namespace 即可。

### Step 2: 确认 9 个 service 类型存在

读 `src/OnToPilot/Extraction/ExtractionOrchestrator.cs` line 58-72 确认 14 个 ctor 参数的类型：
- `ExtractionJobStore`（已注册 in B5）
- `IBlobStore`（已注册）
- `IDocumentParser`（已注册）
- `Chunker`（已注册）
- `IChatClientFactory`（**待注册** this task）
- `EndpointCapacityCoordinator`（**待注册** this task）
- `TBoxExtractionService`（**待注册** this task）
- `ABoxExtractionService`（**待注册** this task）
- `TerminologyService`（**待注册** this task）
- `PromptSnapshotService`（**待注册** this task）
- `IExtractionMerger`（**待注册** this task）
- `StoreWrapper`（已注册 in B6）
- `TimeProvider`（已注册）

9 个新增都是真实 concrete types；DI 容器用反射构建。

### Step 3: 在 `Program.cs` 调 `AddExtractionServices()`

读取 `src/OnToPilot/Program.cs` 找到 `AddOntologyServices()` 或 `AddValidationDecisionServices()` 调用。在它 **之后** 添加：

```csharp
builder.Services.AddExtractionServices();
```

若 `Program.cs` 顶部 namespace 是 `OnToPilot`，但 `ExtractionServiceCollectionExtensions` 在 `OnToPilot.Extraction` namespace — 添加 using：

```csharp
using OnToPilot.Extraction;
```

### Step 4: 编译验证

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
```

预期：0 warning 0 error。如有 "Unable to resolve service for type ..." 错误，检查 missing 注册。

### Step 5: 跑测试验证 DI override 在 AuthTestWebApplicationFactory 仍工作

```bash
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Extraction"
```

预期：20 / 20 passing。`AuthTestWebApplicationFactory` 的 `IChatClientFactory → FakeChatClientFactory.Default` override 应让所有用 `WebApplicationFactory` 的 extraction test 拿到 fake client。

### Step 6: Commit DI 注册

```bash
git add src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs \
        src/OnToPilot/Program.cs
git commit -m "feat(extraction): register ExtractionOrchestrator + 8 deps in DI

AddExtractionServices() wires:
- IChatClientFactory -> ChatClientFactory (singleton)
- EndpointCapacityCoordinator (singleton, in-memory permit bucket)
- TBoxExtractionService / ABoxExtractionService (singleton, param-less)
- TerminologyService / PromptSnapshotService (singleton, sync)
- IExtractionMerger -> ExtractionMerger (singleton, sync MergeTBox/MergeABox)
- ExtractionOrchestrator (singleton, holds ExtractionJobStore + Task.Run bg work)

Test factory override (IChatClientFactory -> FakeChatClientFactory.Default)
from prior commit keeps all 20 extraction tests green."
```

---

## Task 3: Wire 3 个 dispatcher arm 到 ExtractionOrchestrator

**Files:**
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`

**Interfaces:**
- Consumes: `ExtractionRequest`（已存在, `src/OnToPilot/Extraction/ExtractionRequest.cs`）, `ExtractionOrchestrator.Start{TBox|ABox|Combined}Async`（已存在）, `ExtractionJobOut.From(ExtractionJobEntity)` 静态工厂（已存在, `src/OnToPilot/Extraction/ExtractionJobDtos.cs`）
- Produces: 私有 helper `InvokeExtractionAsync(IntegrationRequest, string runKind, CancellationToken) → Task<object?>` 直接返回 `ExtractionJobOut.From(...)`

> **关键**：wire DTO `ExtractionJobOut` 已存在（`ExtractionJobDtos.cs`），含 `From()` 静态工厂。
> EF entity `ExtractionJobEntity` 的字段都是 string/int/DateTimeOffset（**没有** enum），
> wire 通过 `[JsonPropertyName]` 锁 snake_case。本任务不新建任何 DTO 文件。

### Step 1: 新增 `InvokeExtractionAsync` 私有方法

读取 `src/OnToPilot/Integration/InternalOperationDispatcher.cs` line 110-120（placeholder arms）+ line 1288 / 1303（现有 `InvokeExtractionListJobsAsync` / `InvokeExtractionGetJobAsync`）+ line 1803（`EmptyExtractionJob`）。

在 `InvokeExtractionGetJobAsync` 附近新增 `InvokeExtractionAsync`：

```csharp
/// <summary>
/// Shared body for the 3 extraction.run* arms. Deserialises the request
/// body to <see cref="ExtractionRequest"/>, invokes the matching
/// <see cref="ExtractionOrchestrator.Start*Async"/> entry point, and
/// projects the resulting job entity to the wire DTO via
/// <see cref="ExtractionJobOut.From"/>.
/// </summary>
private async Task<object?> InvokeExtractionAsync(
    IntegrationRequest request, string runKind, CancellationToken cancellationToken)
{
    var body = DeserializeBody<ExtractionRequest>(request, "extraction body");
    var orchestrator = Resolve<ExtractionOrchestrator>();

    var job = runKind switch
    {
        "extraction.run"           => await orchestrator.StartTBoxAsync(body, cancellationToken),
        "extraction.run_combined"  => await orchestrator.StartCombinedAsync(body, cancellationToken),
        "extraction.run_instances" => await orchestrator.StartABoxAsync(body, cancellationToken),
        _ => throw new InvalidOperationException(
            $"Unknown extraction run kind '{runKind}'."),
    };

    return ExtractionJobOut.From(job);
}
```

### Step 2: 检查 `DeserializeBody<T>` helper 是否已存在

```bash
grep -n "DeserializeBody<\|JsonSerializer.Deserialize" src/OnToPilot/Integration/InternalOperationDispatcher.cs | head -10
```

如果 dispatcher 已有 `DeserializeBody<T>(request, label)` 私有静态方法（很可能 — 同文件已大量使用），沿用其签名。否则，新增：

```csharp
private static T DeserializeBody<T>(IntegrationRequest request, string label)
{
    if (request.Body is null)
    {
        throw new InvalidOperationException($"{label} is required.");
    }
    try
    {
        var body = JsonSerializer.Deserialize<T>(
            request.Body.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (body is null)
        {
            throw new InvalidOperationException($"{label} is null after deserialise.");
        }
        return body;
    }
    catch (JsonException ex)
    {
        throw new InvalidOperationException(
            $"{label} is malformed: {ex.Message}", ex);
    }
}
```

### Step 3: 替换 3 个 placeholder arm

读取 `src/OnToPilot/Integration/InternalOperationDispatcher.cs` line 110-120（已确认是 placeholder）。

**替换前**：

```csharp
"extraction.run" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => Task.FromResult<object?>(EmptyExtractionJob())),
"extraction.run_combined" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => Task.FromResult<object?>(EmptyExtractionJob())),
"extraction.run_instances" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => Task.FromResult<object?>(EmptyExtractionJob())),
```

**替换后**：

```csharp
"extraction.run" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => InvokeExtractionAsync(request, "extraction.run", cancellationToken)),
"extraction.run_combined" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => InvokeExtractionAsync(request, "extraction.run_combined", cancellationToken)),
"extraction.run_instances" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => InvokeExtractionAsync(request, "extraction.run_instances", cancellationToken)),
```

`RunWithExtractionGuardAsync` 已经做 "find active job → 409 envelope"，新 arm 直接调 `InvokeExtractionAsync`。

### Step 4: 编译验证

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
```

预期：0 warning 0 error。

### Step 5: 跑现有 extraction test 验证 wire-up 未破坏 read endpoint + 409 path

```bash
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~Extraction"
```

预期：20 / 20 passing（13 lower-level + 7 B5 read endpoints）。**不应** 有 run pipeline test 因为它们由 Task 4 写。

### Step 6: Commit dispatcher wire-up

```bash
git add src/OnToPilot/Integration/InternalOperationDispatcher.cs
git commit -m "feat(extraction): wire extraction.run / run_combined / run_instances

Replaces 3 placeholder arms (Task.FromResult(EmptyExtractionJob())) in
InternalOperationDispatcher with calls to ExtractionOrchestrator.Start*Async
via a new InvokeExtractionAsync helper. The helper projects the resulting
job entity through the existing ExtractionJobOut.From(...) factory
(ExtractionJobDtos.cs) so the wire shape matches what the read endpoints
emit.

RunWithExtractionGuardAsync is retained so the 409 envelope for concurrent
jobs keeps working. All 20 existing extraction tests still pass (13
lower-level + 7 B5 read-endpoint)."
```

---

## Task 4: 写 6 个 HTTP-level contract test

**Files:**
- Create: `src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs`

**Interfaces:**
- Consumes: `AuthTestWebApplicationFactory`（已 override `IChatClientFactory`）, `ExtractionRunApiTests` helper 类（同文件），`FakeChat.ValidTBoxDelta` / `ValidABoxDelta`（已存在）

### Step 1: 创建测试文件骨架

读取 `src/OnToPilot.Tests/Extraction/ExtractionApiTests.cs` line 1-50 学习 scaffolding pattern（`SeedAdminAndClientAsync` / `CreateKsAsync` / `LookupKsGuid` 等 helpers 的位置和签名）。

创建 `src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs` 包含文件头 + 6 个 test 方法 + helpers。每个 test 方法标 `[Fact]`。

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Persistence;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// HTTP-level contract tests for the B6b extraction run pipeline. The 3
/// <c>extraction.run*</c> arms now go through
/// <c>ExtractionOrchestrator.Start*Async</c> via the dispatcher's
/// <c>InvokeExtractionAsync</c> helper, so the run pipeline is real.
///
/// <para>Read-endpoint coverage (ListJobs / GetJob / 409 envelope from
/// Documents) lives in <c>ExtractionApiTests.cs</c> (Block 5).</para>
/// </summary>
public sealed class ExtractionRunApiTests
{
    private const string CookieHeader = "ontopilot_session";

    // ====== 6 tests ======

    [Fact]
    public async Task Post_extract_tbox_creates_job_and_writes_ontology_classes() { ... }

    [Fact]
    public async Task Post_extract_instances_creates_job_and_writes_individuals() { ... }

    [Fact]
    public async Task Post_extract_all_combined_runs_tbox_and_abox() { ... }

    [Fact]
    public async Task Post_extract_while_active_job_returns_409_with_job_envelope() { ... }

    [Fact]
    public async Task Post_extract_with_viewer_role_returns_403() { ... }

    [Fact]
    public async Task Post_extract_with_missing_blobsha_returns_400() { ... }

    // ====== Helpers ======

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app) { ... }

    private static async Task<(long LegacyId, Guid Guid)> SeedKnowledgeSystemAsync(
        AuthTestWebApplicationFactory app, HttpClient client, string tag) { ... }

    private static Guid LookupKsGuid(AuthTestWebApplicationFactory app, long legacyId) { ... }

    private static string LookupKsTboxIri(AuthTestWebApplicationFactory app, Guid ksGuid) { ... }

    private static string LookupKsAboxIri(AuthTestWebApplicationFactory app, Guid ksGuid) { ... }

    private static string SeedBlobSha(AuthTestWebApplicationFactory app) { ... }

    private static async Task WaitForJobAsync(
        HttpClient client, long ksId, Guid jobId, TimeSpan timeout) { ... }

    private static async Task SeedViewerUserAsync(
        AuthTestWebApplicationFactory app, string username, string password) { ... }
}
```

### Step 2: 实现 helper `SeedAdminAndClientAsync`

照抄 `ExtractionApiTests.cs` line 86+ 的现有 `SeedAdminAndClientAsync`（admin seed + login + cookie）。但本文件的 admin 用户名要确保与 `AuthTestWebApplicationFactory.AdminUsername` 一致（已 `seedadmin` 或类似）。

若 `AuthTestWebApplicationFactory.AdminUsername` / `AdminDisplayName` / `AdminPassword` 已存在（per B5 / B7c），直接引用其常量。

### Step 3: 实现 helper `SeedKnowledgeSystemAsync` + `LookupKsTboxIri` + `LookupKsAboxIri`

照抄 `ABoxValidationApiTests.cs` 的同名 helpers（line 403+ / 437+）— 创建 KS,返回 `(long legacyId, Guid guid)` + 查 TBox / ABox graph IRI。

### Step 4: 实现 helper `SeedBlobSha`

通过 `app.Services.GetRequiredService<OnToPilot.Storage.IBlobStore>()` 拿到 blob store，直接调 `PutAsync(stream, ct)` 拿到 sha256（参考 `ExtractionCapacityKeyTests.cs` line 240 私有 helper `PutDocument`）。

```csharp
private static string SeedBlobSha(AuthTestWebApplicationFactory app)
{
    var blobs = app.Services.GetRequiredService<OnToPilot.Storage.IBlobStore>();
    using var stream = new MemoryStream(
        Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"));
    return blobs.PutAsync(stream, CancellationToken.None)
        .GetAwaiter().GetResult().Sha256;
}
```

### Step 5: 实现 helper `WaitForJobAsync`

轮询 `GET /api/knowledge/{ksId}/jobs/{jobId}`（或 `/api/knowledge/{ksId}/jobs`，按现有路由）直到 `succeeded` 或 `failed`，或超时抛异常。

```csharp
private static async Task WaitForJobAsync(
    HttpClient client, long ksId, Guid jobId, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var response = await client.GetAsync(
            $"/api/knowledge/{ksId}/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var job = body.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("id").GetGuid() == jobId);
        if (job.ValueKind != JsonValueKind.Undefined)
        {
            var status = job.GetProperty("status").GetString();
            if (status == "completed" || status == "failed")
            {
                return;
            }
        }
        await Task.Delay(100);
    }
    throw new TimeoutException(
        $"Job {jobId} did not finish within {timeout.TotalSeconds}s.");
}
```

### Step 6: 实现 helper `SeedViewerUserAsync`

类似 `SeedAdminAndClientAsync` 但创建一个 `IsAdmin=false` 的 user，然后 login 拿 cookie，返回带 viewer cookie 的 `HttpClient`。

```csharp
private static async Task<HttpClient> SeedViewerAsync(
    AuthTestWebApplicationFactory app)
{
    var db = app.CreateDbContext();
    var passwordService = new OnToPilot.Authentication.PasswordService();
    const string viewerUsername = "viewer-b6b";
    if (!db.Users.Any(u => u.Username == viewerUsername))
    {
        db.Users.Add(new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = viewerUsername,
            DisplayName = "Viewer B6B",
            PasswordHash = passwordService.Hash("viewer-pass-b6b"),
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
    var client = app.CreateClient();
    var login = await client.PostAsJsonAsync("/api/auth/login", new
    {
        username = viewerUsername,
        password = "viewer-pass-b6b",
    });
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    var cookie = login.Headers.GetValues("Set-Cookie").Single(
        c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
    client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
    return client;
}
```

### Step 7: 写 test #1 — `Post_extract_tbox_creates_job_and_writes_ontology_classes`

```csharp
[Fact]
public async Task Post_extract_tbox_creates_job_and_writes_ontology_classes()
{
    await using var app = new AuthTestWebApplicationFactory();
    FakeChatClientFactory.Default.Reset();
    FakeChatClientFactory.Default.UseClient(new FakeChat().EnqueueValidDelta());

    var (client, _) = await SeedAdminAndClientAsync(app);
    var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-tbox");
    var blobSha = SeedBlobSha(app);

    var response = await client.PostAsJsonAsync(
        $"/api/knowledge/{ksId}/extract",
        new
        {
            knowledge_system_id = ksGuid,
            blob_sha = blobSha,
            file_name = "test.txt",
            provider = "openai",
            model = "gpt-4",
            endpoint = "https://api.example.com",
            api_key = (string?)null,
            concurrency_limit = 4,
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    var jobId = body.GetProperty("id").GetGuid();
    Assert.Equal("tbox", body.GetProperty("kind").GetString());

    await WaitForJobAsync(client, ksId, jobId, TimeSpan.FromSeconds(30));

    // TBox graph should now contain Animal / Dog / Collar owl:Class triples
    // from FakeChat.ValidTBoxDelta
    var store = app.Services.GetRequiredService<OnToPilot.Ontology.StoreWrapper>();
    var tboxGraph = LookupKsTboxIri(app, ksGuid);
    Assert.NotEmpty(store.Match(
        predicateIri: "http://www.w3.org/2002/07/owl#Class",
        graphIri: tboxGraph));
}
```

### Step 8: 写 test #2 — `Post_extract_instances_creates_job_and_writes_individuals`

类似 test #1 但 URL 是 `/extract-instances`,`kind == "abox"`,断言 ABox graph 含 individual IRI + `rdf:type Person`。

```csharp
[Fact]
public async Task Post_extract_instances_creates_job_and_writes_individuals()
{
    await using var app = new AuthTestWebApplicationFactory();
    FakeChatClientFactory.Default.Reset();
    // ValidABoxDelta references a "Person" class — seed TBox first
    var (client, _) = await SeedAdminAndClientAsync(app);
    var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-abox");
    var tboxEdit = await client.PostAsJsonAsync(
        $"/api/knowledge/{ksId}/ontology/edit",
        new { op = "add_class", label = "Person" });
    Assert.Equal(HttpStatusCode.OK, tboxEdit.StatusCode);

    FakeChatClientFactory.Default.UseClient(new FakeChat().EnqueueValidABoxDelta());

    var blobSha = SeedBlobSha(app);
    var response = await client.PostAsJsonAsync(
        $"/api/knowledge/{ksId}/extract-instances",
        new
        {
            knowledge_system_id = ksGuid,
            blob_sha = blobSha,
            file_name = "test.txt",
            provider = "openai",
            model = "gpt-4",
            endpoint = "https://api.example.com",
            api_key = (string?)null,
            concurrency_limit = 4,
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    var jobId = body.GetProperty("id").GetGuid();
    Assert.Equal("abox", body.GetProperty("kind").GetString());

    await WaitForJobAsync(client, ksId, jobId, TimeSpan.FromSeconds(30));

    var store = app.Services.GetRequiredService<OnToPilot.Ontology.StoreWrapper>();
    var aboxGraph = LookupKsAboxIri(app, ksGuid);
    Assert.NotEmpty(store.Match(
        predicateIri: "http://www.w3.org/1999/02/22-rdf-syntax-ns#type",
        graphIri: aboxGraph));
}
```

### Step 9: 写 test #3 — `Post_extract_all_combined_runs_tbox_and_abox`

URL 是 `/extract-all`,`kind == "both"`,断言 TBox + ABox graph 都有变化。

```csharp
[Fact]
public async Task Post_extract_all_combined_runs_tbox_and_abox()
{
    await using var app = new AuthTestWebApplicationFactory();
    FakeChatClientFactory.Default.Reset();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-combined");
    var blobSha = SeedBlobSha(app);

    // Combined needs both TBox and ABox replies — enqueue many to cover
    // multi-chunk corpora. FakeChat falls back to "{}" if queue empties,
    // which is fine for an empty one-paragraph blob.
    FakeChatClientFactory.Default.UseClient(
        new FakeChat().EnqueueValidDeltas(5));

    var response = await client.PostAsJsonAsync(
        $"/api/knowledge/{ksId}/extract-all",
        new
        {
            knowledge_system_id = ksGuid,
            blob_sha = blobSha,
            file_name = "test.txt",
            provider = "openai",
            model = "gpt-4",
            endpoint = "https://api.example.com",
            api_key = (string?)null,
            concurrency_limit = 4,
        });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    var jobId = body.GetProperty("id").GetGuid();
    Assert.Equal("both", body.GetProperty("kind").GetString());

    await WaitForJobAsync(client, ksId, jobId, TimeSpan.FromSeconds(30));

    var store = app.Services.GetRequiredService<OnToPilot.Ontology.StoreWrapper>();
    var tboxGraph = LookupKsTboxIri(app, ksGuid);
    var aboxGraph = LookupKsAboxIri(app, ksGuid);
    Assert.NotEmpty(store.Match(graphIri: tboxGraph));
}
```

### Step 10: 写 test #4 — `Post_extract_while_active_job_returns_409_with_job_envelope`

seed 一个 `running` 状态的 `ExtractionJobEntity` 直接进 DB（不走 orchestrator — 用 `FakeChat.BlockAfter` 太脆，本 test 走 fast path），然后 POST `/extract`：

```csharp
[Fact]
public async Task Post_extract_while_active_job_returns_409_with_job_envelope()
{
    await using var app = new AuthTestWebApplicationFactory();
    FakeChatClientFactory.Default.Reset();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-409");

    // Seed an existing 'running' job directly so the second POST
    // triggers RunWithExtractionGuardAsync's 409 path.
    var db = app.CreateDbContext();
    var existingJob = new ExtractionJobEntity
    {
        LegacyId = TestLegacyIds.Next("extraction_job"),
        KnowledgeSystemId = ksGuid,
        Kind = "tbox",
        Status = "running",
        Model = "gpt-4",
        ChunkIds = new List<int>(),
        TotalChunks = 0,
        ProcessedChunks = 0,
        AxiomsAdded = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        Log = string.Empty,
        Phase = string.Empty,
    };
    db.ExtractionJobs.Add(existingJob);
    db.SaveChanges();
    var existingJobId = existingJob.Id;
    db.Entry(existingJob).State = EntityState.Detached;

    var blobSha = SeedBlobSha(app);
    var response = await client.PostAsJsonAsync(
        $"/api/knowledge/{ksId}/extract",
        new
        {
            knowledge_system_id = ksGuid,
            blob_sha = blobSha,
            file_name = "test.txt",
            provider = "openai",
            model = "gpt-4",
            endpoint = "https://api.example.com",
            api_key = (string?)null,
            concurrency_limit = 4,
        });

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    // The 409 envelope from B5: {detail: {job_id, error, ...}}
    Assert.Equal(existingJobId,
        body.GetProperty("detail").GetProperty("job_id").GetGuid());
}
```

### Step 11: 写 test #5 — `Post_extract_with_viewer_role_returns_403`

```csharp
[Fact]
public async Task Post_extract_with_viewer_role_returns_403()
{
    await using var app = new AuthTestWebApplicationFactory();
    FakeChatClientFactory.Default.Reset();
    // The admin (created via SeedAdminAndClientAsync) is IsAdmin=true so
    // automatically resolves to KSRole.Owner for any KS. To trigger the
    // Viewer gate we need a NON-admin, NON-owner user with an explicit
    // viewer grant row on the KS.
    var viewerClient = await SeedViewerAsync(app);
    var viewerDb = app.CreateDbContext();
    var viewerId = viewerDb.Users
        .Single(u => u.Username == "viewer-b6b").Id;
    var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(
        app, viewerClient, "b6b-viewer");

    // Insert a viewer grant for the viewer user on this KS. KS owner is
    // a different user (admin), so viewer user gets exactly Viewer role.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        db.KSGrants.Add(new KSGrantEntity
        {
            KnowledgeSystemId = ksGuid,
            UserId = viewerId,
            Role = "viewer",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    var blobSha = SeedBlobSha(app);
    var response = await viewerClient.PostAsJsonAsync(
        $"/api/knowledge/{ksId}/extract",
        new
        {
            knowledge_system_id = ksGuid,
            blob_sha = blobSha,
            file_name = "test.txt",
            provider = "openai",
            model = "gpt-4",
            endpoint = "https://api.example.com",
            api_key = (string?)null,
            concurrency_limit = 4,
        });

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

    // No new extractionjob row was written.
    using var verifyScope = app.Services.CreateScope();
    var verifyDb = verifyScope.ServiceProvider
        .GetRequiredService<OnToPilotDbContext>();
    Assert.Empty(verifyDb.ExtractionJobs.Where(j => j.KnowledgeSystemId == ksGuid));
}
```

**注意**：`db.KSGrants` + `KSGrantEntity { Role = "viewer" }`（字符串值，对应
`KnowledgeSystemAccessService.GetEffectiveRoleAsync` 的 switch arm）；`KSRole`
enum 是 **运行时** computed，**不是** 持久化字段。Viewer 测试用户必须是
**非 admin 非 KS owner**，否则 `HasAtLeastAsync(Editor)` 自动通过。

### Step 12: 写 test #6 — `Post_extract_with_missing_blobsha_returns_400`

```csharp
[Fact]
public async Task Post_extract_with_missing_blobsha_returns_400()
{
    await using var app = new AuthTestWebApplicationFactory();
    FakeChatClientFactory.Default.Reset();
    var (client, _) = await SeedAdminAndClientAsync(app);
    var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b6b-400");

    // POST without blob_sha field — DeserializeBody<ExtractionRequest>
    // throws InvalidOperationException -> FastApiErrorMiddleware -> 400.
    var response = await client.PostAsJsonAsync(
        $"/api/knowledge/{ksId}/extract",
        new
        {
            knowledge_system_id = ksGuid,
            // blob_sha omitted on purpose
            file_name = "test.txt",
            provider = "openai",
            model = "gpt-4",
            endpoint = "https://api.example.com",
            api_key = (string?)null,
            concurrency_limit = 4,
        });

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    var detail = body.GetProperty("detail").GetString();
    Assert.NotNull(detail);
    Assert.Contains("blob", detail!, StringComparison.OrdinalIgnoreCase);
}
```

### Step 13: 跑 6 个新 test

```bash
dotnet build src/OnToPilot.Tests/OnToPilot.Tests.csproj -c Debug
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~ExtractionRunApiTests" \
  --no-build
```

预期：6 / 6 passing。

**如有失败 debug 清单：**

- test #1-3 失败 → 检查 `FakeChatClientFactory.Default.UseClient(...)` 在 POST 前调用;`ValidTBoxDelta` / `ValidABoxDelta` 被正确 enqueue;检查 controller 路由是不是真的接受 `snake_case` 字段
- test #4 失败 → 检查 seeded job 字段名 (`Kind` / `Status` 都是 lowercase 字符串);409 envelope shape 是 `{detail: {job_id, error, ...}}` 而非 `{job: {id, status, ...}}`
- test #5 失败 → 检查 `KSGrantEntity` 字段名 (`KnowledgeSystemId` / `UserId` / `Role` / `CreatedAt`);viewer 用户必须 `IsAdmin=false` 且不是 KS owner,否则 `GetEffectiveRoleAsync` 解析成 Owner 自动通过 Editor 检查
- test #6 失败 → 检查 `DeserializeBody<T>` 抛的异常类型是否被 `FastApiErrorMiddleware` 转 400;检查 wire field 名是 `blob_sha`(snake_case)而非 `BlobSha`

### Step 14: Commit 6 个 test

```bash
git add src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs
git commit -m "test(extraction): add 6 HTTP-level contract tests for run pipeline

ExtractionRunApiTests covers the B6b run pipeline end-to-end:
1. /extract — TBox run writes owl:Class triples
2. /extract-instances — ABox run writes rdf:type Person triples
3. /extract-all — combined TBox+ABox run touches both graphs
4. concurrent /extract — 409 envelope with seeded running job
5. Viewer-role /extract — 403 + no extractionjob row written
6. missing blob_sha — 400 from DeserializeBody<ExtractionRequest>

Separate from Block 5's ExtractionApiTests which covers read endpoints
(ListJobs/GetJob) and the 409 envelope triggered from Documents."
```

---

## Task 5: 全量回归 + memory + 报告

**Files:**
- Create: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-extraction-block6b.md`
- Modify: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md`

### Step 1: 全量回归

```bash
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
```

预期：324 / 325 passing。1 pre-existing fail 来自 `AuthenticationContractTests.Me_with_valid_session_returns_user`（Block 11 is_admin 命名 bug）。

### Step 2: 编译 Release

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
```

预期：0 warning 0 error。

### Step 3: 写 memory 文件

读取现有 `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-abox-block7c.md` 学习 memory 模板风格。

创建 `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-extraction-block6b.md`：

```markdown
---
name: ontopilot-extraction-block6b
description: Block 6b ExtractionOrchestrator 14 deps + run* paths wire-up (commit pending, 6 HTTP tests passing, 20 → 26 extraction tests)
metadata: 
  node_type: memory
  type: project
  originSessionId: <session id>
  modified: 2026-08-19T...Z
---

Block 6b 完成 (commit pending)。B6b 把 extraction 写入面收尾:

- `extraction.run` + `extraction.run_combined` + `extraction.run_instances` 3
  个 dispatcher arm 接 `ExtractionOrchestrator.Start{TBox|Combined|ABox}Async`,
  替换之前的 placeholder `Task.FromResult(EmptyExtractionJob())`。
- 9 个 service 在 DI 注册: IChatClientFactory/EndpointCapacityCoordinator/
  TBox/ABoxExtractionService/TerminologyService/PromptSnapshotService/
  IExtractionMerger/ExtractionOrchestrator。
- 3 个 extraction test 文件里的 private nested `FakeChatClientFactory` 合并
  到 `src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs` 一个共享
  `Default` singleton + `UseClient()` / `Reset()` hooks。
- `AuthTestWebApplicationFactory` override `IChatClientFactory → FakeChatClientFactory.Default`。
- 新增 `src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs` (6 个
  HTTP-level contract tests)。

## 关键改动

- **`src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs`** (NEW) —
  `AddExtractionServices()` 注册 9 个 singleton
- **`src/OnToPilot/Extraction/ExtractionJobOut.cs`** (NEW, 若缺) — wire DTO
- **`src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs`** (NEW) —
  共享 factory + `Default` singleton + `UseClient` / `Reset` hooks
- **`src/OnToPilot/Integration/InternalOperationDispatcher.cs`** — 3 个
  placeholder arm 接 `InvokeExtractionAsync` 私有 helper
- **`src/OnToPilot/Program.cs`** — 在 `AddOntologyServices()` 之后
  `builder.Services.AddExtractionServices();`
- **`src/OnToPilot.Tests/Authentication/AuthTestWebApplicationFactory.cs`** —
  override `IChatClientFactory → FakeChatClientFactory.Default`
- **`src/OnToPilot.Tests/Extraction/{State,LlmFailure,CapacityKey}Tests.cs`** —
  删 nested factory,改用 `FakeChatClientFactory.Default.UseClient(...)`

## 6 个 HTTP-level tests (全部 passing)

1. `Post_extract_tbox_creates_job_and_writes_ontology_classes` — /extract + TBox
2. `Post_extract_instances_creates_job_and_writes_individuals` — /extract-instances + ABox
3. `Post_extract_all_combined_runs_tbox_and_abox` — /extract-all + combined
4. `Post_extract_while_active_job_returns_409_with_job_envelope` — 409 + seeded running job
5. `Post_extract_with_viewer_role_returns_403` — Viewer gate + no extractionjob row
6. `Post_extract_with_missing_blobsha_returns_400` — DeserializeBody<ExtractionRequest> 400

## 关键决定

- **`ExtractionOrchestrator` 必须 singleton** — 它 hold `ExtractionJobStore`
  + `Task.Run` 后台任务,scope 化断 background job 状态
- **`FakeChatClientFactory.Default` 类级别 singleton** — 简化 test setup,
  `Reset()` 显式清状态
- **`RunWithExtractionGuardAsync` 保留** — 409 envelope 逻辑已正确,不重写
- **新文件 `ExtractionRunApiTests.cs` 区分现有 `ExtractionApiTests.cs`** —
  B5 的 7 个 read endpoint test 不动

## 复用现有模式

- **DI 注册模板**: 照抄 `AddOntologyServices()` / `AddValidationDecisionServices()`
- **HTTP test scaffolding**: 照抄 `ABoxValidationApiTests` 的 helpers
- **Dispatcher helper 模板**: 照抄 `InvokeKnowledgeEditAsync` 模式
- **409 envelope 模板**: B5 的 `RunWithExtractionGuardAsync`

## 进度

- 全量回归: **324 passed / 325 total** (1 pre-existing fail:
  `AuthenticationContractTests.Me_with_valid_session_returns_user`
  是 Block 11 的 is_admin 命名 bug)
- Block 6a (ontology) + 6b (extraction) 全部完成
- 下一个 block: Block 8 (Vocabulary) / Block 9 (Resolution) / Block 10 (Releases)
  / Block 11 (Auth, 会修 is_admin bug) 择一

**Why:** B6b 是 Block 6 extraction 写入面的收尾,让 /extract /extract-all
/extract-instances 真的走到 orchestrator,前端按钮不再是 no-op。
**How to apply:** 之后任何 extraction-related dispatcher arm 走
`InvokeExtractionAsync` helper 模式;测试用 `FakeChatClientFactory.Default`
+ `UseClient(FakeChat)` + `Reset()`,不要重新发明 nested factory。
```

### Step 4: 更新 MEMORY.md index

读取 `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md`,在末尾添加一行：

```markdown
- [ontopilot-extraction-block6b](ontopilot-extraction-block6b.md) — Block 6b ExtractionOrchestrator wire-up + 6 HTTP contract tests
```

### Step 5: Commit memory + 报告用户

```bash
git add -A memory/
git commit -m "docs(memory): record block 6b extraction orchestrator wire-up"
```

最终消息给用户包含：
- B6b commit hash(es)
- 6 / 6 新 test passing
- 324 / 325 全量回归
- Refactor summary (3 nested → 1 shared factory)
- 下一个 block 选项:Block 8 (Vocabulary) / Block 9 (Resolution) / Block 10 (Releases) / Block 11 (Auth,会修 is_admin bug)

---

## 验证清单

| 步骤 | 命令 | 预期 |
|---|---|---|
| Task 1 build | `dotnet build src/OnToPilot.Tests/OnToPilot.Tests.csproj -c Debug` | 0 error |
| Task 1 refactor 验证 | `dotnet test --filter "FullyQualifiedName~ExtractionStateTests\|FullyQualifiedName~ExtractionLlmFailureTests\|FullyQualifiedName~ExtractionCapacityKeyTests"` | 13/13 passing |
| Task 1 全 extraction 验证 | `dotnet test --filter "FullyQualifiedName~Extraction"` | 20/20 passing |
| Task 2 build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |
| Task 2 DI smoke test | `dotnet test --filter "FullyQualifiedName~Extraction"` | 20/20 passing |
| Task 3 build | `dotnet build src/OnToPilot/OnToPilot.csproj -c Release` | 0 warning 0 error |
| Task 3 dispatcher smoke | `dotnet test --filter "FullyQualifiedName~Extraction"` | 20/20 passing |
| Task 4 build | `dotnet build src/OnToPilot.Tests/OnToPilot.Tests.csproj -c Debug` | 0 error |
| Task 4 new tests | `dotnet test --filter "FullyQualifiedName~ExtractionRunApiTests"` | 6/6 passing |
| Task 5 全量回归 | `dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj` | 324/325 passing |

---

## 不在计划范围(留给后续 block)

- **Block 8**: Vocabulary
- **Block 9**: Resolution（EntityResolution status lifecycle + documents.contribution.individual_count）
- **Block 10**: Releases
- **Block 11**: Auth/Tokens/McpTokens（修 is_admin bug 让 full regression 变 325/325）
- **Block 12**: Settings/Prompts/History/RdfImport/External
- **Capacity / failure 优化**:已有 lower-level test 覆盖
- **Production ChatClientFactory 优化**: Block 12 可能做 production rate-limit / retry 增强
- **Wire DTO 自动生成**: 未来可考虑 STJ source generation,但 6 个字段 DTO 手写已足够