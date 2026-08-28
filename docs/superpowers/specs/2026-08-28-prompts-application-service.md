# Prompts 应用服务抽取 + dispatcher → application-service 拆分(10/13)

**状态**: 已完成(10/13 slice 落地,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `main`
**范围**: 4 个 dispatcher arms:
- `prompts.list` (read)
- `prompts.update` (mutation + `RunWithExtractionGuardAsync` 409 守卫)
- `prompts.restore` (mutation + `RunWithExtractionGuardAsync` 409 守卫)
- `prompts.restore_all` (mutation + `RunWithExtractionGuardAsync` 409 守卫)

从 `InternalOperationDispatcher` god-class 拆出一个
`IPromptsApplicationService`(定义在 `ISEStudio.Application.Integration`,
实现在 `ISEStudio.Integration`)。把 3 个 wire DTO 从 `ISEStudio.Prompts`
搬到 `ISEStudio.Application.Prompts`(`PromptDef` catalog record 留在
原命名空间)。

接续 [2026-08-28-history-application-service.md](2026-08-28-history-application-service.md)
9/13 slice,本切片验证模板在「1 read + 3 mutation + 简单 wire DTO」组合
下的可用性,并顺带修复了一个与切片无关的测试基建 flake
(commit `cc6d7ba`,详见 §4)。

---

## 1. 背景

`InternalOperationDispatcher` 在 9/13 切片后 ~3371 行,其中 prompts
helpers 占 ~77 行(原 lines 835-911 的 1 个 DI helper + 4 个 helper),
承载:

- 1 个 read 端点:`prompts.list`(无 query 解析,合并静态 catalog +
  本 KS override rows)
- 3 个 mutation 端点:`prompts.update`(body `{content}` +
  `request.ResourceId` = prompt key)、`prompts.restore`(同路由,
  删 override row)、`prompts.restore_all`(清空本 KS 全部 override)

3 个 wire DTO 住在 `ISEStudio.Prompts`:`PromptOut`(10 字段,
snake_case JSON 名)/ `PromptListOut`(2 字段)/ `PromptUpdateIn`
(1 字段);`PromptDef`(catalog row,非 wire)同文件。

## 2. 决策

### 2.1 DTO 搬入 `ISEStudio.Application.Prompts`,PromptDef 留守

**结论**:搬 wire DTO,留 catalog record。

**实现细节**:
- 新增 `ISEStudio.Application/Prompts/PromptDtos.cs`(3 records)
- `ISEStudio/Prompts/PromptDtos.cs` 只留 `PromptDef`(加注释说明留守
  理由:catalog 是内部行,不是 wire shape)
- `PromptService.cs` 加 `using ISEStudio.Application.Prompts;`(加
  `PromptOut` / `PromptListOut` type alias 提升可读性)

### 2.2 应用服务接口 = 4 个 typed 方法

**结论**:`Task<T?>(InternalRequest, CancellationToken)` 签名,4 个方法:

```csharp
Task<PromptListOut?> ListAsync(InternalRequest, CancellationToken);
Task<PromptOut?>      UpdateAsync(InternalRequest, CancellationToken);
Task<PromptOut?>      RestoreAsync(InternalRequest, CancellationToken);
Task<int>             RestoreAllAsync(InternalRequest, CancellationToken);
```

`RestoreAllAsync` 返回 `Task<int>`(删除行数),dispatcher 投影
`EmptyPromptList()` 固定 shape(与 9/13 `RevokeDecisionAsync` 的
`Task<Guid?>` 模式同类)。

### 2.3 dispatcher arm 不动,4 个 helper 全部 1 行委托

**结论**:4 个 `InvokePrompts*Async` helper 都缩成 1 行委托。

**实现细节**:
- 新增 `ResolvePromptsAppService()` 1 行 + `InvokePromptsAsync`
  shared wrapper(与 9/13 `InvokeHistoryAsync` 同构)
- 4 个 helper 都通过 wrapper,每个 1 行委托
- 删 `ResolvePromptService()`(旧 typed facade helper,完全清理)

### 2.4 守卫包装 (`RunWithExtractionGuardAsync`) 留在 dispatcher arm 上(沿用 8/13 §2.4)

3 个 mutation arm 在 dispatcher switch arm 层仍然 wrap
`RunWithExtractionGuardAsync`,应用服务不实现 extraction guard。

### 2.5 dispatcher 跨 slice shim: 无

**结论**:`ResolvePromptService` 完全删掉,没有留 shim。

**理由**:
- 没有 typed facade 绕过 dispatcher 调用 `PromptService`(grep
  `IIntegrationApiFacade` + `PromptService` 无匹配)。
- 4 个 arm 都通过 dispatcher,折叠到 app service 内部直接
  `_prompts.ListAsync(...)` / `_prompts.UpdateAsync(...)` 等。

### 2.6 body 反序列化复用 `InternalRequestHelpers.DeserializeBody`

**结论**:`DeserializeBody<PromptUpdateIn>(request)` 复用
`InternalRequestHelpers`(与 conflicts/documents 切片同)。

**实现细节**:
- app service 内 `DeserializeBody<PromptUpdateIn>(request)` +
  `string.IsNullOrWhiteSpace(body.Content)` 校验,空 content 抛
  `ValidationException("content must not be empty")`(与 dispatcher
  旧行为一致,→ HTTP 400)

### 2.7 `Guid.TryParse` + `KeyNotFoundException` 404 保留

prompt key 是 `request.ResourceId` 字符串(非 Guid),未知 key 由
`PromptCatalog.Find` 抛 `KeyNotFoundException`(→ HTTP 404 via
`FastApiErrorMiddleware`),app service 不额外校验。

## 3. 文件清单

### 新增

| 文件 | 行 | 说明 |
|------|----|----|
| `src/ISEStudio.Application/Prompts/PromptDtos.cs` | 30 | 3 wire records |
| `src/ISEStudio.Application/Integration/IPromptsApplicationService.cs` | 60 | 4-method 接口 |
| `src/ISEStudio/Integration/PromptsApplicationService.cs` | 90 | 4 methods |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | prompts section 77 行 → 78 行(-1 行,注释重写) |
| `src/ISEStudio/Prompts/PromptDtos.cs` | 删 3 wire records,留 PromptDef + 留守注释 |
| `src/ISEStudio/Prompts/PromptService.cs` | +1 using + 2 type alias |
| `src/ISEStudio/Prompts/PromptServiceCollectionExtensions.cs` | +6 行(DI 注册) |

### dispatcher 行数

- 前:3371 行(9/13 后)
- 后:~3370 行(10/13 后)
- 净变化 **-1 行**(注释膨胀与 helper 缩成 1 行委托基本抵消)

## 4. 顺带修复: RBAC matrix extract 行 409/200 flake(commit `cc6d7ba`)

与 prompts 切片无关的测试基建 flake,根因与修复:

- **根因**: `EndpointRoleMatrixTests` 是独立 xUnit collection,与
  `ExtractionTestCollection` 并行运行。后者把 fake chat client 装进
  进程级单例 `FakeChatClientFactory.Default`;matrix 的 extract 行
  (`POST /extract-all` / `POST /extract-instances`)走真实 orchestrator,
  当并行测试恰好装有 client 时 → 真建 job row(200 而非 pinned 500)
  → pending job 跨 KS 泄漏 → 后续 mutation 行被
  `FindAnyActiveJobAsync` 409。
- **修复**(纯测试侧,零生产行为变更):
  1. matrix 类加入 `[Collection(ExtractionTestCollection.Name)]`,
     与 factory 修改者串行;
  2. seed 阶段 `FakeChatClientFactory.Default.Reset()`,extract 行
     确定性在 job 插入前抛异常(→ 500 pinned)。
- **验证**: 全量单测连续两次 850/850 绿(此前偶发 3 失败),167/167
  contract 绿。

## 5. 验证

```
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj (x2)
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851

$ dotnet test src/ISEStudio.ApiContract.Tests/...
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167
```

零回归;`RunWithExtractionGuardAsync` 守卫保持 409 + job_id envelope
行为,`EmptyPrompt()` / `EmptyPromptList()` fallback envelopes 全部
保留,wire shape 完全不变。

---

## 6. 后续切片(剩 3)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 11/13 external + published (free `ResolveExternalOntologyService` + `ParseExportFormat` shim)
- [ ] 12/13 providers + settings + auth + knowledge + tokens + mcp_tokens
- [ ] 13/13 rdf.import

每个切片都会复用本切片定下的 4 段模式:
1. DTO 搬入 `ISEStudio.Application.{Prompts,...}`
2. `IXxxApplicationService`: `Task<T?>(InternalRequest, CancellationToken)`
3. dispatcher arm 不动,helper 缩成 1 行委托
4. 守卫包装留在 arm 上,不沉到 app service

---

## 7. Decision Log

- 2026-08-28: 10/13 prompts slice 完成。
  本切片锁定 4-arms(1 read + 3 mutation)+ `RunWithExtractionGuardAsync`
  守卫 + wire DTO 搬迁(内部 catalog record 留守原命名空间)的拆分模式。
  `ResolvePromptService` 完全清理(无 typed facade 引用)。
  `InternalRequestHelpers.DeserializeBody` 复用,无私有 helper 复制。
  net dispatcher -1 行。
  顺带修复 RBAC matrix extract 行 flake(commit `cc6d7ba`,测试基建
  专属,生产行为零变更)。
