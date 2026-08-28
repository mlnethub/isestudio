# Documents application-service slice (2026-08-28)

> 第三片 dispatcher 拆分叶子,沿用 [[2026-08-28-abox-application-service-pilot]] +
> [[2026-08-28-conflicts-application-service]] 锁定的模式。目标:`documents.*` 10 op
> 从 dispatcher 迁到 `IDocumentApplicationService`,helper 缩成 1 行委托。
> `documents.upload` 已在 dispatcher arm 里 throw `NotSupportedException`,保持原状
> 不归 application service(走 DocumentsController 直接 multipart)。

## 1. 背景

[InternalOperationDispatcher.cs](src/ISEStudio/Integration/InternalOperationDispatcher.cs) 4413 行 god-class,
`documents.*` 占 11 个 switch arm + 10 个 helper(原 lines 1922-2117,共 ~195 行)
+ `ResolveDocumentService` + `ParseLongOrDefault` + `ParseDocumentId` 3 个辅助。
加上 5 个 `EmptyXxx` fallback 工厂(`EmptyDocument` / `EmptyContribution` /
`EmptyImpact` / `EmptyParseResponse` / `EmptyParseBatchResponse`)。

documents slice 没有像 conflicts.detect 那样的多步 fanout,**最 straightforward 的 slice**:
- 10 helper 全部走单参数 envelope unpacking(KS id + ResourceId + body)
- 2 个 body-required-throw op:`move` + `parse_batch`(service 抛 InvalidOperationException)
- 1 个 inline projection op:`delete`(`{ok:bool}`)
- 1 个 5-query-parameter op:`list_page`(folder / q / status / limit / offset)
- 唯一非标准点:`list_page` 区分 onMissing vs onNull 两种 fallback(同 `items:[], total:0L, folders:[]` envelope)

## 2. 设计

### 2.1 接口在 `ISEStudio.Application/Integration/IDocumentApplicationService.cs`

10 个强类型方法,1:1 对应 dispatcher arm。

| Op | 方法 | 返回 |
| --- | --- | --- |
| `documents.list` | `ListAsync` | `Task<IReadOnlyList<DocumentOut>?>` |
| `documents.list_page` | `ListPageAsync` | `Task<DocumentListResponse?>` |
| `documents.get` | `GetAsync` | `Task<DocumentOut?>` |
| `documents.move` | `MoveAsync` | `Task<DocumentOut?>` |
| `documents.list_chunks` | `ListChunksAsync` | `Task<IReadOnlyList<ChunkOut>?>` |
| `documents.contribution` | `ContributionAsync` | `Task<ContributionOut?>` |
| `documents.impact` | `ImpactAsync` | `Task<ImpactOut?>` |
| `documents.delete` | `DeleteAsync` | `Task<bool?>` |
| `documents.parse` | `ParseAsync` | `Task<ParseResponse?>` |
| `documents.parse_batch` | `ParseBatchAsync` | `Task<ParseBatchResponse>`(非 null) |

**关键非显然点**:
- `DeleteAsync` 返回 `Task<bool?>` 而非 `Task<bool>` —— 跟 `DocumentService.DeleteAsync`
  的非 nullable `bool` 错位,application service 把 null 当 "KS id / resource id 缺失"(→ dispatcher 兜 `{ok:false}`)。
- `ParseBatchAsync` 返回非 null `Task<ParseBatchResponse>` —— `DocumentService.ParseBatchAsync`
  是 nullable,application service 内部 `?? EmptyParseBatchResponse()` 兜底,让 wire shape
  永远非 null。
- `documents.upload` 不在接口里 —— dispatcher arm 仍 throw `NotSupportedException`,
  application service 无 Upload 方法。

### 2.2 dispatcher helper 改写模式

照搬 abox + conflicts slice 的 `Func<IApplicationService, Task<object?>> call +
Func<object> onMissing + Func<object>? onNull` 模板:

```csharp
private Task<object?> InvokeDocumentListPageAsync(InternalRequest request, CancellationToken ct) =>
    InvokeDocumentAsync(request, ct,
        async app => (object?)await app.ListPageAsync(request, ct).ConfigureAwait(false),
        onMissing: () => EmptyDocumentListPage,
        onNull: () => EmptyDocumentListPage);
```

wrapper 与 conflicts slice `InvokeConflictAsync` 形态完全一致。`list_page` 是唯一用 onNull
的 documents op,其他 9 个只用 onMissing。

`delete` op 需要 inline projection 到 `{ok:bool}` shape:

```csharp
private Task<object?> InvokeDocumentDeleteAsync(InternalRequest request, CancellationToken ct) =>
    InvokeDocumentAsync(request, ct,
        async app =>
        {
            var ok = await app.DeleteAsync(request, ct).ConfigureAwait(false);
            return (object?)new { ok };
        },
        onMissing: () => new { ok = false });
```

### 2.3 跨 slice 决策落地(2026-08-28 §6)

| 决策 | documents slice 落地 |
| --- | --- |
| 跨 slice helper 集中到 `InternalRequestHelpers.cs` | 直接用,无需新 helper(`DeserializeBody<T>` / `QueryString` / `QueryInt` 已存在) |
| DTO 按 slice 分目录 | `DocumentDtos.cs` 从 `ISEStudio.Documents` 搬到 `ISEStudio.Application.Documents`(namespace `ISEStudio.Documents` → `ISEStudio.Application.Documents`) |
| detect fanout → orchestrator | 不适用(documents 没有 fanout) |

### 2.4 DI 注册走 `AddDocumentServices` 扩展,不改 `Program.cs`

`DocumentServiceCollectionExtensions.AddDocumentServices` 多注册一行:
`services.AddScoped<IDocumentApplicationService, DocumentApplicationService>()`。
`Program.cs:452` 的 `AddDocumentServices()` 一行不动。

## 3. 实施

### 3.1 文件清单

| 文件 | 变化 |
| --- | --- |
| `src/ISEStudio.Application/Documents/DocumentDtos.cs` | **移动** from `src/ISEStudio/Documents/DocumentDtos.cs` + namespace 改 |
| `src/ISEStudio.Application/Integration/IDocumentApplicationService.cs` | **新增** —— 10 强类型方法 |
| `src/ISEStudio/Integration/DocumentApplicationService.cs` | **新增** —— envelope unpacking + 调 DocumentService |
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | 10 helper 缩成 1 行委托;新增 `InvokeDocumentAsync(..., onMissing, onNull?)` wrapper + `ResolveDocumentAppService`;移除 `ResolveDocumentService` / `ParseLongOrDefault` / `ParseDocumentId` private static;新增 `static readonly EmptyDocumentListPage` 工厂 |
| `src/ISEStudio/Documents/DocumentService.cs` | 加 `using ISEStudio.Application.Documents;`(DTO namespace 改) |
| `src/ISEStudio/Documents/DocumentServiceCollectionExtensions.cs` | 加 `using ISEStudio.Application.Integration;` / `ISEStudio.Integration;` + 多注册 1 行 |

### 3.2 dispatcher switch arm 改写

原 11 个 arm(arm 不变,只换 helper body):

```csharp
"documents.list" => InvokeDocumentListAsync(request, cancellationToken),
"documents.list_page" => InvokeDocumentListPageAsync(request, cancellationToken),
"documents.upload" => throw new NotSupportedException(...),  // 不变
"documents.parse_batch" => InvokeDocumentParseBatchAsync(request, cancellationToken),
"documents.get" => InvokeDocumentGetAsync(request, cancellationToken),
"documents.move" => InvokeDocumentMoveAsync(request, cancellationToken),
"documents.list_chunks" => InvokeDocumentListChunksAsync(request, cancellationToken),
"documents.contribution" => InvokeDocumentContributionAsync(request, cancellationToken),
"documents.delete" => InvokeDocumentDeleteAsync(request, cancellationToken),
"documents.impact" => InvokeDocumentImpactAsync(request, cancellationToken),
"documents.parse" => InvokeDocumentParseAsync(request, cancellationToken),
```

### 3.3 `InvokeDocumentAsync` wrapper

```csharp
private Task<object?> InvokeDocumentAsync(
    InternalRequest request,
    CancellationToken ct,
    Func<IDocumentApplicationService, Task<object?>> call,
    Func<object> onMissing,
    Func<object>? onNull = null)
{
    var app = ResolveDocumentAppService();
    if (app is null) return Task.FromResult<object?>(onMissing());
    return WrapAsync(async () =>
    {
        var out_ = await call(app).ConfigureAwait(false);
        if (out_ is null) return (onNull ?? onMissing)();
        return out_;
    });
}
```

`WrapAsync` 沿用 dispatcher 既有 FastApiErrorMiddleware 兼容 wrapper(把异常翻成 HTTP envelope)。

## 4. 测试

| 测试 | 结果 |
| --- | --- |
| `dotnet test src/ISEStudio.Tests` | 850 passed / 1 skipped,0 failed |
| `dotnet test src/ISEStudio.ApiContract.Tests` | 167 passed,0 failed(零 wire 偏移) |

**注意**:完整 unit suite 偶发 1-3 个 `EndpointRoleMatrixTests` 失败,模式是 ExtractionJobStore
持久化残留导致后续 extraction/prompts 测试被 `RunWithExtractionGuardAsync` 拒绝 409。
re-run 即恢复 850/850 全绿;baseline(conflicts commit) 也偶发同样 flake。这是
**pre-existing xUnit test isolation bug**,**非 documents slice 引入**。

无新增单测——850 unit + 167 contract 已覆盖所有 application service 路径
(DocumentService 直测 + dispatcher arm wire-shape 直测 + ApiContract harness 全 op baseline)。

## 5. 决策日志

| 日期 | 决策 |
| --- | --- |
| 2026-08-28 | DTO 路径:`DocumentDtos.cs` 从 `ISEStudio.Documents` 搬到 `ISEStudio.Application.Documents`,2 个消费方(`DocumentService.cs` + `InternalOperationDispatcher.cs`)各加 `using` |
| 2026-08-28 | `DeleteAsync` 签名返 `Task<bool?>`(非 nullable `bool` → nullable 转换在 application service),dispatcher 端 inline `{ok:bool}` 投影守 wire shape |
| 2026-08-28 | `ParseBatchAsync` 签名返非 null `Task<ParseBatchResponse>`,application service 内部 `?? EmptyParseBatchResponse()` 兜底,让 wire shape 永远非 null |
| 2026-08-28 | `documents.upload` 不归 application service(已有 dispatcher `NotSupportedException` 守卫) |
| 2026-08-28 | `EmptyDocumentListPage` 提到 dispatcher 端 `static readonly` 字段(避免每次 fallback 重新分配匿名对象),通过 `() => EmptyDocumentListPage` 包装喂给 wrapper |

## 6. 后续 slice 顺位

按 [[ontopilot-dispatcher-split-workflow]] 顺位:conflicts ✅ → documents ✅ →
**releases (next)** → vocabulary → ontology → extraction → resolution → history →
prompts → external + published → providers + settings + auth + knowledge + tokens +
mcp_tokens → rdf.import。