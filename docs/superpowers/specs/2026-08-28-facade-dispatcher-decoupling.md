# facade↔dispatcher 相互引用解除

**状态**: 已完成(commit 7816d95,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `main`
**范围**: `IIntegrationApiFacade` / `IInternalOperationDispatcher` /
3 个 SPARQL query arms / 2 个 app service

dispatcher 拆分工作流(13/13,2026-08-28 闭环)的收尾项之一:拆除
facade 与 dispatcher 之间的相互引用,使
`IInternalOperationDispatcher` 回归纯 routing table。

---

## 1. 背景

拆分完成后的依赖图存在一个相互引用:

```text
IntegrationApiFacade ──(ctor)──▶ IInternalOperationDispatcher
      ▲                                │
      └──(resolve at runtime)──────────┘
          dispatcher 的 3 个 query arm
          (external.query / published.query / published.release.query)
          经 IServiceProvider 解析 IIntegrationApiFacade
          再调 typed QueryAsync
```

虽然运行时不会无限递归(typed `QueryAsync` 直连
`ISparqlQueryExecutor` 不回调 dispatcher),但这是双向耦合,且
dispatcher 接口还背着 3 个 typed 方法:

- `GetOntologyAsync(long)` — Stage 1 空占位(MCP `get_ontology`
  硬编码 1L 调用)
- `GetOntologyAsync(Guid)` — 真实实现,但走 `ResolveOntologyService`
  shim 直连 `OntologyService`,与 6/13 的 `ontology.get` arm
  (`IOntologyApplicationService.GetAsync`)重复实现
- `PreviewOntologyChangesAsync` — Stage 1 空占位(MCP
  `preview_ontology_changes` 调用)

## 2. 决策

### 2.1 facade 的 typed 方法不再转发 dispatcher

**结论**:`IntegrationApiFacade` 构造改为
`(IInternalOperationDispatcher, ISparqlQueryExecutor,
IOntologyApplicationService)`:

| 方法 | 新实现 |
|------|--------|
| `GetOntologyAsync(Guid)` | 构造 InternalRequest 调 `IOntologyApplicationService.GetAsync`,null → `KeyNotFoundException`(语义与旧 dispatcher 版一致) |
| `GetOntologyAsync(long)` | 保持空占位(MCP 1L 调用者不变) |
| `PreviewOntologyChangesAsync` | 保持空占位 |
| `QueryAsync` | 不变(直连 executor) |
| `InvokeAsync` | 不变(转发 dispatcher) |

facade 留在 `ISEStudio.Application`(零 ProjectReference 保持)——
`IOntologyApplicationService` 和 `ISparqlQueryExecutor` 都在
Application 项目内。

### 2.2 `IInternalOperationDispatcher` 删 3 个 typed 方法

**结论**:接口只剩 `InvokeAsync`,dispatcher 删掉 3 个实现 +
`EmptyOntologyResponseAsync` + `ResolveOntologyService` shim。
MCP 不受影响(facade 接口签名不变)。

### 2.3 3 个 SPARQL query arms 搬进 app service

**结论**:`external.query` → `IExternalApplicationService.QueryAsync`;
`published.query` / `published.release.query` →
`IPublishedApplicationService.QueryAsync`(两个 arm 共享一个方法,
与 published 其他 op 一致)。两个实现各自注入
`ISparqlQueryExecutor` 直连,不再经过 facade。

语义 1:1:

- `public_id` / body / query 文本缺失 → 返回 null → dispatcher
  fallback `EmptyQueryResponse()`(`{rows: []}`)
- `max_rows` 默认 1000,并保留 facade 路径的
  `Math.Clamp(maxRows, 1, 10_000)`(executor 本身不 clamp)
- `TokenPrincipal(TokenId: Actor.UserId, KnowledgeSystemPublicId:
  PublicId, Scopes: [])` 构造搬入 app service
- read-only SPARQL policy 仍在 executor 内(controller 预校验 +
  executor 兜底不变)

dispatcher 侧:旧 `InvokeExternalQueryAsync` 实现删除,换成两个
1 行委托(wrapper 惯例,`onMissing: EmptyQueryResponse`);
`InvokePublishedQueryAsync` 新增。

### 2.4 dispatcher 遗留死代码清理

**结论**:随本次重构一并删除:
`ResolveRdfExportService`(6/13 后无使用者)+
"shared envelope helpers" 区块的 7 个
`InternalRequestHelpers` 转发 shim(QueryString / QueryInt /
ExtractPayload / ExtractChunkIds / ExtractBodyIri / ResolveKsAsync /
ResolveKsByPublicIdAsync,13/13 后无使用者)。

## 3. 文件清单

### 修改(8)

| 文件 | 改动 |
|------|----|
| `ISEStudio.Application/Integration/IntegrationApiFacade.cs` | 构造 +1 IOntologyApplicationService;Guid 版 GetOntologyAsync 改走 app service;空 OntologyResponse 工厂搬入 |
| `ISEStudio.Application/Integration/IInternalOperationDispatcher.cs` | 删 3 个 typed 方法,只剩 InvokeAsync |
| `ISEStudio.Application/Integration/IExternalApplicationService.cs` | +QueryAsync,更新"query 留 dispatcher"注释 |
| `ISEStudio.Application/Integration/IPublishedApplicationService.cs` | +QueryAsync,同上 |
| `ISEStudio/Integration/ExternalApplicationService.cs` | +executor 注入 + QueryAsync 实现 |
| `ISEStudio/Integration/PublishedApplicationService.cs` | +executor 注入 + QueryAsync 实现 |
| `ISEStudio/Integration/InternalOperationDispatcher.cs` | 删 3 typed 实现 + shim + 死代码;query arms 换 1 行委托 |
| `ISEStudio.ApiContract.Tests/Baseline/FacadeSmokeTests.cs` | 构造 +NullOntologyApplicationService stub |

### dispatcher 行数

- 前:2447 行(13/13 后)
- 后:2315 行
- 净变化 **-132 行**

## 4. 验证

```text
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851

$ dotnet test src/ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167
```

零回归;query arm 的 wire shape、throw 语义(400/404/409)、
max_rows clamp、MCP typed 表面全部不变。

## 5. 后续

- 依赖图现在为:`IntegrationApiFacade → dispatcher`(单向)、
  `dispatcher → 14 个 app service`、`app service → 领域服务 /
  ISparqlQueryExecutor`。无环。
- 两个延期决策(guard 下沉 / controller 直连)经评估均维持现状
  (见 dispatcher-split-workflow spec §5 与 memory)。

## 6. Decision Log

- 2026-08-28: 7816d95。facade 的 typed 方法直连
  IOntologyApplicationService / executor,query arms 沉入 external /
  published app service,dispatcher 接口回归纯 routing table,
  相互引用解除,死代码清理 -144 行。
