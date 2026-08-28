# Conflicts application-service slice (2026-08-28)

> 第二片 dispatcher 拆分叶子,沿用 [[2026-08-28-abox-application-service-pilot]] 锁定的模式。
> 目标:`conflicts.*` 9 op 从 dispatcher 迁到 `IConflictApplicationService`,
> helper 缩成 1 行委托,新增首个 `<Slice>XxxOrchestrator`(`ConflictDetectionOrchestrator`)。

## 1. 背景

[InternalOperationDispatcher.cs](src/ISEStudio/Integration/InternalOperationDispatcher.cs) 4413 行 god-class,
`conflicts.*` 占 9 个 switch arm + 9 个 helper(原 lines 1615-1813,共 ~200 行),
加上一个 service-resolve + status/ctype 解析 helper + ConflictService null-fallback 工厂。

abox pilot 已验证模式可复用,但 conflicts 比 abox 多两件事:
1. **`conflicts.detect` 是多步 fanout** —— `ConflictService.DetectAsync` 之后
   还要 `ConflictAgent.TriageAsync` + `StructureAgent.AttachIsolatedAsync`,
   而 dispatcher 原实现走 `_services.GetService(typeof(ConflictAgent)) as ConflictAgent`
   的 null-degrade 路径(contract-test factory 不注册 agent)。
2. **`conflicts.resolve` 抛 `InvalidOperationException`** —— 缺 body / `resolution_id`
   直接 throw,FastApiErrorMiddleware 翻译成 HTTP 400。

## 2. 设计

### 2.1 接口在 `ISEStudio.Application/Integration/IConflictApplicationService.cs`

9 个强类型方法,每个对应一个 op 名;返回类型与 ConflictService 1:1,只是
把 nullable annotation 与 envelope unpacking 责任从 dispatcher 移到 service。

| Op | 方法 | 返回 |
| --- | --- | --- |
| `conflicts.list` | `ListAsync` | `Task<IReadOnlyList<ConflictOut>?>` |
| `conflicts.detect` | `DetectAsync` | `Task<IReadOnlyList<ConflictOut>?>` |
| `conflicts.get_context` | `GetContextAsync` | `Task<ConflictContext?>` |
| `conflicts.dismiss` | `DismissAsync` | `Task<ConflictOut?>` |
| `conflicts.reopen` | `ReopenAsync` | `Task<ConflictOut?>` |
| `conflicts.resolve` | `ResolveAsync` | `Task<ResolveConflictResponse?>` |
| `conflicts.list_reconciliations` | `ListReconciliationsAsync` | `Task<ReconciliationListResponse>` |
| `conflicts.revoke_reconciliation` | `RevokeReconciliationAsync` | `Task<Guid?>` |
| `conflicts.edit_reconciliation_reason` | `EditReconciliationReasonAsync` | `Task<(Guid Id, string Reason)?>` |

**关键非显然点**:
- `ListReconciliationsAsync` 返非 null `ReconciliationListResponse`(与 `ConflictService` 对齐),
  app service 在 `KnowledgeSystemGuid` 缺失时返回 `new ReconciliationListResponse([], 0)`
  而不是 null(避免 dispatcher 兜 `EmptyListResponse()`,保证 wire shape 与 `ConflictService` 直接
  调用对齐)。
- `EditReconciliationReasonAsync` 返 `(Guid Id, string Reason)?` 而**不是** `ReconciliationOut?`。
  这是因为原 dispatcher 直接 inline projection 成 `{id, reason}` 两个字段(详见 §2.4),
  而不是序列化 11 字段 record。把投影决策保留在 dispatcher,service 只搬运元组。

### 2.2 `<Slice>XxxOrchestrator`:`ConflictDetectionOrchestrator`

第一个 orchestrator,落 2026-08-28 跨 slice 决策 §6.3。

[ConflictDetectionOrchestrator.cs](src/ISEStudio/Conflicts/ConflictDetectionOrchestrator.cs) 拥有
`ConflictService.DetectAsync → ConflictAgent.TriageAsync → StructureAgent.AttachIsolatedAsync`
三步链。ctor 注入 `ConflictService` + `IServiceProvider`,**不**直接注入
`ConflictAgent` / `StructureAgent`,因为:

- 原 dispatcher 走 `_services.GetService(typeof(ConflictAgent)) as ConflictAgent`,
  contract-test factory 不注册 agent 时返回 null,detector 继续跑完(SQL paths 仍可用)。
- 把 agent 强类型注入 ctor 会让 contract-test factory 必须注册一个 stub agent,
  或者把 agent 做成 `ConflictAgent?` 可选——前者增加 factory 负担,后者让 production
  工厂也需要 null 检查。沿用 `_services.GetService` 的 null-degrade 把责任收在一处。

```csharp
var agent = _services.GetService(typeof(ConflictAgent)) as ConflictAgent;
if (agent is not null) { await agent.TriageAsync(...); }
var structure = _services.GetService(typeof(StructureAgent)) as StructureAgent;
if (structure is not null) { await structure.AttachIsolatedAsync(...); }
```

两个 agent 都自门控在 `agentic_*` setting + KS `extraction_active`,并吞掉所有 LLM 错误,
所以 orchestrator 永不抛——`DetectAsync` 返给调用方的 rows 是 pre-triage snapshot,
匹配 Python `backend/app/api/conflicts.py::detect` 行为。

### 2.3 dispatcher helper 改写模式

照搬 abox pilot 的 `Func<IApplicationService, Task<object?>> call + Func<object> onMissing` 模板,
共用一个 `InvokeConflictAsync(...)` wrapper:

```csharp
private Task<object?> InvokeConflictListAsync(InternalRequest request, CancellationToken ct) =>
    InvokeConflictAsync(request, ct,
        async app => (object?)await app.ListAsync(request, ct).ConfigureAwait(false),
        onMissing: Array.Empty<object>);
```

wrapper 多出一个 `Func<object>? onNull` 可选参数——为
`conflicts.resolve`(`onMissing = EmptyConflict`, `onNull = {resolved_cid:Guid.Empty, open_conflicts:[], view:{}}`)
和 `conflicts.revoke_reconciliation`(`onMissing = {ok:false}`, `onNull = {deleted:0}`)
保留"service 缺失 vs service 返 null"两种 fallback 的区分。其他 7 个 op 不传 onNull,
wrapper 内部走 `(onNull ?? onMissing)()`。

### 2.4 `edit_reconciliation_reason` wire shape 守恒

原 dispatcher 在 `result` 不为 null 时 inline `{id = result.Value.Id, reason = result.Value.Reason}`,
而不是序列化 `ConflictService.EditReconciliationReasonAsync` 返回的完整元组 / 任何
含 `slot` / `property_label` / `candidates` 等的 record。app service 沿用元组返,
dispatcher 端 lambda 内手写投影:

```csharp
var result = await app.EditReconciliationReasonAsync(request, ct).ConfigureAwait(false);
if (result is null) return EmptyReconciliation();
return (object?)new { id = result.Value.Id, reason = result.Value.Reason };
```

理由:Python `/api/knowledge/{ks_id}/reconciliation/{reconciliation_id}` 端点的 wire shape
**就是** `{id, reason}`(无其他字段),不能扩成 11 字段。OpenAPI 契约测试会断。

### 2.5 `resolve` 的 `InvalidOperationException` 保留在 app service

`body is null || string.IsNullOrEmpty(body.ResolutionId)` 时 service 抛 `InvalidOperationException`,
FastApiErrorMiddleware 翻成 HTTP 400 envelope。dispatcher 不再做 body 校验,只做
service 缺失 → `EmptyConflict()` 兜底。

### 2.6 DI 注册走 `AddConflictServices` 扩展,不改 `Program.cs`

`ConflictServiceCollectionExtensions.AddConflictServices` 多注册一行:
`services.AddScoped<IConflictApplicationService, ConflictApplicationService>()`
+ `services.AddScoped<ConflictDetectionOrchestrator>()`。
`Program.cs:398` 的 `AddConflictServices()` 一行不动。

### 2.7 跨 slice 决策落地(2026-08-28 §6)

| 决策 | conflicts slice 落地 |
| --- | --- |
| 跨 slice helper 集中到 `InternalRequestHelpers.cs` | 已完成(abox 试点时合并) |
| DTO 按 slice 分目录 | `ISEStudio.Application/Conflicts/ConflictDtos.cs`(`mkdir + cp + rm` 模式从 `ISEStudio/Conflicts/ConflictDtos.cs` 搬过来,git rename 检测) |
| detect fanout → orchestrator | `ConflictDetectionOrchestrator`(首个落地) |

## 3. 实施

### 3.1 文件清单

| 文件 | 变化 |
| --- | --- |
| `src/ISEStudio.Application/Conflicts/ConflictDtos.cs` | **移动** from `src/ISEStudio/Conflicts/ConflictDtos.cs` + namespace 改 |
| `src/ISEStudio.Application/Integration/IConflictApplicationService.cs` | **新增** —— 9 强类型方法 |
| `src/ISEStudio/Integration/ConflictApplicationService.cs` | **新增** —— envelope unpacking + 调 ConflictService + 调 ConflictDetectionOrchestrator |
| `src/ISEStudio/Conflicts/ConflictDetectionOrchestrator.cs` | **新增** —— 首个 `<Slice>XxxOrchestrator`,Detect 三步链 |
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | 9 helper 缩成 1 行委托;新增 `InvokeConflictAsync` + `ResolveConflictAppService`;移除 `using ISEStudio.Conflicts;`(不再直接 resolve `ConflictService` / `ConflictAgent` / `StructureAgent`);移除 `ReadConflictFilters` private static(搬到 application service) |
| `src/ISEStudio/Conflicts/ConflictService.cs` | 加 `using ISEStudio.Application.Conflicts;`(DTO 命名空间变了) |
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | 同上(DTO 命名空间引用) |
| `src/ISEStudio/Ontology/RdfImportService.cs` | 同上(签名消费 `ConflictOut`) |
| `src/ISEStudio/Conflicts/ConflictServiceCollectionExtensions.cs` | 加 `using ISEStudio.Application.Integration;` / `ISEStudio.Integration;` + 多注册 2 行 |

### 3.2 dispatcher switch arm 改写

原 arm(单行委托替换):

```csharp
"conflicts.list" => InvokeConflictListAsync(request, cancellationToken),
"conflicts.detect" => InvokeConflictDetectAsync(request, cancellationToken),
"conflicts.get_context" => InvokeConflictGetContextAsync(request, cancellationToken),
"conflicts.dismiss" => InvokeConflictDismissAsync(request, cancellationToken),
"conflicts.reopen" => InvokeConflictReopenAsync(request, cancellationToken),
"conflicts.resolve" => InvokeConflictResolveAsync(request, cancellationToken),
"conflicts.list_reconciliations" => InvokeConflictListReconciliationsAsync(request, cancellationToken),
"conflicts.revoke_reconciliation" => InvokeConflictRevokeReconciliationAsync(request, cancellationToken),
"conflicts.edit_reconciliation_reason" => InvokeConflictEditReconciliationReasonAsync(request, cancellationToken),
```

9 个 arm 一字未动,只换了 helper body。

### 3.3 `InvokeConflictAsync` wrapper

```csharp
private Task<object?> InvokeConflictAsync(
    InternalRequest request,
    CancellationToken ct,
    Func<IConflictApplicationService, Task<object?>> call,
    Func<object> onMissing,
    Func<object>? onNull = null)
{
    var app = ResolveConflictAppService();
    if (app is null)
    {
        return Task.FromResult<object?>(onMissing());
    }
    return WrapAsync(async () =>
    {
        var out_ = await call(app).ConfigureAwait(false);
        if (out_ is null)
        {
            return (onNull ?? onMissing)();
        }
        return out_;
    });
}
```

`WrapAsync` 是 dispatcher 既有的 FastApiErrorMiddleware 兼容 wrapper(把异常翻成
HTTP envelope),从 abox pilot 沿用。

## 4. 测试

| 测试 | 结果 |
| --- | --- |
| `dotnet test src/ISEStudio.Tests` | 850 passed / 1 skipped,0 failed(基线 850,改动不动 unit 数) |
| `dotnet test src/ISEStudio.ApiContract.Tests` | 167 passed,0 failed(改动零 wire 偏移) |

无新增单测——abox pilot 同样做法,因为所有 application service 路径已被
850 unit + 167 contract 覆盖(ConflictService 直测 + dispatcher arm wire-shape 直测
+ ApiContract harness 全 op baseline)。

## 5. 决策日志

| 日期 | 决策 |
| --- | --- |
| 2026-08-28 | cross-slice helper 合并:abox 试点时的 `DeserializeBody<T>` / `ExtractIriFromBody` / `QueryString` / `QueryInt` 迁到 `Integration/InternalRequestHelpers.cs`,`ABoxApplicationService` 改 `using static` |
| 2026-08-28 | DTO 路径:`ConflictDtos.cs` 从 `ISEStudio.Conflicts` 搬到 `ISEStudio.Application.Conflicts`,3 个消费方(`ConflictService` / `InternalOperationDispatcher` / `RdfImportService`)各加 `using` |
| 2026-08-28 | orchestrator 模式:detect 三步链提到 `ConflictDetectionOrchestrator`,ctor 用 `IServiceProvider` 而非强类型 agent 注入,保留 contract-test factory null-degrade |
| 2026-08-28 | `edit_reconciliation_reason` 走 inline `{id, reason}` 投影守住 wire shape,不返完整 `ReconciliationOut` |
| 2026-08-28 | `ListReconciliationsAsync` application service 返非 null `ReconciliationListResponse`(empty list payload 内部构造),dispatcher 端不再兜 `EmptyListResponse()`(除非 app service 缺失) |

## 6. 后续 slice 顺位

按 [[ontopilot-dispatcher-split-workflow]] 顺位:conflicts ✅ → **documents (next)** →
releases → vocabulary → ontology → extraction → resolution → history → prompts →
external + published → providers + settings + auth + knowledge + tokens + mcp_tokens → rdf.import。