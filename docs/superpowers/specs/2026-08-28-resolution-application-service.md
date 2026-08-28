# Resolution 应用服务抽取 + dispatcher → application-service 拆分(8/13)

**状态**: 已完成(8/13 slice 落地,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `dotnet`
**范围**: 5 个 dispatcher arms:
- 2 个 reads: `resolution.get_queue` / `resolution.list_decisions`
- 3 个 mutations: `resolution.resolve` / `resolution.revoke_decision` / `resolution.edit_decision_reason`

从 `InternalOperationDispatcher` god-class 拆出一个
`IResolutionApplicationService`(定义在 `ISEStudio.Application.Integration`,
实现在 `ISEStudio.Integration`)。把 7 个 DTO(5 个 output +
2 个 input)从 `ISEStudio.EntityResolution` 搬到
`ISEStudio.Application.Resolution`。

接续 [2026-08-28-extraction-application-service.md](2026-08-28-extraction-application-service.md)
7/13 slice,本切片验证模板在「2 read + 3 mutation + `RunWithExtractionGuardAsync`
409 守卫」组合下的可用性,以及 7 个 plain records DTO 全搬 Application
的场景(因为所有 7 个 DTO 都无 Infrastructure 依赖)。

---

## 1. 背景

`InternalOperationDispatcher` 在 7/13 切片后 ~3360 行,其中 resolution helpers
占 ~124 行(原 lines 869-992 的多个 helpers,加上 1 个 DI helper +
1 个 paging helper),承载:

- 2 个 read 端点:`resolution.get_queue` / `resolution.list_decisions`
- 3 个 mutation 端点:`resolution.resolve` / `resolution.revoke_decision`
  / `resolution.edit_decision_reason`(都通过 `RunWithExtractionGuardAsync` 守卫 +
  `ResolveResRowGuidAsync` 解析 resource_id → Guid PK)
- 共享逻辑:`ReadResolutionPaging`(`q` / `limit` / `offset` 解析) +
  `ResolveResolutionService`(DI 解析) + `ResolveResRowGuidAsync` 静态调用

7 个 DTO 住在 `ISEStudio.EntityResolution`:`ResolutionCandidateOut` /
`ResolutionQueueItemOut` / `ResolutionDecisionOut` /
`ResolutionQueueEnvelope` / `ResolutionDecisionsEnvelope` /
`ResolutionResolveIn` / `ResolutionEditReasonIn`。

## 2. 决策

### 2.1 DTO 全部搬入 `ISEStudio.Application.Resolution`

**结论**:搬。命名空间 `ISEStudio.Application.Resolution`。

**实现细节**:
- 新增 `ISEStudio.Application/Resolution/ResolutionDtos.cs`(7 records,83 行)
- 删 `ISEStudio/EntityResolution/ResolutionDtos.cs`
- 改 `ResolutionService.cs` + `InternalOperationDispatcher.cs` 加
  `using ISEStudio.Application.Resolution;`
- 7 个 DTO 都是 plain records(无 Infrastructure 依赖),全部可搬

### 2.2 应用服务接口 = 5 个 typed 方法

**结论**:`Task<T?>(InternalRequest, CancellationToken)` 签名,5 个方法:

```csharp
Task<ResolutionQueueEnvelope?>     ListQueueAsync(InternalRequest, CancellationToken);
Task<ResolutionDecisionsEnvelope?> ListDecisionsAsync(InternalRequest, CancellationToken);
Task<ResolutionDecisionOut?>       ResolveAsync(InternalRequest, CancellationToken);
Task<Guid?>                        RevokeDecisionAsync(InternalRequest, CancellationToken);
Task<ResolutionDecisionOut?>       EditDecisionReasonAsync(InternalRequest, CancellationToken);
```

`RevokeDecisionAsync` 返回 `Task<Guid?>`(不是 `bool?`):
- 成功:返 rowId Guid PK(Phase 3 legacy_id 已退役)
- 失败(RevokeAsync 返 false / rowId 未找到):返 null
- dispatcher 投影:`Guid` → `{revoked: guid.ToString()}` / `null` → `{revoked: 0}`

### 2.3 dispatcher arm 不动,5 个 helper 全部 1 行委托

**结论**:5 个 `InvokeResolution*Async` helper 都缩成 1 行委托。

**实现细节**:
- 新增 `ResolveResolutionAppService()` 1 行 + `InvokeResolutionAsync`
  shared wrapper(`Func<IResolutionApplicationService, Task<object?>> call`,
  `Func<object> onMissing`, `Func<object>? onNull = null`)。
- 5 个 helper 都通过 wrapper,每个 1 行委托。
- 重命名:`InvokeResolutionGetQueueAsync`(原 dispatcher 私有 helper)→
  `InvokeResolutionListQueueAsync`(对齐 `ListQueueAsync` 方法名)。

### 2.4 守卫包装 (`RunWithExtractionGuardAsync`) 留在 dispatcher arm 上(沿用 7/13 §2.4)

3 个 mutation arms 在 dispatcher switch arm 层仍然 wrap `RunWithExtractionGuardAsync`:

```csharp
"resolution.resolve" => RunWithExtractionGuardAsync(request, cancellationToken,
    () => InvokeResolutionResolveAsync(request, cancellationToken)),
```

应用服务不实现 extraction guard — 守卫属于 transport-level concern。

### 2.5 dispatcher 跨 slice shim: 无

**结论**:`ResolveResolutionService` 完全删掉,没有留 shim。

**理由**:
- 没有 typed facade 绕过 dispatcher 调用 `ResolutionService`(确认
  grep `IIntegrationApiFacade` + `ResolutionService` 无匹配)。
- 所有 5 个 arm 都通过 dispatcher,折叠到 app service 内部直接
  `_resolution.ListQueueAsync(...)` 等,不再需要 dispatcher helper。

### 2.6 `ReadResolutionPaging` 搬到 app service 私有方法

**结论**:搬到 `ResolutionApplicationService.ReadResolutionPaging` 私有方法。

**理由**:
- 原 dispatcher 私有静态 helper(15 行),只在 resolution slice 用。
- application service 私有方法(15 行),完全 1:1 复制。
- 沿用 7/13 §2.7 extraction slice 的私有 helper 复制模式(不污染
  `InternalRequestHelpers`)。

### 2.7 `ResolveResRowGuidAsync` 静态调用搬到 app service

**结论**:保留 `ResolutionService.ResolveResRowGuidAsync` 静态方法,
3 个 mutation arms 通过 `ResolutionService.ResolveResRowGuidAsync(...)` 调用。

**理由**:
- 静态方法不在 DI 范畴,application service 直接通过类型名调用。
- 解析 resource_id → Guid PK 的逻辑保持单点实现,避免重复。

### 2.8 `DeserializeBody<T>` 私有 helper 搬到 app service

**结论**:搬到 `ResolutionApplicationService.DeserializeBody<T>` 私有方法。

**理由**:
- 同 7/13 §2.7 extraction slice:需要 snake_case + case-insensitive 规则
  (frontend body 是 `{action, individual_iri}` snake_case),8 行私有
  copy,不影响其他 slice。

## 3. 文件清单

### 新增

| 文件 | 行 | 说明 |
|------|----|----|
| `src/ISEStudio.Application/Resolution/ResolutionDtos.cs` | 83 | 7 records |
| `src/ISEStudio.Application/Integration/IResolutionApplicationService.cs` | 75 | 5-method 接口 |
| `src/ISEStudio/Integration/ResolutionApplicationService.cs` | 175 | 5 methods + 2 私有 helper |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | -124 +96 行(resolution section 869-992 + switch arm 重写) |
| `src/ISEStudio/EntityResolution/ResolutionService.cs` | +1 行(using) |
| `src/ISEStudio/EntityResolution/ResolutionServiceCollectionExtensions.cs` | -1 +10 行(DI 重写) |

### 删除

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/EntityResolution/ResolutionDtos.cs` | 完全删除(7 records 搬到 Application) |

### dispatcher 行数

- 前:3360 行(7/13 后)
- 后:3332 行(8/13 后)
- 净减少 **28 行**

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
`EmptyListResponse` / `EmptyResolutionDecision` / `{revoked: 0}` fallback
envelopes 全部保留,wire shape 完全不变。

---

## 5. 后续切片(剩 5)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 9/13 history (free `ResolveOntologyService` shim)
- [ ] 10/13 prompts
- [ ] 11/13 external + published (free `ResolveExternalOntologyService` + `ParseExportFormat` shim)
- [ ] 12/13 providers + settings + auth + knowledge + tokens + mcp_tokens
- [ ] 13/13 rdf.import

每个切片都会复用本切片定下的 4 段模式:
1. DTO 搬入 `ISEStudio.Application.{Resolution,...}`
2. `IXxxApplicationService`: `Task<T?>(InternalRequest, CancellationToken)`
3. dispatcher arm 不动,helper 缩成 1 行委托
4. 守卫包装留在 arm 上,不沉到 app service

---

## 6. Decision Log

- 2026-08-28: 8/13 resolution slice 完成。
  本切片锁定 5-arms(2 read + 3 mutation)+ `RunWithExtractionGuardAsync`
  守卫 + 静态 `ResolveResRowGuidAsync` + 私有 paging helper 复制的拆分模式。
  7 个 plain records DTO 全部搬 Application(无 Infrastructure 依赖)。
  `RevokeDecisionAsync` 返回 `Task<Guid?>` 而非 `Task<bool?>` — 暴露 rowId
  Guid PK 到 contract layer 让 dispatcher 投影 `{revoked: guid.ToString()}`,
  失败 + 未找到合并为 null → `{revoked: 0}`。
  无 dispatcher shim 残留(`ResolveResolutionService` 完全清理,无 typed
  facade 引用 `ResolutionService`)。