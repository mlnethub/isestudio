# History 应用服务抽取 + dispatcher → application-service 拆分(9/13)

**状态**: 已完成(9/13 slice 落地,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `dotnet`
**范围**: 2 个 dispatcher arms:
- `history.get` (read)
- `history.rollback` (mutation + `RunWithExtractionGuardAsync` 409 守卫)

从 `InternalOperationDispatcher` god-class 拆出一个
`IHistoryApplicationService`(定义在 `ISEStudio.Application.Integration`,
实现在 `ISEStudio.Integration`)。把 3 个 DTO 从 `ISEStudio.Ontology`
搬到 `ISEStudio.Application.History`,并把 `RollbackResponseOut` 从
`object? View` / `object? OpenConflicts` 升级为 typed
`OntologyResponse? View` / `IReadOnlyList<ConflictOut>? OpenConflicts`。

接续 [2026-08-28-resolution-application-service.md](2026-08-28-resolution-application-service.md)
8/13 slice,本切片验证模板在「1 read + 1 mutation + 简单 typed DTO +
typed 多 DTO 升级」组合下的可用性。**本切片同时解锁 ontology slice
保留的 `ResolveOntologyService` shim**(无 typed facade 调用
`HistoryService`,所以 `ResolveHistoryService` 完全清理)。

---

## 1. 背景

`InternalOperationDispatcher` 在 8/13 切片后 ~3332 行,其中 history helpers
占 ~34 行(原 lines 762-795 的 1 个 DI helper + 2 个 helper),承载:

- 1 个 read 端点:`history.get`(用 `category` / `q` / `limit` / `offset`
  query 解析)
- 1 个 mutation 端点:`history.rollback`(用 `RunWithExtractionGuardAsync`
  守卫 + 解析 `request.ResourceId` → eventId Guid)

3 个 DTO 住在 `ISEStudio.Ontology`:`HistoryItemOut`(7 字段 +
`JsonElement? Detail`)/ `HistoryResponseOut`(2 字段)/ `RollbackResponseOut`(3 字段)。

## 2. 决策

### 2.1 DTO 搬入 `ISEStudio.Application.History` + RollbackResponseOut 升级为 typed

**结论**:搬。命名空间 `ISEStudio.Application.History`。

**实现细节**:
- 新增 `ISEStudio.Application/History/HistoryDtos.cs`(3 records,40 行)
- 删 `ISEStudio/Ontology/HistoryDtos.cs`
- 改 `HistoryService.cs` 加 `using ISEStudio.Application.History;` +
  `using ISEStudio.Application.Conflicts;`
- **`RollbackResponseOut` 升级**:从 `(int Undone, object? View, object? OpenConflicts)`
  升级为 `(int Undone, OntologyResponse? View, IReadOnlyList<ConflictOut>? OpenConflicts)`。
  - `View` 来自 `_ontology.GetViewAsync(ksId, actor, ct)` 返回
    `OntologyResponse?`(`ISEStudio.Application.Foundation`)
  - `OpenConflicts` 来自 `_conflicts.SyncAfterOntologyMutationAsync(ksId, ...)` 返回
    `IReadOnlyList<ConflictOut>`(`ISEStudio.Application.Conflicts`),
    默认 `Array.Empty<ConflictOut>()`
  - wire shape 不变(JSON 序列化时 typed DTO 字段名一致)

### 2.2 应用服务接口 = 2 个 typed 方法

**结论**:`Task<T?>(InternalRequest, CancellationToken)` 签名,2 个方法:

```csharp
Task<HistoryResponseOut?> ListAsync(InternalRequest, CancellationToken);
Task<RollbackResponseOut?>  RollbackAsync(InternalRequest, CancellationToken);
```

### 2.3 dispatcher arm 不动,2 个 helper 全部 1 行委托

**结论**:2 个 `InvokeHistory*Async` helper 都缩成 1 行委托。

**实现细节**:
- 新增 `ResolveHistoryAppService()` 1 行 + `InvokeHistoryAsync` shared wrapper
- 2 个 helper 都通过 wrapper,每个 1 行委托
- 重命名:`InvokeHistoryGetAsync`(原 dispatcher 私有 helper)→
  `InvokeHistoryListAsync`(对齐 `ListAsync` 方法名)

### 2.4 守卫包装 (`RunWithExtractionGuardAsync`) 留在 dispatcher arm 上(沿用 8/13 §2.4)

`history.rollback` 在 dispatcher switch arm 层仍然 wrap
`RunWithExtractionGuardAsync`:

```csharp
"history.rollback" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeHistoryRollbackAsync(request, cancellationToken)),
```

应用服务不实现 extraction guard。

### 2.5 dispatcher 跨 slice shim: 无

**结论**:`ResolveHistoryService` 完全删掉,没有留 shim。

**理由**:
- 没有 typed facade 绕过 dispatcher 调用 `HistoryService`(确认
  grep `IIntegrationApiFacade` + `HistoryService` 无匹配)。
- 2 个 arm 都通过 dispatcher,折叠到 app service 内部直接
  `_history.ListHistoryAsync(...)` / `_history.RollbackAsync(...)`。

### 2.6 query tuple 解析复用 `InternalRequestHelpers.QueryString`

**结论**:用 `InternalRequestHelpers.QueryString(request, "category")` /
`QueryString(request, "q")` 等,不复制 dispatcher 私有 helper。

**理由**:
- `InternalRequestHelpers` 已经有 `QueryString(request, key)` 公共 helper。
- 不像 8/13 resolution slice 需要私有 `ReadResolutionPaging` tuple
  helper — history 单字段读取更简单。
- 沿用 4/13 releases slice 的 `InternalRequestHelpers` 复用模式。

### 2.7 `Guid.TryParse` + `KeyNotFoundException` 404 保留

**结论**:application service 内部 `Guid.TryParse(request.ResourceId, ...)`,
失败抛 `KeyNotFoundException("History event not found")`(→ HTTP 404 via
`FastApiErrorMiddleware`)。

**理由**:
- 保留原 dispatcher 行为(malformed eventId → KeyNotFoundException)。
- app service 自己抛,不污染 dispatcher。

## 3. 文件清单

### 新增

| 文件 | 行 | 说明 |
|------|----|----|
| `src/ISEStudio.Application/History/HistoryDtos.cs` | 40 | 3 records |
| `src/ISEStudio.Application/Integration/IHistoryApplicationService.cs` | 50 | 2-method 接口 |
| `src/ISEStudio/Integration/HistoryApplicationService.cs` | 75 | 2 methods |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | -34 +66 行(history section 762-795 + switch arm 重写) |
| `src/ISEStudio/Ontology/HistoryService.cs` | +2 行(using) + typed RollbackResponseOut |
| `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs` | +6 行(DI 注册) |

### 删除

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Ontology/HistoryDtos.cs` | 完全删除(3 records 搬到 Application) |

### dispatcher 行数

- 前:3332 行(8/13 后)
- 后:3371 行(9/13 后)
- 净增加 **39 行**(注释说明 ~30 行,helper 缩成 1 行委托 ~10 行)
- *注:净增加主要来自详细 block 注释。后续切片可适当精简注释*

## 4. 验证

```
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851

$ dotnet test src/ISEStudio.ApiContract.Tests/...
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167
```

零回归;`RunWithExtractionGuardAsync` 守卫保持 409 + job_id envelope 行为,
`EmptyListResponse` / `EmptyKnowledgeSystem` fallback envelopes 全部保留,
wire shape 完全不变(`RollbackResponseOut` 升级 typed 后字段名 + JSON
序列化保持一致)。

---

## 5. 后续切片(剩 4)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 10/13 prompts
- [ ] 11/13 external + published (free `ResolveExternalOntologyService` + `ParseExportFormat` shim)
- [ ] 12/13 providers + settings + auth + knowledge + tokens + mcp_tokens
- [ ] 13/13 rdf.import

每个切片都会复用本切片定下的 4 段模式:
1. DTO 搬入 `ISEStudio.Application.{History,...}`
2. `IXxxApplicationService`: `Task<T?>(InternalRequest, CancellationToken)`
3. dispatcher arm 不动,helper 缩成 1 行委托
4. 守卫包装留在 arm 上,不沉到 app service

---

## 6. Decision Log

- 2026-08-28: 9/13 history slice 完成。
  本切片锁定 2-arms(1 read + 1 mutation)+ `RunWithExtractionGuardAsync`
  守卫 + typed DTO 升级(`object? View` / `object? OpenConflicts` →
  `OntologyResponse?` / `IReadOnlyList<ConflictOut>?`)的拆分模式。
  3 个 plain record DTO 搬 Application,无 Infrastructure 依赖。
  `ResolveHistoryService` 完全清理(无 typed facade 引用)。
  `InternalRequestHelpers.QueryString` 复用,无私有 paging helper 复制。
  net dispatcher +39 行(注释膨胀);后续切片可精简注释。