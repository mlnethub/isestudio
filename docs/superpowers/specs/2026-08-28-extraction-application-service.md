# Extraction 应用服务抽取 + dispatcher → application-service 拆分(7/13)

**状态**: 已完成(7/13 slice 落地,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `dotnet`
**范围**: 5 个 dispatcher arms:
- 3 个 `extraction.run*`(`extraction.run` / `extraction.run_combined` / `extraction.run_instances`)
- 2 个 reads(`extraction.list_jobs` / `extraction.get_job`)

从 `InternalOperationDispatcher` god-class 拆出一个
`IExtractionApplicationService`(定义在 `ISEStudio.Application.Integration`,
实现在 `ISEStudio.Integration`)。沿用 abox / conflicts / documents /
releases / vocabulary / ontology 切片锁定的 4 段模式,验证 dispatcher
的 **mutation + 守卫路径** 在所有 5 arms 上(3 个 run* + 2 个 read)都能
干净地折叠到 application service。

接续 [2026-08-28-ontology-application-service.md](2026-08-28-ontology-application-service.md)
6/13 slice,本切片验证模板在「3 个 mutation 共享 runKind + 2 个 read +
`RunWithExtractionGuardAsync` 409 守卫」组合下的可用性。

---

## 1. 背景

`InternalOperationDispatcher` 在 6/13 切片后 ~3412 行,其中 extraction helpers
占 ~164 行(原 lines 1957-2113 的多个 helpers,加上 3 个 DI helper +
shared body + `FrontendExtractionRequest` + `BuildFrontendExtractionRequestAsync`),
承载:

- 3 个 mutation 端点:`extraction.run` / `extraction.run_combined` /
  `extraction.run_instances`(都通过 `RunWithExtractionGuardAsync` 守卫 +
  同一个 `InvokeExtractionAsync` shared body)
- 2 个 read 端点:`extraction.list_jobs` / `extraction.get_job`
- 共享逻辑:`FrontendExtractionRequest` 反序列化 +
  `BuildFrontendExtractionRequestAsync`(frontend-flavoured 请求 shape,
  `<already-read>` sentinel 标记 blob 已 read)

注意 `documents.*` 不属于本切片(3/13 documents 已完成),
`ontology.*` 不属于本切片(6/13 ontology 已完成)。

## 2. 决策

### 2.1 DTO `ExtractionJobOut` 留在 `ISEStudio.Extraction`(不走 Application)

**结论**:**不搬**。命名空间保持 `ISEStudio.Extraction`。

**理由**:
- `ExtractionJobOut.From(ExtractionJobEntity)` 直接投影 `ISEStudio.Infrastructure.Persistence.Entities.ExtractionJobEntity`。
- `ISEStudio.Application` 是 zero-`<ProjectReference>` contracts 项目,
  搬 `ExtractionJobOut` 会强制引入 `ISEStudio.Infrastructure` 引用。
- vocabulary / ontology DTO 都是 plain records,无 Infrastructure 依赖,所以
  都能搬 Application;extraction 不行。

**实现细节**:
- 接口 `IExtractionApplicationService` 的方法返回类型全部是
  `Task<object?>`(vocabulary slice 已经为此建立了先例 — 16 个方法中
  11 个用 `object?` 返回 `TerminologyResult` / `{items,total}` /
  `{deleted,removed_triples}` envelope)。application service 实现里
  通过 `_jobs.GetAsync(...)` / `ExtractionJobOut.From(entity)` 投影,
  dispatcher 直接序列化 `object?`。
- `ExtractionJobOut` 类的所有字段保持现有 `[JsonPropertyName("xxx")]`
  snake_case 显式标识,wire shape 不变。

### 2.2 应用服务接口 = 3 个方法(`object?` 返回)

**结论**:3 个方法,统一 `Task<object?>` 返回。

```csharp
public interface IExtractionApplicationService
{
    Task<object?> RunAsync(InternalRequest request, string runKind, CancellationToken cancellationToken);
    Task<object?> ListJobsAsync(InternalRequest request, CancellationToken cancellationToken);
    Task<object?> GetJobAsync(InternalRequest request, CancellationToken cancellationToken);
}
```

**理由**:
- `RunAsync(..., string runKind, CancellationToken)` 共享 3 个 run* arms;
  `runKind` 选 `extraction.run` → `StartTBoxAsync` /
  `extraction.run_combined` → `StartCombinedAsync` /
  `extraction.run_instances` → `StartABoxAsync`。
- `ListJobsAsync` 返回 `List<ExtractionJobOut>`(隐式装箱为 `object`),
  dispatcher 在 wrapper 层 `??` 退到 `Array.Empty<object>()`。
- `GetJobAsync` 返回单个 `ExtractionJobOut`,wrapper 层 `??` 退到
  `EmptyExtractionJob()`(匹配 Python 404 envelope)。

### 2.3 dispatcher arm 不动,只缩 helper(沿用 6/13 §2.3)

**结论**:5 个 `InvokeExtraction*Async` helper 都缩成 1 行委托。

**实现细节**:
- 新增 `ResolveExtractionAppService()` 1 行 + `InvokeExtractionAsync`
  shared wrapper(`Func<IExtractionApplicationService, Task<object?>> call`,
  `Func<object> onMissing`, `Func<object>? onNull = null`)。
- 5 个 helper:`InvokeExtractionRunAsync` / `InvokeExtractionListJobsAsync` /
  `InvokeExtractionGetJobAsync` 都通过 wrapper,每个 1 行委托。
- 重命名:`InvokeExtractionAsync`(原 dispatcher 私有 helper,直接
  调 `ExtractionOrchestrator`)→ `InvokeExtractionRunAsync`(新 wrapper
  委托,调 `app.RunAsync`)。wrapper 本身复用 `InvokeExtractionAsync`
  这个名字(沿用 ontology / vocabulary slice 的命名)。

### 2.4 守卫包装 (`RunWithExtractionGuardAsync`) 留在 dispatcher arm 上(沿用 6/13 §2.4)

3 个 `extraction.run*` arms 在 dispatcher switch arm 层仍然 wrap
`RunWithExtractionGuardAsync`:

```csharp
"extraction.run" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => InvokeExtractionRunAsync(request, "extraction.run", cancellationToken)),
```

应用服务不实现 extraction guard — 守卫属于 transport-level concern。

### 2.5 dispatcher 跨 slice shim: 无

**结论**:`ResolveExtractionJobs` + `ResolveExtractionOrchestrator` 完全删掉,
没有留 shim。

**理由**:
- `ResolveExtractionJobs` 只被 dispatcher 内部 `InvokeExtractionListJobsAsync`
  / `InvokeExtractionGetJobAsync` 使用,现在折叠到 app service 内部直接
  `_jobs.GetAsync(...)`,不再需要 dispatcher helper。
- `ResolveExtractionOrchestrator` 只被 `InvokeExtractionAsync` 使用,
  现在折叠到 app service 内部直接 `_orchestrator.Start*Async(...)`。

### 2.6 `BuildFrontendExtractionRequestAsync` + `FrontendExtractionRequest` 搬到 app service

**结论**:搬到 `ExtractionApplicationService` 私有方法 + 私有 inner record。

- 原 dispatcher helper 通过 `_services.GetRequiredService<ISEStudioDbContext>()`
  取 DbContext 查 KS / systemConfig / provider / chunks。
- app service 构造函数注入 `IServiceProvider`(与 ontology / vocabulary
  slice 一致),内部同样调 `GetRequiredService<ISEStudioDbContext>()`。
- Scoped 注册:`ExtractionApplicationService` 注入 `ExtractionJobStore`(singleton)
  + `ExtractionOrchestrator`(singleton) + `IServiceProvider`,所以 app
  service 自身 Scoped,与 dispatcher / 其他 app service 一致。

### 2.7 `DeserializeBody<T>` 私有 helper 搬到 app service

**结论**:搬到 `ExtractionApplicationService.DeserializeBody<T>` 私有方法。

**理由**:
- dispatcher 的 `DeserializeBody<T>` 私有 helper(line 1350)有多个 caller
  在 dispatch 各个 slice,但 app service 想用同样的 snake_case + case-insensitive
  规则,因 frontend body 是 `{chunk_ids, model}`(snake_case)而 csharp property 是
  `ChunkIds` / `Model`。
- 直接搬到 app service 私有方法(8 行),不污染 dispatcher private 区域;
  dispatcher 的全局 `DeserializeBody<T>` 保持不变继续服务其他 slice。

## 3. 文件清单

### 新增

| 文件 | 行 | 说明 |
|------|----|----|
| `src/ISEStudio.Application/Integration/IExtractionApplicationService.cs` | 80 | 3-method 接口 |
| `src/ISEStudio/Integration/ExtractionApplicationService.cs` | 220 | 3 methods + 3 私有 helper(`BuildFrontendExtractionRequestAsync` + `FrontendExtractionRequest` + `DeserializeBody<T>`) |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | -164 +82 行(extraction section 1950-2113 + switch arm comments 重写) |
| `src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs` | +2 行 using, +6 行 DI 注册 |

### 删除

无。

### dispatcher 行数

- 前:3412 行(6/13 后)
- 后:3361 行(7/13 后)
- 净减少 **51 行**(switch arm comments 0 增 / 减,extraction section -82,
  switch arm 注释重写 -1)

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
`ExtractionJobOut` wire shape 完全不变。

---

## 5. 后续切片(剩 6)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 8/13 resolution
- [ ] 9/13 history
- [ ] 10/13 prompts
- [ ] 11/13 external + published(free `ResolveExternalOntologyService` + `ParseExportFormat` shim)
- [ ] 12/13 providers + settings + auth + knowledge + tokens + mcp_tokens
- [ ] 13/13 rdf.import

每个切片都会复用本切片定下的 4 段模式:
1. 接口定义在 `ISEStudio.Application.Integration`
2. `IXxxApplicationService`: `Task<T?>(InternalRequest, CancellationToken)`
3. dispatcher arm 不动,helper 缩成 1 行委托
4. 守卫包装留在 arm 上,不沉到 app service

---

## 6. Decision Log

- 2026-08-28: 7/13 extraction slice 完成。
  本切片锁定 5-arms(3 mutation + 2 read)+ `RunWithExtractionGuardAsync`
  守卫 + frontend-flavoured body 反序列化的拆分模式。
  3 个方法 `object?` 返回,沿用 vocabulary slice 既有先例。
  `ExtractionJobOut` 因依赖 `ExtractionJobEntity` 留在 `ISEStudio.Extraction`
  不搬 Application。
  5 个 dispatcher helper 全部 1 行委托,共享 `InvokeExtractionAsync` wrapper;
  无跨 slice shim 残留(2 个 resolve helper 完全删掉)。