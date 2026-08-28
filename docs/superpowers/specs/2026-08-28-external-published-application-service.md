# External + Published 应用服务抽取 + dispatcher → application-service 拆分(11/13)

**状态**: 已完成(11/13 slice 落地,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `main`
**范围**: 18 个 dispatcher arms:
- `external.ontology` / `external.metadata` / `external.classes` /
  `external.export` / `external.individual` / `external.individuals`
  (6 个 read,public_id-keyed)
- `published.*` × 6 + `published.release.*` × 6(12 个 read,
  current 与 pinned 两路径共享同一组 helper)

从 `InternalOperationDispatcher` god-class 拆出两个应用服务:
`IExternalApplicationService`(委托 `ExternalApiService` +
`ExternalOntologyService`)与 `IPublishedApplicationService`(委托
`PublishedDataService`)。接口定义在 `ISEStudio.Application.Integration`,
实现在 `ISEStudio.Integration`。

接续 [2026-08-28-prompts-application-service.md](2026-08-28-prompts-application-service.md)
10/13 slice,本切片验证模板在「全 read + 双路径共享 helper +
Infrastructure-entity 依赖 DTO」组合下的可用性。

---

## 1. 背景

`InternalOperationDispatcher` 在 10/13 切片后 ~3370 行,其中
external helpers 占 ~115 行(6 个 helper + `ResolveExternalApiService`
+ `ResolveExternalOntologyService` + `ParseExportFormat` shim),
published helpers 占 ~165 行(6 个 helper + `ResolvePublishedDataService`
+ `ResolveServingAsync` + `TryReadScopes`),合计 ~280 行。

关键约束(在 10/13 切片时已确认):

1. **query arms 不能搬**:`external.query` / `published.query` /
   `published.release.query` 走 `IIntegrationApiFacade.QueryAsync`,
   而 facade 就是 dispatcher 自身 — 经应用服务路由会循环依赖。
   `InvokeExternalQueryAsync` 留在 dispatcher(服务 3 个 query 分支)。
2. **vocabulary facade 转发不能搬**:8 个
   `external.vocabulary.*` / `published.vocabulary.*` /
   `published.release.vocabulary.*` 分支已经是
   `IVocabularyApplicationService` 的 1 行委托(5/13 切片),本切片不动。
3. **`published.ontology` / `published.release.ontology` 不能搬**:
   已由 `IOntologyApplicationService.GetPublishedAsync` 承载(6/13
   切片),本切片只把它的 1 行 delegate 从 `(request, version, ct)`
   签名收缩为 `(request, ct)` — `version` 参数本就被
   `GetPublishedAsync` 忽略(它从 `request.ResourceId` 读
   effectiveVersion,见 §2.3)。

## 2. 决策

### 2.1 接口 = 全 `Task<object?>` 签名

**结论**:两个接口共 12 个方法,全部
`Task<object?>(InternalRequest, CancellationToken)`,不做 typed DTO。

**理由**:
- external metadata envelope 是匿名的(`ExternalApiService.
  GetMetadataAsync` 返回匿名 wire shape)
- published 的 `ServingContext` 依赖 Infrastructure entities
  (KS / release / deployment / serving-store tuple),不能进
  `ISEStudio.Application`(零 `<ProjectReference>`)
- 与 7/13 extraction 切片的「Infrastructure-dependent DTO 返回
  `Task<object?>`」先例一致

**null 返回语义**:返回 null 表示 dispatcher 应落到 schema-compatible
空 envelope(per-arm 的 `onMissing`);缺失 `public_id` 抛
`InvalidOperationException`(external,与旧 helper 一致);
`ExportFilePayloadException` / `KeyNotFoundException` /
`ValidationException` 的 throw 语义 1:1 保留(published)。

### 2.2 pinned version 走 `request.ResourceId`(复用 6/13 先例)

**结论**:current 与 pinned 两路径共享同一组方法,pinned version
由 controller 绑定进 `request.ResourceId`(current 路径为 null)。

**先例**:`OntologyApplicationService.GetPublishedAsync` 内部读
`request.ResourceId` 作为 effectiveVersion(6/13 切片)。

**连带简化**:dispatcher 的 `version: null` /
`version: request.ResourceId` switch 参数全部删除,
`InvokePublishedOntologyAsync` 的 `string? version` 形参删除(该形参
从未被使用 — `GetPublishedAsync` 自读 `request.ResourceId`)。

### 2.3 dispatcher 12 个 helper 全部 1 行委托

**结论**:external 6 个 + published 6 个 helper 都缩成 1 行委托,
新增两个 shared wrapper(`InvokeExternalAsync` / `InvokePublishedAsync`,
与 9/13 `InvokeHistoryAsync` 同构)。

**fallback 映射**(与旧 helper 1:1):

| arm | onMissing |
|-----|-----------|
| external.ontology | `EmptyOntologyResponse` |
| external.metadata | `EmptyKnowledgeSystem` |
| external.classes | `new { classes = [], total = 0 }` |
| external.export | `""` |
| external.individual | `EmptyIndividualRef` |
| external.individuals | `EmptyListResponse` |
| published.metadata / release | `EmptyRelease` |
| published.manifest / release | `EmptyReleaseManifest` |
| published.classes / release | `EmptyListResponse` |
| published.export / release | `Array.Empty<byte>()` |
| published.individual / release | `EmptyIndividualRef` |
| published.individuals / release | `EmptyListResponse` |

`onNull` 默认同 `onMissing`(旧 helper 的 null-coalescing 语义)。

### 2.4 shim 全清

**结论**:`ResolveExternalOntologyService` + dispatcher 私有
`ParseExportFormat` 删除;`ResolveExternalApiService` /
`ResolvePublishedDataService` / `ResolveServingAsync` /
`TryReadScopes` 随区块删除。

**`ParseExportFormat` 归宿**:6/13 切片已把它提升为
`OntologyApplicationService.ParseExportFormat`(internal static,注释
明确预留「external.export 经 dispatcher 的 11/13 切片未来共用」),
本切片 `ExternalApplicationService.ExportAsync` 直接调用它,
不再复制解析规则。

**保留**:`ResolveOntologyService` shim(typed facade 的
`IIntegrationApiFacade.GetOntologyAsync` 绕过 dispatcher,仍在使用)。

### 2.5 无 extraction guard

**结论**:18 个 arm 全是 read,无 `RunWithExtractionGuardAsync`
守卫(与旧代码一致 — external/published 区块本就没有 409 guard)。

## 3. 文件清单

### 新增

| 文件 | 行 | 说明 |
|------|----|----|
| `src/ISEStudio.Application/Integration/IExternalApplicationService.cs` | 47 | 6-method 接口 |
| `src/ISEStudio.Application/Integration/IPublishedApplicationService.cs` | 54 | 6-method 接口 |
| `src/ISEStudio/Integration/ExternalApplicationService.cs` | 96 | 6 methods,委托 ExternalApiService / ExternalOntologyService |
| `src/ISEStudio/Integration/PublishedApplicationService.cs` | 140 | 6 methods,委托 PublishedDataService |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | external ~115 行 + published ~165 行区块折叠为 2 个 wrapper + 12 个 1 行委托;switch 删 14 处 `version:` 参数;shim 清 2 处 |
| `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs` | +2 个 DI 注册(Scoped) |

### dispatcher 行数

- 前:~3370 行(10/13 后)
- 后:2929 行(11/13 后)
- 净变化 **-150 行**(diff: 144 insertions / 294 deletions)

## 4. 验证

```
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851

$ dotnet test src/ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167
```

零回归;12 个 fallback envelope 全部保留,throw 语义
(400/404/409/raw-bytes)不变,wire shape 完全不变。

---

## 5. 后续切片(剩 2)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 12/13 providers + settings + auth + knowledge + tokens + mcp_tokens
- [ ] 13/13 rdf.import

---

## 6. Decision Log

- 2026-08-28: 11/13 external + published slice 完成。
  本切片锁定「全 read + 双路径共享 helper(pinned version 走
  `request.ResourceId`)+ Infrastructure-entity 依赖返回
  `Task<object?>`」的拆分模式。query arms 因 facade 循环依赖
  永久留在 dispatcher;`ParseExportFormat` 复归 6/13 提升的
  `OntologyApplicationService.ParseExportFormat`,dispatcher 私有一份
  删除。net dispatcher -150 行(本切片是拆分启动以来单切片最大
  瘦身)。
