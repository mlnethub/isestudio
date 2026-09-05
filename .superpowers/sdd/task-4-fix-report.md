# 任务 4 缺陷修复报告

## 状态

已完成并提交。

修复提交 hash：`f665a05347e0fa1da784cd59830dd4a3201352c4`

## 缺陷复现

先运行：

```powershell
dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Neighborhood
```

原有邻域测试基线为 6/6 通过。新增回归测试后，在修复前得到 10 个测试中 3 个失败：

- 反向递归邻居未被发现。
- 只有通过跨知识库类型 ID 连接时，实体错误解析出外部类型键 `foreign-type`。
- JSON value 场景在修复前已正确排除。

失败结果与两个审查缺陷一致。

## 实现

仅修改了任务 4 范围内的 `GraphStore.cs` 和 `GraphStoreTests.cs`，未修改 API、schema、迁移、前端或 `GraphContracts.cs`。

`GetNeighborhoodAsync` 的递归 CTE 现在：

- 保存当前实体 ID，并从当前实体匹配事实的 subject 或 object 两端。
- 仍从 root 的 subject facts 建立深度 1 锚点，保持既有 root 回环语义。
- 对事实、subject 实体和下一实体均限定 `knowledge_system_id`。
- 继续保留深度上限、UUID 路径环路排除、事实去重、时态过滤和 `IncludeInvalidated` 行为。
- 仅对 entity facts 递归，`object_entity_id IS NOT NULL` 继续排除 JSON value 邻接。

实体投影的 `GraphEntities` 与 `EntityTypes` 左连接新增同知识库限定：

```csharp
.Where(item => item.KnowledgeSystemId == query.KnowledgeSystemId)
```

新增或调整集成测试覆盖：

- 反向递归发现相邻实体。
- 双向遍历事实去重并排除回到 root 的环边。
- JSON value 不作为邻接实体。
- 实体类型不跨知识库解析。

## 测试结果

聚焦邻域测试：

```powershell
dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Neighborhood
```

结果：10/10 通过。

完整 GraphStore 测试：

```powershell
dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests
```

结果：16/16 通过。

提交前另执行 `git diff --check`，无空白错误。

## 剩余疑问

无。
