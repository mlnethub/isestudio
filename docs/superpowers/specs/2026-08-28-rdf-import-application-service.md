# rdf.import 应用服务抽取 + dispatcher 拆分(13/13)

**状态**: 已完成(13/13 slice 落地,commit e5dc0eb,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `main`
**范围**: 1 个 dispatcher arm:`rdf.import`(multipart RDF import)

从 `InternalOperationDispatcher` god-class 拆出最后一个应用服务
`IRdfImportApplicationService`。接口定义在
`ISEStudio.Application.Integration`,实现在 `ISEStudio.Integration`。

接续 [2026-08-28-providers-settings-auth-knowledge-tokens-application-service.md](2026-08-28-providers-settings-auth-knowledge-tokens-application-service.md)
12/13 slice,本切片是 dispatcher god-class 拆分工作流
([ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md))
的终局 — 13/13 全部完成。

---

## 1. 背景

`InternalOperationDispatcher` 在 12/13 切片后 2526 行,其中 rdf.import
区块 ~100 行:

- `InvokeRdfImportAsync` helper(~37 行):KS Guid 校验 + body 字段
  unpacking(file / filename / target / strategy / format / base_iri)+
  `RdfImportRequest` 构造 + `RdfImportService.ImportAsync` 调用
- `ProjectRdfImportResult` 投影(~46 行):filename … graph_iri /
  view / open_conflicts / validation / terminology 完整 snake_case
  wire shape
- `ProjectConflictOut` 投影(~14 行):open_conflicts 嵌套元素
- `EmptyImportResponse()` fallback(保留在 dispatcher)

关键约束:

1. **`RdfImportResult` 及成员都是 Infrastructure DTO**
   (`ISEStudio.Ontology` 的 `RdfImportResult` /
   `OntologyResponse` / `ABoxValidationReport`,
   `ISEStudio.Extraction` 的 `TerminologyResult`),不能进
   `ISEStudio.Application` → 接口签名 `Task<object?>(InternalRequest,
   CancellationToken)`(7/13 extraction 先例)。
2. **`ConflictOut` 在 `ISEStudio.Application.Conflicts`**
   (Application 项目内),`ProjectConflictOut` 投影可直接搬进 app
   service 实现。grep 确认它是 dispatcher 里唯一使用点。
3. **`RunWithExtractionGuardAsync` 守卫留在 switch arm** —
   arm 上仍包裹 `RunWithExtractionGuardAsync(request,
   cancellationToken, () => InvokeRdfImportAsync(...))`,app service
   只做 import 本身。

## 2. 决策

### 2.1 单 arm 切片:直接 resolve,不走泛型 wrapper

**结论**:只有一个 arm,不需要 `InvokeXxxAsync(request, ct, call,
onMissing, onNull)` 泛型 wrapper + 委托组合(2/13 conflicts 起
每个多 arm 切片的惯例);`InvokeRdfImportAsync` 直接 resolve
app service 并调用,名称保留(switch arm 不变)。

```csharp
private Task<object?> InvokeRdfImportAsync(
    InternalRequest request, CancellationToken cancellationToken)
{
    var app = _services.GetService(typeof(IRdfImportApplicationService))
        as IRdfImportApplicationService;
    if (app is null)
    {
        return Task.FromResult<object?>(EmptyImportResponse());
    }
    return WrapAsync(async () =>
        await app.ImportAsync(request, cancellationToken).ConfigureAwait(false)
            ?? EmptyImportResponse());
}
```

### 2.2 throw / null / fallback 语义 1:1 保留

- KS Guid 缺失 → `InvalidOperationException("Knowledge system id is
  required for rdf.import.")`(app service 内 throw,经 WrapAsync 传播)
- body 缺失 → app 返回 `null` → dispatcher `?? EmptyImportResponse()`
  (旧实现 `svc is null || request.Body is null` →
  EmptyImportResponse 同语义)
- file 缺失 → `RdfImportException("file is required and must be
  non-empty")`(app service 内 throw)
- app service 未注册(hand-built dispatcher 单测)→ EmptyImportResponse

### 2.3 `ProjectConflictOut` 随切片搬走

**结论**:grep 确认 `ProjectConflictOut` 唯一调用点是
`ProjectRdfImportResult` 的 `open_conflicts`,两者一起搬入
`RdfImportApplicationService`(conflicts 切片自己的投影早已在
`ConflictApplicationService` 内,不冲突)。

### 2.4 DI 注册位置

**结论**:`OntologyServiceCollectionExtensions`,在
`services.AddScoped<RdfImportService>();` 旁加
`services.AddScoped<IRdfImportApplicationService,
RdfImportApplicationService>();`(Scoped — 与 RdfImportService 共享
请求 DbContext,与 11/13 external/published 同文件的先例一致)。

## 3. 文件清单

### 新增(2)

| 文件 | 说明 |
|------|----|
| `ISEStudio.Application/Integration/IRdfImportApplicationService.cs` | 1-method 接口(`ImportAsync`) |
| `ISEStudio/Integration/RdfImportApplicationService.cs` | 委托 RdfImportService,2 投影 |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | 旧 helper + 2 投影删除,换直接 resolve 版;switch arm 不变 |
| `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs` | +1 DI 注册(IRdfImport,在 RdfImportService 旁) |

### dispatcher 行数

- 前:2526 行(12/13 后)
- 后:2447 行(13/13 后)
- 净变化 **-79 行**

## 4. 验证

```text
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851

$ dotnet test src/ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167
```

零回归;wire shape(含 open_conflicts / validation /
terminology 嵌套)完全不变,throw 语义(400/409)不变。

---

## 5. 拆分工作流收尾

13/13 全部完成,`InternalOperationDispatcher` god-class 拆分工作流
([ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md))
闭环:

| 切片 | 范围 | commit |
|------|------|--------|
| 1/13 ABox(试点) | abox.* 12 op | 3c3002d |
| 2/13 | conflicts.* 9 op | fecf888 |
| 3/13 | documents.* 10 op | d2c2532 |
| 4/13 | releases.* 16 op | 15faf15 |
| 5/13 | vocabulary.* 28 op + 8 facade | 3700d38 |
| 6/13 | ontology.* 6 op | 7cace89 |
| 7/13 | extraction.* 5 op | 3c5534d |
| 8/13 | resolution.* 5 op | 95bb148 |
| 9/13 | history.* 2 op | c3dc6c0 |
| 10/13 | prompts.* 4 op | 4acd10f |
| 11/13 | external + published(6 + 12 op) | e74a099 |
| 12/13 | providers/settings/auth/knowledge/tokens(33 op) | 6bcaffb + 55693b8 |
| 13/13 | rdf.import(1 op) | e5dc0eb |

dispatcher 由拆分启动前的 3370+ 行降到 2447 行;所有 switch arm 保留
`RunWithExtractionGuardAsync` 409 守卫、空 envelope fallback 和
1:1 的 throw 语义。

---

## 6. Decision Log

- 2026-08-28: 13/13 完成(commit e5dc0eb)。单 arm 切片不走泛型
  wrapper,直接 resolve + `?? EmptyImportResponse()` 兜底;
  `ProjectConflictOut` 确认唯一使用点后随切片搬走。拆分工作流
  13/13 全部闭环,850 unit + 167 contract 全绿。
