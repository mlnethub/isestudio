# Task 5 报告：迁移和性能验收基线

## 范围

- 修改 `src/ISEStudio.IntegrationTests/Graph/GraphSchemaTests.cs`
- 新建 `src/ISEStudio.IntegrationTests/Graph/GraphPerformanceTests.cs`
- 修改 `src/ISEStudio.IntegrationTests/Graph/PostgresGraphFixture.cs`
- 修改 `docs/architecture.md`
- 未修改前端、Rust 源码、生产图查询/写入实现或迁移文件

现有 `20260904151546_AddKnowledgeGraph` 迁移已经包含 Task 5 所需的 active subject/object 部分索引、facts 检查约束和图表外键，因此没有追加迁移。

## RED 证据

命令：

```powershell
dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter "FullyQualifiedName~GraphSchemaTests|FullyQualifiedName~GraphPerformanceTests" --no-restore
```

结果：首先因 Task 5 所需 fixture 能力不存在而编译失败，缺少 `GetForeignKeysAsync`、`ResetGraphWritesAsync`、`CreateTraversalFixtureAsync` 和 `BuildServices`。这证明新增验收测试没有被已有辅助代码意外满足。

## 实现

- `GraphSchemaTests` 新增 PostgreSQL `numeric`/`timestamp with time zone` 断言，覆盖 `confidence`、`valid_from`、`valid_to`、`recorded_at`。
- 新增完整图外键集合断言，覆盖知识系统、类型层、实体、事实、自引用 supersession、证据、chunk 和 conflict 连接。
- `PostgresGraphFixture` 新增参数化外键元数据读取、图写入清理、确定性 1000-fact/5-hop 数据构造和 scoped `GraphStore` 服务构造。
- `GraphPerformanceTests` 新增 1000 条事实的 5-hop 邻域两秒性能守卫。
- `docs/architecture.md` 增加 PostgreSQL 图账本权威性、仅追加事实、失效/替代保留历史、有界递归 CTE、知识系统隔离和 Oxigraph 边界说明。

## GREEN 证据

Task 5 窄测试：

```powershell
dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter "FullyQualifiedName~GraphSchemaTests|FullyQualifiedName~GraphPerformanceTests" --no-restore
```

结果：6/6 通过，包含 5 个 schema 测试和 1 个性能测试。

完整图核心验证：

```powershell
dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ISEStudio.IntegrationTests.Graph --no-restore
```

结果：25/25 通过。

计划要求的完整构建：

```powershell
dotnet build src\ISEStudio.sln --no-restore -warnaserror
```

结果：未通过，但失败与 Task 5 无关：

- `ISEStudio.OxigraphProbe` 缺少 `obj/project.assets.json`，需要还原依赖。
- `ISEStudio.Tests` 存在既有 nullable/xUnit analyzer 错误：`HistoryServiceTests.cs`、`ReleaseServiceTests.cs`、`ExportArtifactStoreTests.cs`。

`git diff --check` 通过。

## 独立自审

- 事务：fixture 批量造数在单个 PostgreSQL 事务中提交；生产 `GraphStore` 的事实事务代码未改动。
- 知识系统隔离：造数和 schema 外键均使用 `KnowledgeSystemId`；邻域查询及其既有测试仍覆盖跨知识系统隔离。
- 时态：既有 Task 4 的 `valid_from`/`valid_to` 过滤未改动；新增测试确认数据库列为 PostgreSQL `timestamp with time zone`。
- 外键：新增断言覆盖迁移中全部图核心外键，且与实际 TPC 单数表名对齐。
- 参数化 SQL：fixture 的 `@ks`、`@ids`、`@padding`、链节点和元数据表参数均使用 Npgsql 参数；没有拼接用户输入。
- 性能样本：1000 条事实包含 5 条实体链和 995 条 JSON 事实，避免重复边制造非目标性的递归组合爆炸；性能断言只测邻域调用本身。

## 提交

- 实现提交：`466c549` (`test(graph): verify postgres traversal constraints`)
- 报告和进度账本提交：`d68c3e6` (`docs(sdd): record task 5 graph validation`)

## 剩余风险

- 两秒阈值受 Docker Desktop、宿主机负载和首次数据库连接影响；本次容器运行中通过，但不是跨机器 SLA。
- 性能样本的 995 条 JSON 事实不会进入实体邻域递归，后续应在真实数据规模基准中补充高基数但受控的实体边分布。
- solution 全量构建仍受未还原项目和无关既有诊断阻断，未修改这些无关问题。
