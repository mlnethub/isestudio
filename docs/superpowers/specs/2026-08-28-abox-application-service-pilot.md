# ABox 应用服务抽取 + dispatcher → application-service 拆分(试点切片)

**状态**: 已完成(pilot 实现 + 850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `dotnet`
**范围**: 12 个 `abox.*` operation,从 4413 行 `InternalOperationDispatcher` god-class
拆出一个 `ABoxApplicationService`(定义在 `ISEStudio.Application`,实现在 `ISEStudio.Integration`),
并把 12 个 DTO 从 `ISEStudio.Ontology` 搬到 `ISEStudio.Application.Ontology`。

---

## 1. 背景

`InternalOperationDispatcher` 现在 ~4400 行,里面 12 个 `abox.*` helper
(`InvokeAboxListClassesAsync` … `InvokeAboxRevokeValidationDecisionAsync`)
各自重复 5 段同样的 boilerplate:

1. `ResolveAboxService()` 服务定位解析 + null-degrade
2. `request.KnowledgeSystemGuid` / `request.Body` / `request.Query` envelope 拆解
3. `DeserializeBody<T>`(走 loose `"_"` envelope key + snake_case JSON 策略)
4. `ExtractIriFromBody`(只用于 `abox.delete_individual`)
5. `Task<object?>(svc.XAsync(...)) ?? EmptyFallback()` 的 null-coalesce 到匿名 fallback

同时 12 个 ABox DTO(`ClassEntry` / `ClassesOut` / `IndividualListItem` /
`IndividualsOut` / `LabeledIri` / `ObjectAssertionOut` / `DataAssertionOut` /
`IndividualOut` / `CreateIndividualRequest` / `IndividualRef` /
`DeleteIndividualResponse` / `AssertionRequest` / `ResetAboxRequest` /
`ResetAboxResponse` / `FixViolationRequest` / `ValidationViolationOut` /
`ViolationFixOut` / `ValidationReportOut` / `ValidationReportCounts` /
`ValidationDecisionOut` / `ValidationDecisionListOut` /
`RevokeValidationDecisionResponse`)都住在 `ISEStudio.Ontology` 命名空间里,
被 `ISEStudio.Application` 引用是不可能的(后者是零 ProjectReference contracts
项目)。这意味着用户在生产里真正想要的"应用服务接口在 Application 层"无法
落地,必须先把 DTO 搬过去。

## 2. 决策

### 2.1 DTO 搬入 `ISEStudio.Application`(Plan A)

**结论**:搬。理由:`ISEStudio.Application` 是零依赖 contracts 项目,既有的
`IIntegrationApiFacade` / `IInternalOperationDispatcher` 接口都住在那里;
应用服务接口要写强类型方法签名就必须把 DTO 搬过去。

**实现细节**:
- `git mv src/ISEStudio/Ontology/ABoxDtos.cs src/ISEStudio.Application/Ontology/ABoxDtos.cs`
- 改 `namespace ISEStudio.Ontology;` → `namespace ISEStudio.Application.Ontology;`
- 文档里两处 `<see cref="ABoxValidator"/>` → `<c>ABoxValidator</c>`(`<see cref>`
  会让 cref 解析器去 Application 项目找 `ABoxValidator`,编译通不过)
- 6 个 web 项目内消费者(`ABoxManager.cs` / `ABoxService.cs` /
  `ABoxValidator.cs` / `ExternalApiService.cs` / `PublishedDataService.cs` /
  `ValidationDecisionService.cs`)+ dispatcher 加 `using ISEStudio.Application.Ontology;`

### 2.2 应用服务接口 = 12 个强类型方法 + `InternalRequest` 入参

**结论**:签名采用 `Task<TOut?>(InternalRequest, CancellationToken)`,所有 12 个方法。

**理由**:
- `InternalRequest` 是 dispatcher 已经构造好的 envelope,里面 `KnowledgeSystemGuid` /
  `ResourceId` / `Body` / `Query` / `Actor` 都齐了 —— 让 app service 接受 envelope
  而不是 (Guid + string? + JsonElement? + Actor) 这 5 个散参数,新增字段不会破坏
  调用方。
- 返回 `T?` 而不是 `T`,因为 dispatcher 端要做的 null-coalesce
  (typed DTO null → 匿名 fallback envelope)是 transport-level concern,
  不应该沉到 app service。

### 2.3 dispatcher arm 不动,只缩 helper

**结论**:switch arm(186-204 行)保持原样:`InvokeAboxListClassesAsync(...)`、
`RunWithExtractionGuardAsync(request, ct, () => InvokeAboxAddAssertionAsync(...))`。
helper 签名 `Task<object?>`,内部委托到 `IABoxApplicationService`。

**理由**:
- switch arm 是 frozen-by-test 的 contract gate —— `InternalApiContractTests`
  从 Python baseline 枚举所有 op name,任何未处理名字都是编译可见的 gap。
  拆 arm 会立刻断测试。
- MCP transport(`ISEStudioMcpTools`)是 facade 的第二消费者,
  facade 必须保留 `abox.*` 这 12 个 operation name 才能继续把 MCP `abox.action`
  请求翻译成 envelope;arm 一删 MCP 就坏。

### 2.4 守卫包装 (`RunWithExtractionGuardAsync`) 留在 arm 上,不沉到 app service

**结论**:试点切片不动守卫。

**理由**:`ABoxService.cs` 的 doc comment 明确写了"guard is on the switch arm"。
下沉会引入两层职责(app service 同时负责业务 + 守卫);试点先把 envelope unpacking
这一层干净拆出去,守卫下沉放到推广期评估,届时看 dispatcher 是否真的只剩 transport-level
concern。

### 2.5 匿名 fallback envelope 不下沉到 app service

**结论**:`EmptyListResponse()` / `EmptyIndividualRef()` / `EmptyResetAboxResponse()` /
`EmptyValidateReport()` / inline `{removed:0}` / inline `{revoked:Guid.Empty}`
留在 dispatcher。

**理由**:这些匿名 snake_case envelope 的 wire shape(`{conforms:true,violations:[]}` 等)
和 typed DTO 的 JSON 序列化(`ValidationReportOut` → `{violations, counts, truncated}`)
不一样,`InternalApiContractTests` 严格断言 wire bytes。如果 app service 返回
typed fallback,degraded 路径的 wire bytes 会变,contract test 立刻挂掉。

把 fallback 留在 dispatcher 是唯一能保持 bytes-stable 的做法;通过让 app service
返回 `T?`(null = no data)和 dispatcher 端做 `?? onMissing()` 把 null 折成
匿名 fallback 实现。

### 2.6 DI 注册走 `AddAboxServices` 扩展,不改 `Program.cs`

**结论**:`ABoxServiceCollectionExtensions.AddAboxServices` 同时注册 `ABoxService`
和 `IABoxApplicationService → ABoxApplicationService`。

**理由**:`Program.cs:498` 一行 `AddAboxServices()` 调用已经存在,把 app service
注册折叠进去不需要改 Program.cs;P3-2 时期的 Pester smoke-check 也只认这个调用。

## 3. 实施

### 3.1 文件清单

| 文件 | 变化 |
| --- | --- |
| `src/ISEStudio.Application/Ontology/ABoxDtos.cs` | **移动**(`git mv`)+ 命名空间改 |
| `src/ISEStudio.Application/Integration/IABoxApplicationService.cs` | **新增** —— 12 强类型方法 |
| `src/ISEStudio/Integration/ABoxApplicationService.cs` | **新增** —— envelope unpacking + `DeserializeBody<T>` + `ExtractIriFromBody` + `QueryString` / `QueryInt` |
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | 12 helper 缩成 1 行委托;switch arm 不动;`RunWithExtractionGuardAsync` 不动;fallback factories 不动 |
| `src/ISEStudio/Ontology/ABoxService.cs` | `using ISEStudio.Application.Integration;` / `ISEStudio.Integration;` + `AddAboxServices` 多注册一行 |
| `src/ISEStudio/Ontology/ABoxManager.cs` / `ABoxValidator.cs` / `ExternalApiService.cs` / `PublishedDataService.cs` / `ValidationDecisionService.cs` | 各加一行 `using ISEStudio.Application.Ontology;` |

### 3.2 dispatcher helper 改写模式

12 个 helper 全部缩成单行委托,共享一个 `InvokeAboxAsync(...)` envelope:

```csharp
private Task<object?> InvokeAboxListClassesAsync(InternalRequest request, CancellationToken ct) =>
    InvokeAboxAsync(request, ct,
        async app => (object?)await app.ListClassesAsync(request, ct).ConfigureAwait(false),
        onMissing: () => new { classes = Array.Empty<object>(), total = 0 });

private Task<object?> InvokeAboxAsync(
    InternalRequest request,
    CancellationToken ct,
    Func<IABoxApplicationService, Task<object?>> call,
    Func<object> onMissing)
{
    var app = _services.GetService(typeof(IABoxApplicationService)) as IABoxApplicationService;
    if (app is null)
    {
        return Task.FromResult<object?>(onMissing());
    }
    return WrapAsync(async () =>
    {
        var out_ = await call(app).ConfigureAwait(false);
        return out_ ?? onMissing();
    });
}
```

**为什么 helper 端用 `async (app) => (object?)await ...`**:
C# 协变规则不允许把 `Task<TDerived?>` 直接当 `Task<object?>` 用,
每个 helper 内部 `async` + 显式 `(object?)await` 转换,触发编译器把它包成 `Task<object?>`。

**为什么 `_services.GetService(typeof(...)) as IABoxApplicationService`** 而不是
构造函数注入 dispatcher:`FacadeSmokeTests` 是手搓 `new InternalOperationDispatcher(services)`
构造的,需要在 null-degrade 分支里优雅退化为 fallback envelope。

### 3.3 Controller 与 MCP 不动

`ABoxController.cs` 12 个 endpoint 仍然是 `InvokeAsync("abox.x", ReqGuid(id), ct)`;
`ISEStudioMcpTools.cs:190/365/479` 的 `abox.action` / `abox.get_individual` /
`abox.list_individuals` MCP tool 仍然走 facade。

## 4. 测试

- **基线**(本切片前):`ISEStudio.Tests` 850 通过 / 1 skip(PG-only)/ 0 失败;
  `ISEStudio.ApiContract.Tests` 167 通过 / 0 失败。
- **本切片后**:`ISEStudio.Tests` **850 通过 / 1 skip / 0 失败**;
  `ISEStudio.ApiContract.Tests` **167 通过 / 0 失败**。

`InternalApiContractTests` 是关键 gate —— 它从 frozen Python OpenAPI baseline
枚举所有 `abox.*` op name,跑一次走通路径,断言 200 + schema-compatible wire bytes。
这条 gate 绿证明 dispatcher → app service → ABoxService 整条链没有任何
wire byte 偏移(包括 12 个 typed DTO 序列化路径 + 8 个匿名 fallback envelope +
6 个 extraction-guard 409 envelope)。

## 5. 推广 checklist(给后续 dispatcher 切片照走)

下一个可以照搬的候选 slice(按复杂度和风险排序):

1. **`conflicts.*`** —— 5 个 read + 3 个 noop-op,fallback 工厂少,无 body deserialize,
   是最干净的下一个。
2. **`documents.*`** —— list / get / upload 都是 read-heavy,typed DTO 多但 envelope 简单。
3. **`vocabulary.*`** —— `TerminologyResult` 已经有强类型,搬 DTO 是纯移动。
4. **`releases.*`** —— `ReleaseOut` typed DTO 已经成熟,body deserialize 有但只一处。
5. **`external.*`** —— 5 个读端点,DTO 已经 `ISEStudio.Application` 化(在
   `ISEStudio.Application.External`),只要抽 `IExternalApiApplicationService`。
6. **`published.*`** —— 6 个读端点 + `PublishedDataService` 已经 typed 化。

每个 slice 都要按本 spec §2.1~2.6 的决策走一遍,产出:
- 接口 `IXxxApplicationService.cs` 在 `ISEStudio.Application/Integration/`
- 实现 `XxxApplicationService.cs` 在 `ISEStudio.Integration/`
- dispatcher 对应 N 个 helper 缩成 N 行
- DI 注册走对应 `AddXxxServices` 扩展(不要改 Program.cs)
- 测试门:850 + 167 全绿(零 wire 偏移)

**何时停下**:
- guard(`RunWithExtractionGuardAsync`)下沉问题 —— 试点保留在 arm 上,
  推广期再决定要不要搬到 app service;一旦搬到 app service,`conflicts.dismiss` /
  `conflicts.resolve` 也能复用到同一机制。
- Controller 直接调 app service(完全绕过 dispatcher)—— 试点不做,
  因为 MCP transport 仍然需要 dispatcher 作为单一 op-name 入口;等所有 slice
  都搬完再统一评估。

---

## 6. 2026-08-28 后续补充:跨 slice 决策(同主线所有 slice 沿用)

第 1 个 slice 推广到第 2 个之前,用户拍板了 3 个跨 slice 决策(登记到
[[ontopilot-dispatcher-split-workflow]] "跨 slice 决策"小节),后续 slice 都按这三条走:

### 6.1 跨 slice 私有 helper 集中

`ResolveKsAsync` / `QueryString` / `QueryInt` / `ExtractBodyIri` / `ExtractPayload` /
`ExtractChunkIds` / `DeserializeBody<T>`(reflection-based 通用版)等搬到
`src/ISEStudio/Integration/InternalRequestHelpers.cs`。

**abox 试点影响**:本 spec §3.2 / [ABoxApplicationService.cs](src/ISEStudio/Integration/ABoxApplicationService.cs#L24-L60)
的内部 helper(`DeserializeBody<T>` / `ExtractIriFromBody` / `QueryString` / `QueryInt`)在
conflicts slice 推进时合并到共享文件,**试点代码迁移而非保留两份**。

### 6.2 DTO 命名空间按 slice 分目录

每个 slice 的 DTO 搬到自己子目录:

- `ISEStudio.Application.Conflicts`
- `ISEStudio.Application.Documents`
- `ISEStudio.Application.Releases`
- `ISEStudio.Application.Vocabulary`
- `ISEStudio.Application.Ontology`(已存在)
- `ISEStudio.Application.Extraction`
- `ISEStudio.Application.EntityResolution`
- `ISEStudio.Application.History`
- `ISEStudio.Application.Prompts`
- `ISEStudio.Application.External`
- `ISEStudio.Application.Published`
- `ISEStudio.Application.Providers`
- `ISEStudio.Application.Settings`
- `ISEStudio.Application.Authentication`(auth + tokens + mcp_tokens 共用)
- `ISEStudio.Application.Knowledge`
- `ISEStudio.Application.Rdf`(rdf.import)

### 6.3 detect-style fanout 提到 orchestrator

`conflicts.detect` 这种 "svc.X → agent.Triage → structure.Attach" 多步链抽出
`<Slice>XxxOrchestrator` 类。`IABoxApplicationService` 没有这种多步链,
所以试点本身不需要调整,但后续 conflicts slice 是该模式首次落地。