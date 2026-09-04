# 任务 3 报告：事务性图写入与证据链

## 范围

- 实现 `GraphStore.RecordFactAsync` 的单事务事实/证据/审计写入。
- 实现 `GraphStore.InvalidateFactAsync` 的同知识系统失效与审计写入。
- 新增 `AddGraphStore()` DI 注册并在 `Program.cs` 接入。
- 新增 PostgreSQL 集成测试 `GraphStoreTests`，覆盖成功写入与无效 chunk 回滚。

## 红绿证据

### RED

命令：

```powershell
Set-Location 'E:\GitHub\ontopilot\.worktrees\ultpio-graph-core\src'
dotnet test ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Record_fact
```

结果：FAIL

关键失败：

- `GraphStoreTests.cs(121,18): error CS1061: “ServiceCollection”未包含“AddGraphStore”的定义...`

说明：红测确认缺口是 `GraphStore` 的 DI/实现尚不存在，而不是行为已存在。

### GREEN

命令：

```powershell
Set-Location 'E:\GitHub\ontopilot\.worktrees\ultpio-graph-core\src'
dotnet test ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Record_fact
```

结果：PASS

摘要：`总计: 2, 失败: 0, 成功: 2, 已跳过: 0`

## 测试结果

- `Record_fact_persists_fact_and_evidence_in_one_transaction` 通过。
- `Record_fact_rolls_back_when_evidence_references_unknown_chunk` 通过。

## 改动文件

- `src/ISEStudio/Graph/GraphStore.cs`
- `src/ISEStudio/Infrastructure/Startup/GraphServiceCollectionExtensions.cs`
- `src/ISEStudio/Program.cs`
- `src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs`

## 自检

- `AuditEventEntity` 真实字段为 `Action` / `Summary` / `Detail`，无 `EventType`；实现与测试均改为断言 `Action == "graph.fact.recorded"`。
- `RecordFactAsync` 先执行 `command.Validate()`，再在显式事务内校验知识系统、主语、谓词和实体对象的 `KnowledgeSystemId` 归属。
- 事实、证据、审计通过一次 `SaveChangesAsync()` 写入，并仅在成功后 `CommitAsync()`。
- 无效 `SourceChunkId` 不做预校验，保留数据库 FK 失败路径；失败时事务未提交，因此不会残留事实、证据或审计行。
- `CreateEntityAsync` / `GetNeighborhoodAsync` 按任务范围保留 `NotSupportedException`，未越界实现任务 4 的邻域遍历。

## 问题

- 无阻塞问题。
- 测试过程中修正了两个测试辅助问题：`TestContext` 在当前 xUnit 版本不可用，且从已打开的 Npgsql 连接读取连接串会丢失密码；两者均已在测试内修复，不影响生产实现。

## 2026-09-05 子代理修复追加

### 范围

- 补齐 `RecordFactAsync` 对每个 `FactEvidenceInput.SourceChunkId` 的前置校验：chunk 必须存在，且其 `DocumentEntity.KnowledgeSystemId` 必须等于命令的 `KnowledgeSystemId`。
- 新增 PostgreSQL 集成测试，覆盖未知 chunk、跨知识系统 chunk、成功失效、重复失效、跨知识系统失效。
- 未修改计划文件、账本或其他无关代码。

### RED

命令：

```powershell
Set-Location 'E:\GitHub\ontopilot\.worktrees\ultpio-graph-core'
dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests
```

结果：FAIL

关键输出：

- `Record_fact_rejects_evidence_chunk_from_different_knowledge_system`: `Assert.Throws() Failure: No exception was thrown`
- `Invalidate_fact_marks_live_fact_in_same_knowledge_system_and_writes_audit`: PostgreSQL `timestamptz` 微秒精度导致断言精度不匹配，已将测试断言收紧到数据库实际精度。

说明：第一条失败直接证明 `RecordFactAsync` 在写入前没有校验证据 chunk 的知识系统归属；第二条不是实现缺陷，而是测试断言精度过严。

### GREEN

命令：

```powershell
Set-Location 'E:\GitHub\ontopilot\.worktrees\ultpio-graph-core'
dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests
```

结果：PASS

关键输出：

- `测试摘要: 总计: 5, 失败: 0, 成功: 5, 已跳过: 0`

### 改动

- `src/ISEStudio/Graph/GraphStore.cs`
	- 新增 `EnsureEvidenceChunksInKnowledgeSystemAsync(...)`。
	- `RecordFactAsync` 在事务内、实体入库前批量校验证据 chunk 是否存在且属于目标知识系统；失败时抛出 `InvalidOperationException`，因此不会留下事实、证据或审计残留。
- `src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs`
	- 将“未知 chunk”测试更新为前置校验异常而非数据库 FK 异常。
	- 新增“跨知识系统 chunk 被拒绝且无残留事实/证据/审计”测试。
	- 新增 `InvalidateFactAsync` 成功失效并写 `graph.fact.invalidated` 审计测试。
	- 新增已失效事实、跨知识系统事实的 `KeyNotFoundException` 测试。
	- 为 PostgreSQL 时间精度增加微秒截断辅助断言。

### 自检

- `RecordFactAsync` 现在会为每个唯一 `SourceChunkId` 执行 `Chunk -> Document -> KnowledgeSystemId` 归属校验。
- 缺失 chunk 与跨知识系统 chunk 都在 `SaveChangesAsync()` 之前失败，因此不会产生事实、证据或审计残留。
- `InvalidateFactAsync` 仍只允许失效同一知识系统内 `InvalidatedAt == null` 的事实；已失效或知识系统不匹配时抛出 `KeyNotFoundException`。
- 成功失效路径保留并验证 `graph.fact.invalidated` 审计记录。