# Ontology 应用服务抽取 + dispatcher → application-service 拆分(6/13)

**状态**: 已完成(6/13 slice 落地,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `dotnet`
**范围**: 6 个 internal `ontology.*` arms(get / edit / export / reset /
provenance / sources)+ 1 个 cross-surface shared helper
(`published.ontology` + `published.release.ontology` 共用),从
`InternalOperationDispatcher` god-class 拆出一个
`IOntologyApplicationService`(定义在 `ISEStudio.Application.Integration`,
实现在 `ISEStudio.Integration`),并把 4 个 DTO (`OntologyEditResult` +
`SourceOut` + `ProvenanceGroupOut` + `ProvenanceSourceOut`) 从
`ISEStudio.Ontology` 搬到 `ISEStudio.Application.Ontology`。

接续 [2026-08-28-vocabulary-application-service.md](2026-08-28-vocabulary-application-service.md)
5/13 slice,本切片验证模板在 6-arms 含编辑路径(snapshot + capture-rollback)的
ontology slice 的可用性,确认 `RunWithExtractionGuardAsync` 守卫包装仍然
留在 dispatcher arm 层(switch arm 上)。

---

## 1. 背景

`InternalOperationDispatcher` 在 5/13 切片后 ~3540 行,其中 ontology helpers
占 ~224 行(原 lines 459-1255 的多个 helpers,加上 DeserializeOntologyEditBody
+ ParseExportFormat + 3 个 DI helper),承载:

- 6 个 internal `ontology.*` reads/writes:
  - reads (3):`get` / `export` / `provenance` / `sources`(其中后两个归类为
    read-only reads,虽然是不同 service 源)
  - writes (3):`edit` / `reset` (RunWithExtractionGuardAsync 守卫) /
    `provenance`/`sources`(纯 read 但属于 ontology 域)
- 1 个 cross-surface shared helper:`published.ontology` +
  `published.release.ontology` 共用 `InvokePublishedOntologyAsync`

注意 external.* 5 arms(metadata / ontology / classes / export / individual /
individuals)及 `InvokeExternalOntologyAsync` / `InvokeExternalExportAsync`
属于 11/13 external+published slice,**不在本次范围**。

每个 helper 都重复 4 段 boilerplate:`ResolveOntologyService` /
`ResolveOntologyProvenanceService` / `ResolvePublishedOntologyService` 解析
+ KS envelope 拆解 + `DeserializeBody<T>` / `ParseExportFormat` +
`WrapAsync` + null-coalesce 到 `EmptyXxx` 匿名 fallback。

同时 4 个 DTO (`OntologyEditResult` + `SourceOut` + `ProvenanceGroupOut` +
`ProvenanceSourceOut`) 住在 `ISEStudio.Ontology`,跟 vocabulary slice 的 10
个 SKOS DTO 一样阻塞应用服务接口。

## 2. 决策

### 2.1 DTO 搬入 `ISEStudio.Application`(沿用 vocabulary 模板)

**结论**:搬。命名空间 `ISEStudio.Application.Ontology`。

**实现细节**:
- 新增 `ISEStudio.Application/Ontology/OntologyEditResult.cs`(1 record,11 行)
- 新增 `ISEStudio.Application/Ontology/ProvenanceDtos.cs`(3 records,18 行)
- 删 `ISEStudio/Ontology/ProvenanceDtos.cs`
- 改 `OntologyService.cs` + `OntologyProvenanceService.cs` 加
  `using ISEStudio.Application.Ontology;`
- `OntologyEditResult` record 之前内嵌在 `OntologyService.cs` 顶部,现在独立成文件

### 2.2 应用服务接口 = 6 internal + 1 cross-surface = 7 个强类型方法

**结论**:签名采用 `Task<TOut?>(InternalRequest, CancellationToken)`,7 个方法。

**理由**:
- 沿用 abox / vocabulary slice 的 envelope 入参(2.2),让 app service 接受
  `InternalRequest` 而非 5 个散参数。
- 6 个 internal 方法:
  - `GetAsync` → `OntologyService.GetViewAsync(ksId, actor, ct)`
  - `EditAsync` → `OntologyService.EditAsync(ksId, op, actor, ct)`
  - `ExportAsync` → `RdfExportService.ExportAsync(ks, TBox, format, ct)`
  - `ResetAsync` → `OntologyService.ResetAsync(ksId, actor, ct)`
  - `ProvenanceAsync` → `OntologyProvenanceService.GetProvenanceAsync(ksId, actor, ct)`
  - `SourcesAsync` → `OntologyProvenanceService.ListSourcesAsync(ksId, actor, ct)`
- 1 个 cross-surface 方法 `GetPublishedAsync`:
  - internal version(无 `version` 参数)→ 通过 `request.PublicId` 查 KS,
    客户端排序选最新 `ReleaseDeployment` 行,取其 `ReleaseId` 找到
    `OntologyReleaseEntity`,用 `release.Version` 调
    `PublishedOntologyService.GetViewAsync`
  - pinned version(有 `version`)→ 直接转发 `request.ResourceId` 给
    `PublishedOntologyService.GetViewAsync`
  - dispatcher 通过 `InvokePublishedOntologyAsync(request, version, ct)`
    switch arm 调用,`version` 参数当前被忽略(`GetPublishedAsync` 从
    `request.ResourceId` 内部读取);保留参数避免改 switch arm。

### 2.3 dispatcher arm 不动,只缩 helper(沿用 2.3)

**结论**:6 个 `InvokeOntology*Async` + 1 个 `InvokePublishedOntologyAsync`
helper 都缩成 1 行委托。

**实现细节**:
- 7 个 helper 都需要 ResolveKsAsync + WrapAsync + null-degrade → 提取成
  `InvokeOntologyAsync` 共享 wrapper,签名
  `Func<IOntologyApplicationService, Task<object?>> call`,
  `Func<object> onMissing`, `Func<object>? onNull = null`。
- 全部 internal 6 个 `Invoke*Async` 都是 1 行委托;`InvokePublishedOntologyAsync`
  因为 dispatcher switch arm 传 `version` 参数(为兼容 published.ontology
  vs published.release.ontology),仍然是 expression-body 但只 4 行。

### 2.4 守卫包装 (`RunWithExtractionGuardAsync`) 留在 dispatcher arm 上(沿用 2.4)

`ontology.edit` + `ontology.reset` 在 dispatcher switch arm 层仍然 wrap
`RunWithExtractionGuardAsync`:

```csharp
"ontology.edit" => RunWithExtractionGuardAsync(
    request, cancellationToken,
    () => InvokeOntologyEditAsync(request, cancellationToken)),
```

应用服务不实现 extraction guard — 守卫属于 transport-level concern,跟
abox / conflicts / documents / vocabulary 切片一致。

### 2.5 dispatcher 跨 slice shim: `ResolveOntologyService` + `ResolveExternalOntologyService` + `ParseExportFormat`

**结论**:保留为 1 行 shim,等 9/13 history + 11/13 external 切片处理。

**理由**:
- `ResolveOntologyService` 仍被 typed facade `IIntegrationApiFacade.GetOntologyAsync`(line 421)使用 — facade 路径绕过 dispatcher,无法复用 app service。
- `ResolveExternalOntologyService` + `ParseExportFormat` 仍被
  `InvokeExternalOntologyAsync` + `InvokeExternalExportAsync`(external
  slice 11/13)使用。
- vocabulary slice 已建立此模式(shim section 在 dispatcher 顶部)。

### 2.6 `DeserializeOntologyEditBody` + `JsonElementToObject` 搬到 app service

**结论**:搬到 `OntologyApplicationService` 私有方法。

- 之前是 dispatcher private static helper,因为 abox / vocabulary slice 已经有
  `InternalRequestHelpers.DeserializeLooseBody`(同 bug)。
- ontology.edit body 是 loose dict(`{op: "add_class", label: "...", ...}`),
  不能直接 deserialize 到 typed C# record(`OntologyEditOp`),Python 端是
  `Mapping[str, Any]`,所以 dispatcher 自己手撕 envelope key `_` 然后
  序列化成 `Dictionary<string, object?>`。
- `JsonElementToObject` 同样搬过去,只服务于 ontology.edit 的 body 反序列化。

## 3. 文件清单

### 新增

| 文件 | 行 | 说明 |
|------|----|----|
| `src/ISEStudio.Application/Ontology/OntologyEditResult.cs` | 11 | 1 record |
| `src/ISEStudio.Application/Ontology/ProvenanceDtos.cs` | 18 | 3 records |
| `src/ISEStudio.Application/Integration/IOntologyApplicationService.cs` | 65 | 7-method 接口 |
| `src/ISEStudio/Integration/OntologyApplicationService.cs` | 235 | 7 methods + 2 私有 helper |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | -224 +98 行(ontology section + 3 shim helper) |
| `src/ISEStudio/Ontology/OntologyService.cs` | -11 行(`OntologyEditResult` 搬走) + 1 行(using) |
| `src/ISEStudio/Ontology/OntologyProvenanceService.cs` | +1 行(using) |
| `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs` | +6 行(DI 注册) |

### 删除

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Ontology/ProvenanceDtos.cs` | 完全删除(3 records 搬到 Application) |

### dispatcher 行数

- 前:3540 行
- 后:3412 行(vocabulary section 224 → 98 行,3 shim helper +36 行)
- 净减少 **128 行**

## 4. 验证

```
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851 (1 m 33 s)

$ dotnet test src/ISEStudio.ApiContract.Tests/...
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167 (54 s)
```

零回归;`RunWithExtractionGuardAsync` 守卫保持 409 + job_id envelope 行为,
`ParseExportFormat` 仍然抛 `ValidationException`(→ HTTP 400)。

---

## 5. 后续切片(剩 7)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 7/13 extraction (lifecycle + job_id envelope)
- [ ] 8/13 resolution
- [ ] 9/13 history (free `ResolveOntologyService` shim)
- [ ] 10/13 prompts
- [ ] 11/13 external + published (free `ResolveExternalOntologyService` + `ParseExportFormat` shim)
- [ ] 12/13 providers + settings + auth + knowledge + tokens + mcp_tokens
- [ ] 13/13 rdf.import

每个切片都会复用本切片定下的 4 段模式:
1. DTO 搬入 `ISEStudio.Application.{Vocabulary,Ontology,Integration,...}`
2. `IXxxApplicationService` 接口: `Task<T?>(InternalRequest, CancellationToken)`
3. dispatcher arm 不动,helper 缩成 1 行委托
4. 守卫包装留在 arm 上,不沉到 app service

---

## 6. Decision Log

- 2026-08-28: 6/13 ontology slice 完成(commits 待 push)。
  本切片锁定 6-arms 含编辑路径 ontology slice 的拆分模式,验证
  `RunWithExtractionGuardAsync` 守卫保持 dispatcher arm 上(app service
  不实现 guard)。
  7 个方法 1 行委托,共享 `InvokeOntologyAsync` wrapper;
  shim section `ResolveOntologyService` + `ResolveExternalOntologyService` +
  `ParseExportFormat` 留给 typed facade / 11/13 external slice。
  `DeserializeOntologyEditBody` + `JsonElementToObject` 搬到 app service
  私有方法(本切片独有,不像 vocabulary 复用 `InternalRequestHelpers`,
  因为 ontology.edit body 是 loose dict 而非 typed record)。