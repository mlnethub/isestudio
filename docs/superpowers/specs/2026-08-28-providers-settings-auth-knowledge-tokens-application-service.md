# Providers / Settings / Auth / Knowledge / Tokens 应用服务抽取 + dispatcher 拆分(12/13)

**状态**: 已完成(12/13 slice 落地,2 commits,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `main`
**范围**: 33 个 dispatcher arms:
- `providers.*` × 5(list / create / update / delete / test)
- `settings.*` × 3(list_models / get / update)
- `auth.*` × 5 admin(update_me / list_users / create_user /
  update_user / delete_user;login / logout / me 3 个 inline stub 不动)
- `knowledge.*` × 12(list / create / delete / get / update /
  list_members / add_member / grantable_users / remove_member /
  member_detail / review_counts / refresh_stats)
- `tokens.*` × 4 + `mcp_tokens.*` × 3

从 `InternalOperationDispatcher` god-class 拆出 5 个应用服务:
`IProviderApplicationService`、`ISettingsApplicationService`、
`IAuthApplicationService`、`IKnowledgeApplicationService`、
`ITokenApplicationService`。接口定义在 `ISEStudio.Application.Integration`,
实现在 `ISEStudio.Integration`。

接续 [2026-08-28-external-published-application-service.md](2026-08-28-external-published-application-service.md)
11/13 slice,本切片是拆分启动以来最大的一块(33 arms / 5 服务),
按两个 commit 落地(`6bcaffb` + `55693b8`),并把 dispatcher 私有的
`DeserializeBody` / `DeserializeOptions` 彻底删除(最后一批使用者迁移)。

---

## 1. 背景

`InternalOperationDispatcher` 在 11/13 切片后 2929 行,其中 12/13
五块合计 ~723 行:

| 区块 | 行数 | 内容 |
|------|------|------|
| providers | ~130 | 5 helpers + ResolveProviderService |
| knowledge | ~205 | 12 helpers + ResolveKnowledgeService |
| auth admin | ~112 | 5 helpers + ResolveAuthService + ProjectUserOut |
| settings | ~67 | 3 helpers + ResolveSettingsService + 2 投影 |
| tokens + mcp_tokens | ~209 | 7 helpers + ResolveTokenManagementService + 4 投影 |

关键约束:

1. **所有 wire DTO 都在 Infrastructure 命名空间**
   (`ISEStudio.Providers` / `ISEStudio.Knowledge` /
   `ISEStudio.Authentication` / `ISEStudio.Settings`),不能进
   `ISEStudio.Application` → 接口签名统一
   `Task<object?>(InternalRequest, CancellationToken)`(7/13 extraction
   先例),输入 body DTO 在实现内 `DeserializeBody<T>` 解析。
2. **snake_case 投影** (`ProjectUserOut` / `ProjectSettings` /
   `ProjectModelCatalog` / `ProjectTokenOut` × 4)从 dispatcher 搬进
   各 app service 实现。
3. **auth.login / logout / me 三个 inline stub 留在 switch** —
   它们拥有 AuthSessionEntity + cookie plumbing,与本次 5 个
   admin CRUD 不同路径。
4. **dispatcher 私有 `DeserializeBody` 只服务 12/13 区块** —
   全部迁移后连 `DeserializeOptions` 一起删除。

## 2. 决策

### 2.1 按底层服务切 5 个接口,2 个 commit

**结论**:每服务一个接口,按 dispatcher 区块位置拆两个 commit:

- `6bcaffb`:providers(5)+ settings(3)+ auth(5)= 13 arms,
  3 接口 + 3 实现
- `55693b8`:knowledge(12)+ tokens/mcp_tokens(7)= 19 arms,
  2 接口 + 2 实现

### 2.2 全 `Task<object?>` 签名 + null 语义分层

**结论**:接口方法全部 `Task<object?>(InternalRequest, CancellationToken)`。

**null 语义**(与 11/13 一致的 wrapper 约定):
- app service 未注册(hand-built dispatcher 单测)→ `onMissing`
- app 返回 null(KS 缺失 / Guid 无效 / 行不存在)→ `onNull`
  (默认同 `onMissing`)
- throw 语义 1:1 搬进 app service:body 缺失 →
  `InvalidOperationException("Request body is required for X.")`;
  无效 Guid → `InvalidOperationException("... must be a valid UUID.")`

**需要 app 内部投影的 case**(两个 fallback 值不同的 arm):
- `tokens.revoke` / `mcp_tokens.revoke`:KS/Id 无效 → `{ok:false}`,
  行不存在 → 空 envelope → app 内部投影空 shape
- `tokens.reveal`:KS/Id 无效 → EmptyTokenRevealed,行不存在 →
  同样空 envelope → app 内部投影
- `knowledge.delete` / `remove_member` / `refresh_stats`:
  app 内部投影 `{deleted}` / `{removed}` / `{refreshed, item}`

### 2.3 fallback 映射(dispatcher 保留)

| arm | fallback |
|-----|----------|
| providers.list | `[]` |
| providers.create / update / test | `null`(旧 svc-null 语义) |
| providers.delete | `{ok: true}` |
| settings.list_models | EmptyModelCatalog |
| settings.get / update | EmptySettings |
| auth.update_me / create_user / update_user | EmptyUser |
| auth.list_users | `[]` |
| auth.delete_user | `{ok: false}` |
| knowledge.list / list_members / add_member / grantable_users | `[]` |
| knowledge.get / create / update | EmptyKnowledgeSystem |
| knowledge.delete | `{deleted: Guid.Empty}` |
| knowledge.remove_member | `{removed: Guid.Empty}` |
| knowledge.member_detail | EmptyMember |
| knowledge.review_counts | EmptyReviewCounts |
| knowledge.refresh_stats | `{refreshed: false}` |
| tokens.list | `[]` |
| tokens.create | EmptyTokenCreated |
| tokens.revoke | `{ok: false}` |
| tokens.reveal | EmptyTokenRevealed |
| mcp_tokens.list | EmptyListResponse |
| mcp_tokens.create | EmptyMcpTokenCreated |
| mcp_tokens.revoke | `{ok: false}` |

### 2.4 dispatcher 私有 `DeserializeBody` 删除

**结论**:12/13 是最后一批使用者;迁移后
`DeserializeOptions` + `DeserializeBody<T>` 一起删除。
`InternalRequestHelpers.DeserializeBody`(conflicts/documents 起一直
存在)成为唯一实现,app service 通过
`using static ISEStudio.Integration.InternalRequestHelpers;` 复用。

### 2.5 `settings.list_models` 签名统一

**结论**:`InvokeSettingsListModelsAsync(cancellationToken)` →
`InvokeSettingsListModelsAsync(request, cancellationToken)`,switch
arm 同步更新。接口统一带 `InternalRequest`(实现忽略 request),
wrapper 单一 shape。

## 3. 文件清单

### 新增(9)

| 文件 | 说明 |
|------|----|
| `ISEStudio.Application/Integration/IProviderApplicationService.cs` | 5-method 接口 |
| `ISEStudio.Application/Integration/ISettingsApplicationService.cs` | 3-method 接口 |
| `ISEStudio.Application/Integration/IAuthApplicationService.cs` | 5-method 接口 |
| `ISEStudio.Application/Integration/IKnowledgeApplicationService.cs` | 12-method 接口 |
| `ISEStudio.Application/Integration/ITokenApplicationService.cs` | 7-method 接口 |
| `ISEStudio/Integration/ProviderApplicationService.cs` | 委托 ProviderService |
| `ISEStudio/Integration/SettingsApplicationService.cs` | 委托 SettingsService,2 投影 |
| `ISEStudio/Integration/AuthApplicationService.cs` | 委托 AuthService,ProjectUserOut |
| `ISEStudio/Integration/KnowledgeApplicationService.cs` | 委托 KnowledgeService,3 投影 |
| `ISEStudio/Integration/TokenApplicationService.cs` | 委托 TokenManagementService,4 投影 |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | 五区块折叠为 5 wrapper + 33 个 1 行委托;删私有 DeserializeBody/DeserializeOptions |
| `src/ISEStudio/Program.cs` | +3 DI 注册(IAuth/ISettings/IToken,在 AuthService/TokenManagementService/SettingsService 旁) |
| `src/ISEStudio/Providers/ProviderServiceCollectionExtensions.cs` | +1 DI 注册(IProvider) |
| `src/ISEStudio/Knowledge/KnowledgeServiceCollectionExtensions.cs` | +1 DI 注册(IKnowledge) |

### dispatcher 行数

- 前:2929 行(11/13 后)
- 后:2526 行(12/13 后)
- 净变化 **-403 行**(diff:145 insertions / 424 deletions)

## 4. 验证

```
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告(每个 commit 后各一次)

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851(每个 commit 后)

$ dotnet test src/ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167(每个 commit 后)
```

零回归;33 个 fallback envelope 全部保留,throw 语义
(400/404/409)不变,wire shape 完全不变。

---

## 5. 后续切片(剩 1)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 13/13 rdf.import(终局 — dispatcher god-class 拆除收尾)

---

## 6. Decision Log

- 2026-08-28: 12/13 完成(2 commits)。
  本切片锁定「多服务批量拆分」模式:每服务一个接口,全
  `Task<object?>` 签名(Infrastructure DTO 不搬 Application),body
  unpacking + Guid 解析 + throw 语义 + snake_case 投影全部沉入 app
  service,dispatcher 只留 wrapper + fallback。dispatcher 私有
  `DeserializeBody` 退役,`InternalRequestHelpers.DeserializeBody`
  成为唯一实现。net dispatcher -403 行。
