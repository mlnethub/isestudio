# Block 6b — ExtractionOrchestrator 14 deps + `run*` paths wire-up

**Date**: 2026-08-19
**Branch**: `dotnet`
**Status**: Design (awaiting approval)
**Previous block**: Block 7c — ABox reset + validate + fix_violation (commit `10bd837`, no spec doc)

## Context

Block 5 把 `ExtractionJobStore` 接进真 SQL persistence + 409 envelope
(commit `5766cff`)。Block 6 已经接真 `ontology.edit / ontology.reset` +
`documents.impact` + `AuditEventEntity.Added/Removed` N-Quads diff(commit
`ed3c694`)。

但 `InternalOperationDispatcher.cs:110-120` 的 3 个 extraction.run* arm
仍是 placeholder:

```csharp
"extraction.run" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => Task.FromResult<object?>(EmptyExtractionJob())),
"extraction.run_combined" => RunWithExtractionGuardAsync(..., ...),
"extraction.run_instances" => RunWithExtractionGuardAsync(..., ...),
```

`ExtractionController` 的 3 个 POST endpoint(`/extract`, `/extract-all`,
`/extract-instances`)调 dispatcher 但拿到的永远是个空 job。

**目标**:把 3 个 arm 接 `ExtractionOrchestrator.Start{TBox|Combined|ABox}Async`,
注册 `ExtractionOrchestrator` 的 14 个依赖,合并现有 3 个测试文件里的
`FakeChatClientFactory` private nested class,新增 6 个 HTTP-level
integration test。

## 已查证的关键事实

| 项 | 现状 |
|---|---|
| `ExtractionOrchestrator` 14 deps | 已实现,公开 `StartTBoxAsync / StartABoxAsync / StartCombinedAsync`(line 107-126)|
| `ExtractionRequest` record | 已存在(`ExtractionRequest.cs`),`KnowledgeSystemId / BlobSha / FileName / Provider / Model / Endpoint / ApiKey / ConcurrencyLimit` |
| `ExtractionController` 3 个 POST | 已存在,调 dispatcher 的 3 个 arm |
| `RunWithExtractionGuardAsync` + `RejectIfExtractionActiveAsync` | 已存在(同 Block 5),wrap 3 个 arm + 409 envelope |
| `IChatClientFactory` → `ChatClientFactory` (production) | 已存在,无 deps |
| `IChatClientFactory` override in `AuthTestWebApplicationFactory` | **不存在**,需新增 |
| `FakeChatClientFactory` private nested class | **重复 3 次**(`ExtractionStateTests` / `ExtractionLlmFailureTests` / `ExtractionCapacityKeyTests`) |
| `ExtractionJobStore` (singleton) | 已注册(Block 5)|
| `StoreWrapper` (singleton) | 已注册(Block 6) |
| `IBlobStore` / `IDocumentParser` / `Chunker` / `TimeProvider` | 已注册 |
| `IChatClientFactory` / `EndpointCapacityCoordinator` / `TBoxExtractionService` / `ABoxExtractionService` / `TerminologyService` / `PromptSnapshotService` / `IExtractionMerger` / `ExtractionOrchestrator` | **未注册**,需新增 9 个 |

## 关键设计决定

### D1: 9 个 service 全部 Singleton

`ExtractionOrchestrator` 必须 singleton — 它 hold `ExtractionJobStore`
(reference) + `Task.Run` 后台任务 + `ExecutionContext.SuppressFlow()`,scope 化
会断 background job 状态。

子依赖也都是无状态或线程安全的:
- `IChatClientFactory` 无 deps
- `EndpointCapacityCoordinator` AsyncLocal reentrancy + 进程级 permit bucket
- `TBoxExtractionService` / `ABoxExtractionService` 构造无参,`IChatClient` per call
- `TerminologyService` / `PromptSnapshotService` / `IExtractionMerger` 只依赖
  `StoreWrapper`(已 singleton)

### D2: `FakeChatClientFactory` 合并为共享 singleton

3 个测试文件里的 private nested `FakeChatClientFactory` 合并到
`src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs`(top-level,
`Default` static property 走类级别 singleton)。

`AuthTestWebApplicationFactory.ConfigureWebHost` override:

```csharp
services.RemoveAll<IChatClientFactory>();
services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
```

每个 extraction test 第一行 `FakeChatClientFactory.Default.Reset()` 清 queue +
block state。

### D3: Dispatcher 3 arm 走统一 helper

`InternalOperationDispatcher.cs` 加 `InvokeExtractionAsync` 私有方法:

```csharp
private async Task<object?> InvokeExtractionAsync(
    IntegrationRequest request, string runKind, CancellationToken ct)
{
    var body = DeserializeBody<ExtractionRequest>(request, "extraction body");
    var orchestrator = Resolve<ExtractionOrchestrator>();
    var job = runKind switch {
        "extraction.run"           => await orchestrator.StartTBoxAsync(body, ct),
        "extraction.run_combined"  => await orchestrator.StartCombinedAsync(body, ct),
        "extraction.run_instances" => await orchestrator.StartABoxAsync(body, ct),
        _ => throw new InvalidOperationException(...)
    };
    return MapJob(job);
}
```

3 个 arm 改调此 helper,外层仍走 `RunWithExtractionGuardAsync`(保留 409 逻辑)。

### D4: `MapJob` / `ExtractionJobOut`

`ExtractionJobEntity` 是 EF entity(不能直接 serialise)。检查 `ExtractionJobEntity`
是否有现成 wire DTO — 若无,新增 `ExtractionJobOut` record:

```csharp
public sealed record ExtractionJobOut(
    Guid Id,
    Guid KnowledgeSystemId,
    string Kind,
    string Status,
    int Progress,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);
```

字段映射由 `MapJob` helper 直接手写;实现前先查 `ExtractionJobEntity` 是否已有 wire
映射方法(若有则复用,避免双源真理),否则按此 record 字段一一对应手写。

### D5: `RunWithExtractionGuardAsync` 保留

它已经包了"find active job → 409 + job envelope"逻辑,3 个 arm 复用,不需要新写。

### D6: 不重写现有 lower-level extraction test

`ExtractionStateTests.cs` (9 tests) / `ExtractionLlmFailureTests.cs` (2 tests) /
`ExtractionCapacityKeyTests.cs` (2 tests) + `ExtractionApiTests.cs` (7 tests,
Block 5 read endpoints) — 共 20 个现有 test 是 lower-level(direct service
call / 读 endpoint / 409 envelope from Documents)。
B6b 新加的 6 个是 dispatcher-level 集成(`WebApplicationFactory` HTTP client
走真 orchestrator + Editor gate + 400 body)。两者覆盖不同层,不互替代。

## 文件改动清单

### 新增(3 个)

1. **`src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs`** —
   `AddExtractionServices()` 注册 9 个 services(全 singleton)
2. **`src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs`** —
   共享 `FakeChatClientFactory` + `Default` singleton + Reset/Queue/Block hooks
3. **`src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs`** —
   6 个 HTTP-level contract tests for run pipeline(与现有
   `ExtractionApiTests.cs` 区分:现有 7 个覆盖 read endpoints + 409 envelope
   from Documents,本文件覆盖 run pipeline + Editor gate + 400 body)

> 注:`FakeChat.cs` 在 `src/OnToPilot.Tests/Extraction/FakeChat.cs` 已是
> top-level,不需新增。`FakeChatClientFactory` 与 `FakeChat` 配合使用:
> factory 负责入队 + 计数,`FakeChat` 本身持有 LLM 响应文本与 `Release()`
> 阻塞钩子。

### 修改(6 个)

5. **`src/OnToPilot/Program.cs`** — 在 `AddOntologyServices()` 之后
   `builder.Services.AddExtractionServices();`
6. **`src/OnToPilot.Tests/Authentication/AuthTestWebApplicationFactory.cs`** —
   `ConfigureWebHost` override `IChatClientFactory → FakeChatClientFactory.Default`
7. **`src/OnToPilot/Integration/InternalOperationDispatcher.cs`** —
   3 个 `extraction.run*` arm 接 `InvokeExtractionAsync` 私有方法
8. **`src/OnToPilot.Tests/Extraction/ExtractionStateTests.cs`** — 删 nested
   `FakeChatClientFactory`,改用 `FakeChatClientFactory.Default`
9. **`src/OnToPilot.Tests/Extraction/ExtractionLlmFailureTests.cs`** — 同上
10. **`src/OnToPilot.Tests/Extraction/ExtractionCapacityKeyTests.cs`** — 同上

## 6 个 HTTP contract tests

| # | Test 名 | 覆盖路径 | 关键断言 |
|---|---|---|---|
| 1 | `Post_extract_tbox_creates_job_and_writes_ontology_classes` | `POST /extract` → `extraction.run` | 200 + TBox graph 含 `FakeChat.ValidTBoxDelta` 期望的 class IRI |
| 2 | `Post_extract_instances_creates_job_and_writes_individuals` | `POST /extract-instances` | 200 + ABox graph 含 individual IRI + `rdf:type` triple |
| 3 | `Post_extract_all_combined_runs_tbox_and_abox` | `POST /extract-all` | 200 + TBox + ABox graph 都变化 |
| 4 | `Post_extract_while_active_job_returns_409_with_job_envelope` | `RunWithExtractionGuardAsync` | 第二次 POST 在第一次 running 时返回 409,body 含 `{error, job:{id, status, ...}}` |
| 5 | `Post_extract_with_viewer_role_returns_403` | Editor gate | Viewer POST → 403,`extractionjob` 表无新行 |
| 6 | `Post_extract_with_missing_blobsha_returns_400` | `DeserializeBody<ExtractionRequest>` | 缺 `BlobSha` → 400 |

## 关键文件路径速查

| 用途 | 路径 |
|---|---|
| DI 注册扩展(新增) | `src/OnToPilot/Extraction/ExtractionServiceCollectionExtensions.cs` |
| 共享 FakeChat factory(新增) | `src/OnToPilot.Tests/Extraction/FakeChatClientFactory.cs` |
| HTTP contract tests(新增) | `src/OnToPilot.Tests/Extraction/ExtractionRunApiTests.cs` |
| Program DI(改) | `src/OnToPilot/Program.cs`(在 `AddOntologyServices()` 之后) |
| Test factory override(改) | `src/OnToPilot.Tests/Authentication/AuthTestWebApplicationFactory.cs` |
| Dispatcher arm(改) | `src/OnToPilot/Integration/InternalOperationDispatcher.cs:110-120` |
| 3 个现有 test refactor(改) | `src/OnToPilot.Tests/Extraction/Extraction{State,LlmFailure,CapacityKey}Tests.cs` |

## 复用现有代码

- **DI 注册模板**:照抄 `AddOntologyServices()` / `AddValidationDecisionServices()` / `AddConflictServices()`
- **HTTP test scaffolding**:照抄 `ABoxValidationApiTests` 的 `SeedAdminAndClientAsync` + `CreateKsAsync` + `LookupKsAboxIri` helpers
- **Dispatcher helper 模板**:照抄 `InvokeKnowledgeEditAsync` / `InvokeOntologyEditAsync` 模式
- **409 envelope 模板**:Block 5 的 `RejectIfExtractionActiveAsync` + `RunWithExtractionGuardAsync`(已存在,不重写)
- **Role gate 模板**:照抄 `KnowledgeService.RequireRoleAsync` 的 `KSRole.Viewer / Editor / Owner`

## 实现步骤

1. **写** `FakeChatClientFactory.cs`(top-level shared singleton + hooks)
2. **改** `AuthTestWebApplicationFactory.cs`(override `IChatClientFactory`)
3. **改** 3 个现有 extraction test 文件(`ExtractionState/LlmFailure/CapacityKey` Tests 共 13 个 test,删 nested factory,改用 `Default`)
4. **跑现有 13 个 lower-level extraction test** 验证 refactor 不破坏
5. **写** `ExtractionServiceCollectionExtensions.cs`(`AddExtractionServices()`)
6. **改** `Program.cs`(调 `AddExtractionServices()`)
7. **改** `InternalOperationDispatcher.cs`(3 个 arm 接 `InvokeExtractionAsync`)
8. **写** `ExtractionRunApiTests.cs`(6 个 HTTP-level contract tests,run pipeline + Editor gate + 400 body)
9. **编译** `dotnet build -c Release` 0 error 0 warning
10. **跑新 tests** `dotnet test --filter ExtractionApiTests` 期望 6/6 pass
11. **跑全量回归** `dotnet test` 期望 318(B7c)+ 6(B6b 新加)= 324/325 pass(1 pre-existing Block 11 is_admin fail)
12. **Commit + memory + 报告用户**

## 验证

### 单元 + 集成层

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release          # 0 warning 0 error
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
    --filter "FullyQualifiedName~ExtractionRunApiTests"        # 6 passed
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
    --filter "FullyQualifiedName~ExtractionStateTests|\
FullyQualifiedName~ExtractionLlmFailureTests|\
FullyQualifiedName~ExtractionCapacityKeyTests"                  # 6 passed (refactor 不破坏)
dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj           # 324/325 pass
```

### 浏览器手测清单(用户执行)

1. 登录 admin → 进任一 KS
2. 上传一个 txt 文档 → 拿到 `BlobSha`
3. POST `/api/knowledge/{ks}/extract` body `{blob_sha, ...}` → 应返回 job id
4. 轮询 GET `/api/jobs/{id}` 直到 `status=succeeded`
5. 进 KS 的 Ontology tab → 应有新 class 出现
6. POST `/extract-instances` → ABox 应有新 individual
7. POST `/extract-all` → TBox + ABox 同时更新
8. 故意并发 POST `/extract` 两次 → 第二个应 409 + 含 job envelope
9. 用 Viewer 账号 POST → 应 403

## 不在本设计范围(留给后续 block)

- **Block 8**: Vocabulary
- **Block 9**: Resolution(`EntityResolution` status lifecycle + `documents.contribution.individual_count`)
- **Block 10**: Releases
- **Block 11**: Auth/Tokens/McpTokens(会修 `is_admin` 命名 bug 让 full regression 变 325/325)
- **Block 12**: Settings/Prompts/History/RdfImport/External
- **Capacity / failure 单测**:现有 13 个 lower-level test 覆盖,不在 HTTP-level 重复
- **`ExtractionJobEntity.ToWire()` 优化**:当前 plan 直接手写 `MapJob`,后续可加 entity method

## 风险与回退

- **风险 1**: 9 个 singleton 注入顺序错 → 用 `AddExtractionServices()` 集中管理,
  失败时 `dotnet build` 会报 missing constructor parameter
- **风险 2**: `FakeChatClientFactory.Default` 跨测试泄漏 → 每个 test 第一行
  `Reset()`,xUnit 默认 parallel-by-collection 内 sequential
- **风险 3**: Dispatcher 解析 `ExtractionOrchestrator` 时 scope 链断 → singleton
  可从 scoped context 解析,ASP.NET Core 允许,无需额外 scope 包装
- **风险 4**: 现有 13 个 lower-level test refactor 引入回归 → refactor 后单独跑
  一次 13 个 test 验证
- **回退**: B6b 拆成 3 个 commit(refactor + DI + dispatcher + tests),任何子步失败
  可单步 revert

## 设计选择 Summary

| 决策点 | 选 | 不选 | 理由 |
|---|---|---|---|
| DI lifetime | Singleton 全 9 个 | Scoped `TBox/ABox` | 无状态 + thread-safe + orchestrator 必须 singleton |
| FakeChat 共享 | 共享 singleton + hooks | 每文件 nested | 清技术债,DRY |
| Dispatcher 3 arm | 统一 `InvokeExtractionAsync` helper | 各 arm 内联 | DRY,switch 易读 |
| `MapJob` DTO | 新增 `ExtractionJobOut` record | 直接 return entity | EF entity 不能 serialise |
| `RunWithExtractionGuardAsync` | 保留 | 重写 | 已正确,Block 5 测过 |
| Background work | 信任 orchestrator 内部 `Task.Run` + `SuppressFlow` | dispatcher 再 wrap | 已有正确实现,不要碰 |