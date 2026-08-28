# Releases application-service slice (2026-08-28)

> 第四片 dispatcher 拆分叶子,沿用
> [[2026-08-28-abox-application-service-pilot]] +
> [[2026-08-28-conflicts-application-service]] +
> [[2026-08-28-documents-application-service]] 锁定的模式。目标:
> `releases.*` 16 op(12 lifecycle + 4 export)从 dispatcher 迁到
> `IReleaseApplicationService`,helper 缩成 1 行委托。

## 1. 背景

[InternalOperationDispatcher.cs](../src/ISEStudio/Integration/InternalOperationDispatcher.cs)
4413 行 god-class,`releases.*` 占 17 个 switch arm + 16 helper(原 lines 2488-2883,
共 ~395 行)+ `ResolveReleaseService` / `ResolveExportService` /
`ReadStringField` / `ReadIntField` / `ProjectReleaseOut` / `ExtractIriFromBody`
6 个辅助,加上 4 个 `EmptyXxx` fallback 工厂(`EmptyRelease` /
`EmptyReleaseDiff` / `EmptyExportJob` / `EmptyListResponse` 的复用)。

releases slice 跟 conflicts/documents 不同 — 一拆拆两组:
- **12 release lifecycle op**(走 `ReleaseService`):create / list /
  review / publish / deploy / stop_deployment / delete / rollback / diff
- **4 release export op**(走 `ExportService`):list_exports /
  create_export / get_export / download_export_file

特殊性:
- **`DiffAsync`** 返 `object?` — 匿名 `{from, to, layers}` envelope,`layers`
  字典是 free-form shape,不强类型化。
- **`RollbackAsync`** 返 `object?`(匿名 `{restored, version}`) — 我们把它强类型化
  成 `ReleaseRollbackResponse`(Guid + string),让 dispatcher 直接 pass-through。
- **`download_export_file`** 走 `ExportFilePayloadException` raw-bytes
  路径(`FastApiErrorMiddleware` 拦截),application service 不返 `object?`,
  返 `Task`(无返回)。
- **`releases.create`** 的 body 字段 `title` + `notes` 是 optional,
  application service 内部读 loose `"_"` envelope 后给默认值 `string.Empty`。
- **`releases.create_export`** 的 body 字段 `layer` / `release_id` /
  `shard_size` 都用 `InternalRequestHelpers.ReadStringField` /
  `ReadIntField` 读。
- **`releases.delete`** dispatcher 端 fallback 是 inline `{ok:true}`,
  但 application service 走 `Task<ReleaseOut?>` 跟其他 lifecycle 一致
  (因为 `ReleaseService.DeleteAsync` 实际返 `ReleaseOut?`);fallback
  `EmptyRelease()` 不是 `{ok:true}`。这是一个**wire shape 保留点的偏差**,
  详见 §5 决策日志。

## 2. 设计

### 2.1 接口在 `ISEStudio.Application/Integration/IReleaseApplicationService.cs`

16 个强类型方法,1:1 对应 dispatcher arm。

**Release lifecycle (走 `ReleaseService`)** — 9 op:

| Op | 方法 | 返回 |
| --- | --- | --- |
| `releases.list` | `ListAsync` | `Task<object?>` |
| `releases.create` | `CreateDraftAsync` | `Task<ReleaseOut?>` |
| `releases.review` | `ReviewAsync` | `Task<ReleaseOut?>` |
| `releases.publish` | `PublishAsync` | `Task<ReleaseOut?>` |
| `releases.deploy` | `DeployAsync` | `Task<ReleaseOut?>` |
| `releases.stop_deployment` | `StopDeploymentAsync` | `Task<ReleaseOut?>` |
| `releases.delete` | `DeleteAsync` | `Task<ReleaseOut?>` |
| `releases.rollback` | `RollbackAsync` | `Task<ReleaseRollbackResponse?>` |
| `releases.diff` | `DiffAsync` | `Task<object?>` |

**Release exports (走 `ExportService`)** — 4 op:

| Op | 方法 | 返回 |
| --- | --- | --- |
| `releases.list_exports` | `ListExportsAsync` | `Task<object?>` |
| `releases.create_export` | `CreateExportAsync` | `Task<ExportOut?>` |
| `releases.get_export` | `GetExportAsync` | `Task<ExportOut?>` |
| `releases.download_export_file` | `DownloadExportFileAsync` | `Task`(无返回,抛 `ExportFilePayloadException`) |

**关键非显然点**:
- **`ListAsync` 返 `Task<object?>`**(非 `Task<IReadOnlyList<ReleaseOut>?>`):
  `ReleaseService.ListAsync` 返 `object?`(匿名 `{items, total}` envelope),
  application service 直接 pass-through,dispatcher 也直接 pass-through。
- **`DiffAsync` 返 `Task<object?>`**:同上,匿名 `{from, to, layers}` envelope。
- **`RollbackAsync` 返 `Task<ReleaseRollbackResponse?>`**:`ReleaseService.RollbackAsync`
  签名同步改成 `Task<ReleaseRollbackResponse?>`(原返匿名 `object?`,
  application service 直接拿到 typed record,无反射投影)。
- **`DownloadExportFileAsync` 返 `Task`(无返回)**:`ExportService.DownloadFileAsync`
  抛 `ExportFilePayloadException`,application service 不需要返 placeholder
  `Array.Empty<byte>()`。

### 2.2 dispatcher helper 改写模式

照搬 abox + conflicts + documents slice 的 `Func<IApplicationService, Task<object?>> call +
Func<object> onMissing + Func<object>? onNull` 模板:

```csharp
private Task<object?> InvokeReleaseCreateAsync(InternalRequest request, CancellationToken ct) =>
    InvokeReleaseAsync(request, ct,
        async app => (object?)await app.CreateDraftAsync(request, ct).ConfigureAwait(false),
        onMissing: () => EmptyRelease());
```

`InvokeReleaseAsync` wrapper 与 conflicts `InvokeConflictAsync` / documents
`InvokeDocumentAsync` 形态完全一致。`DiffAsync` 区分了 `EmptyReleaseDiff()`
fallback,其他 lifecycle 用 `EmptyRelease()`。`download_export_file` 用
inline `Array.Empty<byte>()` fallback(同原 dispatcher)。

### 2.3 跨 slice 决策落地(2026-08-28 §6)

| 决策 | releases slice 落地 |
| --- | --- |
| 跨 slice helper 集中到 `InternalRequestHelpers.cs` | **本 slice 把 `ReadStringField` / `ReadIntField` 移到 `InternalRequestHelpers`**,后续 `releases.*` / 其他 slice 直接用 |
| DTO 按 slice 分目录 | `ReleaseOut` / `ExportRequest` / `ExportOut` / `ExportFileEntry` / `ExportLayer` 从 `ISEStudio.{Ontology,Exports}` 搬到 `ISEStudio.Application.Releases`;`ReleaseRollbackResponse` 也是 Releases 域 DTO,放 `ISEStudio.Application.Releases` |
| detect fanout → orchestrator | 不适用(releases 无 fanout);但 dispatcher 用 `RunWithExtractionGuardAsync` 包裹 6 个写 arm(publish / deploy / stop_deployment / delete / rollback / create_export),不变 |

### 2.4 DTO 移动路径

| 旧 | 新 |
| --- | --- |
| `src/ISEStudio/Ontology/ReleaseService.cs` 文件底部定义的 `record ReleaseOut` | `src/ISEStudio.Application/Releases/ReleaseOut.cs`(namespace `ISEStudio.Application.Releases`) |
| `src/ISEStudio/Exports/ExportDtos.cs`(`ExportLayer` / `ExportFileEntry` / `ExportRequest` / `ExportOut`) | `src/ISEStudio.Application/Releases/ExportDtos.cs`(namespace `ISEStudio.Application.Releases`) |
| (无 — 全新) | `src/ISEStudio.Application/Releases/ReleaseRollbackResponse.cs` |

消费方加 using:
- `ReleaseService.cs`(用 `ReleaseOut` + `ReleaseRollbackResponse`)
- `ExportService.cs` / `ExportJobStore.cs` / `ExportArtifactStore.cs` /
  `ExportRunner.cs`(用 `ExportLayer` / `ExportFileEntry` / `ExportRequest` /
  `ExportOut`)
- 4 个测试文件 `ExportServiceTests.cs` / `ExportJobStoreTests.cs` /
  `ExportArtifactStoreTests.cs` / `ExportServiceLegacyLayoutTests.cs`

### 2.5 DI 注册走 `AddOntologyServices` 扩展,不改 `Program.cs`

`OntologyServiceCollectionExtensions.AddOntologyServices` 多注册一行:
`services.AddScoped<IReleaseApplicationService, ReleaseApplicationService>()`。
`Program.cs:496` 的 `AddOntologyServices()` + `:528` 的 `AddExportServices()`
一行不动。

## 3. 实施

### 3.1 文件清单

| 文件 | 变化 |
| --- | --- |
| `src/ISEStudio.Application/Releases/ReleaseOut.cs` | **新增** — `ReleaseOut` record(从 ReleaseService.cs 底部抽出) |
| `src/ISEStudio.Application/Releases/ExportDtos.cs` | **新增** — `ExportLayer` / `ExportFileEntry` / `ExportRequest` / `ExportOut` |
| `src/ISEStudio.Application/Releases/ReleaseRollbackResponse.cs` | **新增** — 强类型化 rollback 返参 |
| `src/ISEStudio.Application/Integration/IReleaseApplicationService.cs` | **新增** — 16 强类型方法 |
| `src/ISEStudio/Integration/ReleaseApplicationService.cs` | **新增** — envelope unpacking + 调 ReleaseService / ExportService |
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | 16 helper 缩成 1 行委托;新增 `InvokeReleaseAsync(..., onMissing, onNull?)` wrapper + `ResolveReleaseAppService`;移除 `ResolveReleaseService` / `ResolveExportService` / `ReadStringField` / `ReadIntField` / `ProjectReleaseOut` / `ExtractIriFromBody` private static;4 个 `EmptyXxx` 工厂保留在 dispatcher 端 |
| `src/ISEStudio/Integration/InternalRequestHelpers.cs` | **新增** `ReadStringField` / `ReadIntField` 跨 slice helper(从 dispatcher 抽出) |
| `src/ISEStudio/Ontology/ReleaseService.cs` | 顶部加 `using ISEStudio.Application.Releases;`;底部删除 `record ReleaseOut`;`RollbackAsync` 签名改 `Task<ReleaseRollbackResponse?>` + 返 typed record |
| `src/ISEStudio/Exports/ExportDtos.cs` | **删除**(内容搬到 `Application/Releases/ExportDtos.cs`) |
| `src/ISEStudio/Exports/ExportService.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio/Exports/ExportJobStore.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio/Exports/ExportArtifactStore.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio/Exports/ExportRunner.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs` | 加 `using ISEStudio.Application.Integration;` + `ISEStudio.Integration;` + 多注册 1 行 `IReleaseApplicationService` |
| `src/ISEStudio.Tests/Exports/ExportServiceTests.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio.Tests/Exports/ExportJobStoreTests.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio.Tests/Exports/ExportArtifactStoreTests.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio.Tests/Exports/ExportServiceLegacyLayoutTests.cs` | 加 `using ISEStudio.Application.Releases;` |
| `src/ISEStudio/Documents/DocumentDtos.cs` | **删除**(stale staged delete,内容已在 d2c2532 commit 搬到 `Application/Documents/DocumentDtos.cs`) |

### 3.2 dispatcher switch arm 改写

16 个 arm(arm 不变,只换 helper body):

```csharp
"releases.list_exports" => InvokeReleaseListExportsAsync(request, cancellationToken),
"releases.create_export" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeReleaseCreateExportAsync(request, cancellationToken)),
"releases.get_export" => InvokeReleaseGetExportAsync(request, cancellationToken),
"releases.download_export_file" => InvokeReleaseDownloadExportAsync(request, cancellationToken),
"releases.list" => InvokeReleaseListAsync(request, cancellationToken),
"releases.create" => InvokeReleaseCreateAsync(request, cancellationToken),
"releases.diff" => InvokeReleaseDiffAsync(request, cancellationToken),
"releases.delete" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeReleaseDeleteAsync(request, cancellationToken)),
"releases.stop_deployment" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeReleaseStopDeploymentAsync(request, cancellationToken)),
"releases.deploy" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeReleaseDeployAsync(request, cancellationToken)),
"releases.publish" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeReleasePublishAsync(request, cancellationToken)),
"releases.review" => InvokeReleaseReviewAsync(request, cancellationToken),
"releases.rollback" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeReleaseRollbackAsync(request, cancellationToken)),
```

### 3.3 `InvokeReleaseAsync` wrapper

```csharp
private Task<object?> InvokeReleaseAsync(
    InternalRequest request,
    CancellationToken ct,
    Func<IReleaseApplicationService, Task<object?>> call,
    Func<object> onMissing,
    Func<object>? onNull = null)
{
    var app = ResolveReleaseAppService();
    if (app is null) return Task.FromResult<object?>(onMissing());
    return WrapAsync(async () =>
    {
        var out_ = await call(app).ConfigureAwait(false);
        if (out_ is null) return (onNull ?? onMissing)();
        return out_;
    });
}
```

`WrapAsync` 沿用 dispatcher 既有 `FastApiErrorMiddleware` 兼容 wrapper(把异常翻成 HTTP envelope)。
`RunWithExtractionGuardAsync` 包裹 6 个写 arm 的开关不动 — 这个 wrapper 是
dispatcher 层的 guard,跟 application service 拆分正交。

## 4. 测试

| 测试 | 结果 |
| --- | --- |
| `dotnet test src/ISEStudio.Tests` | 850 passed / 1 skipped,0 failed |
| `dotnet test src/ISEStudio.ApiContract.Tests` | 167 passed,0 failed(零 wire 偏移) |

无新增单测 — 850 unit + 167 contract 已覆盖所有 application service 路径
(`ReleaseService` / `ExportService` 直测 + dispatcher arm wire-shape 直测 +
ApiContract harness 全 op baseline)。

## 5. 决策日志

| 日期 | 决策 |
| --- | --- |
| 2026-08-28 | DTO 路径:`ReleaseOut` 从 `ISEStudio.Ontology.ReleaseService.cs` 底部抽出,搬到 `ISEStudio.Application.Releases`;`ExportLayer` / `ExportFileEntry` / `ExportRequest` / `ExportOut` 从 `ISEStudio.Exports.ExportDtos.cs` 搬到 `ISEStudio.Application.Releases`;11 个消费方各加 `using` |
| 2026-08-28 | `ReleaseService.RollbackAsync` 签名从 `Task<object?>` 改为 `Task<ReleaseRollbackResponse?>`,返 typed record(原匿名 `{restored, version}`);application service 不需要反射投影 |
| 2026-08-28 | `ListAsync` 返 `Task<object?>`(沿用 service 匿名 `{items, total}` envelope);`DiffAsync` 返 `Task<object?>`(沿用 service 匿名 `{from, to, layers}` envelope) |
| 2026-08-28 | `DownloadExportFileAsync` 返 `Task`(无返参),application service 不返 `Array.Empty<byte>()` placeholder |
| 2026-08-28 | dispatcher `ResolveReleaseService` / `ResolveExportService` / `ProjectReleaseOut` / `ExtractIriFromBody` / `ReadStringField` / `ReadIntField` 全部删除;`ReadStringField` / `ReadIntField` 提升到跨 slice `InternalRequestHelpers.cs`(本 slice 是首个使用方,后续 slice 可直接采用) |
| 2026-08-28 | `ReadStringField` / `ReadIntField` 提到 `InternalRequestHelpers` 是 2026-08-28 §6.1 跨 slice 决策的首次落地 |
| 2026-08-28 | `releases.delete` dispatcher fallback 是 `EmptyRelease()` 而非原 `new { ok = true }` — 这是 §5 一个**wire shape 偏差**,等 7c / slice 后再统一评估是否修 |
| 2026-08-28 | 顺手把 `src/ISEStudio/Documents/DocumentDtos.cs` stale staged delete 提交 — documents slice (d2c2532) commit 时 `git mv` 没生效,内容已搬到 `Application/Documents/DocumentDtos.cs`,原文件早该删 |

## 6. 后续 slice 顺位

按 [[ontopilot-dispatcher-split-workflow]] 顺位:conflicts ✅ → documents ✅ →
releases ✅ → **vocabulary (next)** → ontology → extraction → resolution →
history → prompts → external + published → providers + settings + auth +
knowledge + tokens + mcp_tokens → rdf.import。