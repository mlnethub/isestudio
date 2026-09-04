# ISEStudio PostgreSQL 图核心实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 ISEStudio 中实现由 PostgreSQL 持久化的知识库图核心，使实体、类型、关系、双时态事实和证据链可被事务性写入及有界查询。

**Architecture:** 扩展现有 `ISEStudioDbContext`，以独立实体映射将图表加入已有 ISEStudio PostgreSQL schema。应用服务通过 `IGraphStore` 隔离 EF Core/Npgsql；复杂邻域读取使用参数化递归 CTE。图事实、溯源和审计在单个数据库事务中提交，Oxigraph 不参与运行时事实存储。

**Tech Stack:** .NET 10、ASP.NET Core、EF Core 10、Npgsql、PostgreSQL 16、pgvector、xUnit、Testcontainers.PostgreSql。

## 全局约束

- 新代码位于 `E:\GitHub\ontopilot`；后端和前端的目标命名空间均使用 `ISEStudio`，不用 `Utopia`。
- `ISEStudio` 为唯一 ASP.NET Core 宿主；`ontopilot/frontend` 为唯一 Web UI 宿主。
- PostgreSQL 是图事实、本体、溯源、时态、治理、审计和向量嵌入的唯一权威存储。
- 图事实是仅追加的 SPO 断言；失效和替代须记录，不得物理删除历史事实。
- 运行时图遍历必须通过参数化、深度受限、知识库范围受限的递归 CTE 执行。
- SQLite 可继续用于不依赖 PostgreSQL 语义的宿主单元测试；图 schema、递归 CTE、约束、索引和时态语义必须使用 PostgreSQL Testcontainers 集成测试验证。
- 不承诺保持 Rust HTTP API 的响应兼容；本计划不新增 API 端点和 React 页面。
- 不引入第二个运行时图数据库；Oxigraph/dotNetRDF 仅用于后续 RDF/OWL 导入边界。

---

## 文件结构

| 路径 | 职责 |
| --- | --- |
| `src/ISEStudio/Graph/GraphContracts.cs` | 图领域不可变记录类型、枚举、写入和读取接口。 |
| `src/ISEStudio/Graph/GraphStore.cs` | `IGraphStore` 的 EF Core/Npgsql 实现；事务性命令和参数化邻域查询。 |
| `src/ISEStudio/Infrastructure/Persistence/Entities/GraphEntities.cs` | EF Core 图持久化实体。 |
| `src/ISEStudio/Infrastructure/Persistence/Configurations/GraphEntityConfigurations.cs` | 图表、约束、索引与 PostgreSQL 列类型映射。 |
| `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs` | 图实体的 `DbSet` 和 PostgreSQL 特定类型注册。 |
| `src/ISEStudio/Infrastructure/Persistence/Migrations/<timestamp>_AddKnowledgeGraph.cs` | 由 EF Core 生成、审阅后的图 schema 迁移。 |
| `src/ISEStudio/Infrastructure/Startup/GraphServiceCollectionExtensions.cs` | `IGraphStore` 依赖注入注册。 |
| `src/ISEStudio/Program.cs` | 调用图模块服务注册，复用现有 `--migrate` 入口。 |
| `src/ISEStudio.IntegrationTests/Graph/PostgresGraphFixture.cs` | 基于 Testcontainers 的迁移后 PostgreSQL fixture。 |
| `src/ISEStudio.IntegrationTests/Graph/GraphSchemaTests.cs` | 表、外键、唯一约束、部分索引和 PostgreSQL 类型断言。 |
| `src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs` | 事实账本、时态过滤、证据与递归遍历行为测试。 |
| `docs/architecture.md` | 说明 PostgreSQL 图的权威性与模块边界。 |

## 迁移源映射

第一批只移植 Rust 的 `migrations/0003_graph.sql` 中支撑核心读写的概念：`entity_types`、`relation_types`、`relation_type_domains`、`relation_type_ranges`、`entity_type_parents`、`entities`、`facts`、`fact_evidence` 与 `fact_conflicts`。`entity_type_disjoint`、`entity_retypes`、`fact_adoptions`、`extraction_drops`、本体导入、实体消解、推理和待审核事实由后续独立计划交付。

现有 ISEStudio `KnowledgeSystemEntity` 是此阶段的知识库根实体。新图表外键统一命名为 `KnowledgeSystemId` 并指向 `KnowledgeSystems(Id)`；不得新建并行的 `knowledge_bases` 根表。

### 任务 1：建立图领域契约

**文件：**
- 新建：`src/ISEStudio/Graph/GraphContracts.cs`
- 新建：`src/ISEStudio.Tests/Graph/GraphContractsTests.cs`

**接口：**
- 产出：`GraphObjectKind`、`FactStatus`、`GraphEntity`、`GraphFact`、`GraphFactEvidence`、`CreateEntityTypeCommand`、`CreateRelationTypeCommand`、`RecordFactCommand`、`GraphNeighborhoodQuery`、`GraphNeighborhood`、`IGraphStore`。
- `IGraphStore` 必须声明：

```csharp
public interface IGraphStore
{
    Task<GraphEntity> CreateEntityAsync(CreateGraphEntityCommand command, CancellationToken cancellationToken);
    Task<GraphFact> RecordFactAsync(RecordFactCommand command, CancellationToken cancellationToken);
    Task InvalidateFactAsync(Guid knowledgeSystemId, Guid factId, DateTimeOffset invalidatedAt, CancellationToken cancellationToken);
    Task<GraphNeighborhood> GetNeighborhoodAsync(GraphNeighborhoodQuery query, CancellationToken cancellationToken);
}
```

- `RecordFactCommand` 必须包含 `KnowledgeSystemId`、`SubjectEntityId`、`PredicateId`、`ObjectKind`、`ObjectEntityId`、`ObjectValue`、`Confidence`、`ValidFrom`、`ValidTo`、`RecordedAt`、`Evidence` 和 `ActorId`。

- [ ] **步骤 1：写入失败的契约测试**

```csharp
using ISEStudio.Graph;

namespace ISEStudio.Tests.Graph;

public sealed class GraphContractsTests
{
    [Fact]
    public void Record_fact_command_requires_exactly_one_object_representation()
    {
        var command = new RecordFactCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), GraphObjectKind.Entity,
            Guid.NewGuid(), "must-not-be-present", 0.9m, null, null,
            DateTimeOffset.UtcNow, [], null);

        Assert.Throws<ArgumentException>(() => command.Validate());
    }
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~GraphContractsTests`

预期：编译失败，提示缺少 `ISEStudio.Graph` 类型。

- [ ] **步骤 3：实现最小领域契约和输入校验**

```csharp
namespace ISEStudio.Graph;

public enum GraphObjectKind { Entity, JsonValue }
public enum FactStatus { Live, Invalidated, Superseded }

public sealed record RecordFactCommand(
    Guid KnowledgeSystemId, Guid SubjectEntityId, Guid PredicateId,
    GraphObjectKind ObjectKind, Guid? ObjectEntityId, string? ObjectValue,
    decimal Confidence, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt, IReadOnlyList<FactEvidenceInput> Evidence, Guid? ActorId)
{
    public void Validate()
    {
        var entityObject = ObjectEntityId.HasValue;
        var valueObject = !string.IsNullOrWhiteSpace(ObjectValue);
        if ((ObjectKind == GraphObjectKind.Entity && (!entityObject || valueObject)) ||
            (ObjectKind == GraphObjectKind.JsonValue && (entityObject || !valueObject)))
        {
            throw new ArgumentException("A fact must have exactly one object representation.");
        }
        if (Confidence is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(Confidence));
        }
        if (ValidFrom is not null && ValidTo is not null && ValidFrom > ValidTo)
        {
            throw new ArgumentException("ValidFrom cannot be after ValidTo.");
        }
    }
}
```

同时定义接口块列出的所有类型；所有输出记录都使用 `Guid`、`DateTimeOffset` 和 `decimal`，而非数据库实体类型。

- [ ] **步骤 4：运行测试确认通过**

运行：`dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~GraphContractsTests`

预期：PASS。

- [ ] **步骤 5：提交**

```powershell
git add src/ISEStudio/Graph/GraphContracts.cs src/ISEStudio.Tests/Graph/GraphContractsTests.cs
git commit -m "feat(graph): add graph domain contracts"
```

### 任务 2：映射最小 PostgreSQL 图 schema

**文件：**
- 新建：`src/ISEStudio/Infrastructure/Persistence/Entities/GraphEntities.cs`
- 新建：`src/ISEStudio/Infrastructure/Persistence/Configurations/GraphEntityConfigurations.cs`
- 修改：`src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs`
- 新建：`src/ISEStudio.IntegrationTests/Graph/PostgresGraphFixture.cs`
- 新建：`src/ISEStudio.IntegrationTests/Graph/GraphSchemaTests.cs`

**接口：**
- 消费：任务 1 的 `GraphObjectKind`。
- 产出：`EntityTypeEntity`、`RelationTypeEntity`、`RelationTypeDomainEntity`、`RelationTypeRangeEntity`、`EntityTypeParentEntity`、`GraphEntityEntity`、`FactEntity`、`FactEvidenceEntity`、`FactConflictEntity`。
- `FactEntity` 必须同时具有 `ObjectEntityId` 和 `ObjectValue` 可空列，并由 PostgreSQL `CHECK` 保证仅一个可用。

- [ ] **步骤 1：写入失败的 PostgreSQL schema 测试**

```csharp
[Fact]
public async Task Graph_migration_creates_fact_ledger_constraints()
{
    var tables = await _fixture.GetTableNamesAsync();
    Assert.Contains("facts", tables);
    Assert.Contains("fact_evidence", tables);

    await using var command = _fixture.CreateCommand(@"
        INSERT INTO facts (id, knowledge_system_id, subject_entity_id, predicate_id,
                           object_entity_id, object_value, confidence, recorded_at)
        VALUES (gen_random_uuid(), @ks, @subject, @predicate,
                @object, '{""bad"":true}'::jsonb, 0.8, now())");
    command.Parameters.AddWithValue("ks", _fixture.KnowledgeSystemId);
    command.Parameters.AddWithValue("subject", _fixture.SubjectEntityId);
    command.Parameters.AddWithValue("predicate", _fixture.PredicateId);
    command.Parameters.AddWithValue("object", _fixture.ObjectEntityId);

    var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    Assert.Equal("23514", error.SqlState);
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphSchemaTests`

预期：FAIL，提示 `facts` 表不存在。Docker 必须可用。

- [ ] **步骤 3：添加实体、映射和 DbSet**

在 `GraphEntities.cs` 中以每个物理表一个密封实体类型定义持久化行。每个实体以 `Guid Id` 为主键，连接表以复合主键；`FactEntity` 使用下列关键字段：

```csharp
public sealed class FactEntity
{
    public Guid Id { get; set; }
    public Guid KnowledgeSystemId { get; set; }
    public Guid SubjectEntityId { get; set; }
    public Guid PredicateId { get; set; }
    public Guid? ObjectEntityId { get; set; }
    public string? ObjectValue { get; set; }
    public decimal Confidence { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
    public Guid? SupersedesFactId { get; set; }
}
```

在 `GraphEntityConfigurations.cs` 中：
- 使用 `ToTable` 显式命名为小写 snake_case 表名。
- 对 `facts` 添加 `CHECK ((object_entity_id IS NULL) <> (object_value IS NULL))`、`CHECK (confidence >= 0 AND confidence <= 1)` 和 `CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_from <= valid_to)`。
- 将 `ObjectValue` 配置为 PostgreSQL `jsonb`，`RecordedAt` 配置为 `timestamptz`。
- 为 `facts(knowledge_system_id, subject_entity_id)`、`facts(knowledge_system_id, object_entity_id)` 建立仅包含 `invalidated_at IS NULL` 的部分索引；为 `fact_evidence(fact_id)` 建立索引。
- 使用真实外键连接 `KnowledgeSystems`、实体、关系类型、事实和文档/分块；所有图子表删除时级联，但历史事实不得由应用代码删除。

在 `ISEStudioDbContext` 添加对应 `DbSet`。修改 `ApplyPostgresColumnTypes`，将 `FactEntity.ObjectValue` 注册为 `jsonb`。以现有 `InitialCompatibility` 的命名风格运行 `dotnet ef migrations add AddKnowledgeGraph --project src/ISEStudio --startup-project src/ISEStudio`，审阅生成迁移，确保迁移中含 `CREATE TABLE`、检查约束与部分索引。

- [ ] **步骤 4：运行 schema 测试确认通过**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphSchemaTests`

预期：PASS，Docker PostgreSQL 中存在图表，且不合法双对象事实被 SQLSTATE `23514` 拒绝。

- [ ] **步骤 5：提交**

```powershell
git add src/ISEStudio/Infrastructure/Persistence/Entities/GraphEntities.cs src/ISEStudio/Infrastructure/Persistence/Configurations/GraphEntityConfigurations.cs src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs src/ISEStudio/Infrastructure/Persistence/Migrations src/ISEStudio.IntegrationTests/Graph
git commit -m "feat(graph): add postgres fact ledger schema"
```

### 任务 3：实现事务性图写入与证据链

**文件：**
- 新建：`src/ISEStudio/Graph/GraphStore.cs`
- 新建：`src/ISEStudio/Infrastructure/Startup/GraphServiceCollectionExtensions.cs`
- 修改：`src/ISEStudio/Program.cs`
- 修改：`src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs`

**接口：**
- 消费：任务 1 的 `IGraphStore`、任务 2 的持久化实体和 `ISEStudioDbContext`。
- 产出：`GraphStore : IGraphStore` 和 `IServiceCollection AddGraphStore(this IServiceCollection services)`。
- `RecordFactAsync` 成功时返回新建事实；失败时不得留下事实、证据或审计行。

- [ ] **步骤 1：写入失败的事务行为测试**

```csharp
[Fact]
public async Task Record_fact_persists_fact_and_evidence_in_one_transaction()
{
    var command = _fixture.NewEntityObjectFact(evidenceCount: 2);

    var fact = await _fixture.GraphStore.RecordFactAsync(command, TestContext.Current.CancellationToken);

    await using var verify = _fixture.CreateDbContext();
    Assert.Equal(1, await verify.Facts.CountAsync(item => item.Id == fact.Id));
    Assert.Equal(2, await verify.FactEvidences.CountAsync(item => item.FactId == fact.Id));
    Assert.Contains(await verify.AuditEvents.ToListAsync(), item => item.EventType == "graph.fact.recorded");
}

[Fact]
public async Task Record_fact_rolls_back_when_evidence_references_unknown_chunk()
{
    var command = _fixture.NewEntityObjectFact(evidenceCount: 1) with
    {
        Evidence = [new FactEvidenceInput(Guid.NewGuid(), "quote", "predicate")]
    };

    await Assert.ThrowsAsync<DbUpdateException>(() =>
        _fixture.GraphStore.RecordFactAsync(command, TestContext.Current.CancellationToken));

    await using var verify = _fixture.CreateDbContext();
    Assert.Empty(await verify.Facts.ToListAsync());
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Record_fact`

预期：FAIL，提示 `GraphStore` 或注册服务尚不存在。

- [ ] **步骤 3：实现写入服务及 DI 注册**

`GraphStore.RecordFactAsync` 按此顺序实施：调用 `command.Validate()`；开始 `await db.Database.BeginTransactionAsync(cancellationToken)`；验证知识系统、主语实体、谓词和对象实体均属于同一 `KnowledgeSystemId`；添加 `FactEntity` 和每个 `FactEvidenceEntity`；添加既有 `AuditEventEntity`，其事件类型为 `graph.fact.recorded` 并以 JSON 记录事实 ID、知识系统 ID、actor ID 和证据数；调用一次 `SaveChangesAsync`；提交事务；映射成 `GraphFact` 返回。

```csharp
await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
// Validate referenced rows and their KnowledgeSystemId before adding entities.
_db.Facts.Add(fact);
_db.FactEvidences.AddRange(evidence);
_db.AuditEvents.Add(auditEvent);
await _db.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

`InvalidateFactAsync` 只能将尚未失效的同一知识系统事实设置为 `InvalidatedAt`，并写入 `graph.fact.invalidated` 审计事件；如果未找到可失效的事实，抛出 `KeyNotFoundException`。在 `Program.cs` 调用 `builder.Services.AddGraphStore()`，扩展方法将 `IGraphStore` 注册为 scoped。

- [ ] **步骤 4：运行测试确认通过**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Record_fact`

预期：PASS，成功路径同时保存事实、两条证据与审计记录；失败路径不存在残留事实。

- [ ] **步骤 5：提交**

```powershell
git add src/ISEStudio/Graph/GraphStore.cs src/ISEStudio/Infrastructure/Startup/GraphServiceCollectionExtensions.cs src/ISEStudio/Program.cs src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs
git commit -m "feat(graph): persist facts with provenance and audit"
```

### 任务 4：实现双时态有界图遍历

**文件：**
- 修改：`src/ISEStudio/Graph/GraphContracts.cs`
- 修改：`src/ISEStudio/Graph/GraphStore.cs`
- 修改：`src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs`

**接口：**
- 消费：任务 3 的 `GraphStore`。
- `GraphNeighborhoodQuery` 必须包含 `KnowledgeSystemId`、`RootEntityId`、`MaxDepth`、`EffectiveAt` 和 `IncludeInvalidated`。
- `GetNeighborhoodAsync` 返回去重的 `GraphFact` 边和已访问的 `GraphEntity` 节点；`MaxDepth` 有效范围为 1 到 5。

- [ ] **步骤 1：写入失败的遍历与时态测试**

```csharp
[Fact]
public async Task Neighborhood_uses_bounded_traversal_and_effective_time()
{
    var (a, b, c) = await _fixture.CreateChainAsync();
    await _fixture.RecordEdgeAsync(a, b, validFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
    await _fixture.RecordEdgeAsync(b, c, validFrom: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

    var result = await _fixture.GraphStore.GetNeighborhoodAsync(new GraphNeighborhoodQuery(
        _fixture.KnowledgeSystemId, a, 2,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), false),
        TestContext.Current.CancellationToken);

    Assert.Contains(result.EntityIds, id => id == b);
    Assert.DoesNotContain(result.EntityIds, id => id == c);
    Assert.Single(result.Facts);
}

[Fact]
public async Task Neighborhood_never_crosses_knowledge_system_boundaries()
{
    var root = await _fixture.CreateEntityAsync();
    var foreign = await _fixture.CreateEntityInAnotherKnowledgeSystemAsync();

    await _fixture.InsertForeignBoundaryFactAsync(root, foreign);

    var result = await _fixture.GraphStore.GetNeighborhoodAsync(new GraphNeighborhoodQuery(
        _fixture.KnowledgeSystemId, root, 5, DateTimeOffset.UtcNow, false),
        TestContext.Current.CancellationToken);

    Assert.DoesNotContain(foreign, result.EntityIds);
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Neighborhood`

预期：FAIL，查询尚未实现或未按有效时间过滤。

- [ ] **步骤 3：实现参数化递归 CTE**

在 `GraphStore.GetNeighborhoodAsync` 中先验证 `MaxDepth`。使用 `NpgsqlCommand` 的具名参数传入 `@knowledgeSystemId`、`@rootEntityId`、`@maxDepth`、`@effectiveAt` 和 `@includeInvalidated`。CTE 必须从根实体关联的 live facts 开始，在每轮仅将同一知识系统中相邻的实体对象加入下一层；以路径 UUID 数组排除环；以 `depth < @maxDepth` 截断递归；默认过滤 `invalidated_at IS NULL` 与 `(valid_from IS NULL OR valid_from <= @effectiveAt)` 及 `(valid_to IS NULL OR valid_to > @effectiveAt)`。

```sql
WITH RECURSIVE neighborhood AS (
    SELECT f.id, f.subject_entity_id, f.object_entity_id, 1 AS depth,
           ARRAY[@rootEntityId, f.object_entity_id] AS path
      FROM facts f
     WHERE f.knowledge_system_id = @knowledgeSystemId
       AND f.subject_entity_id = @rootEntityId
       AND f.object_entity_id IS NOT NULL
       AND (@includeInvalidated OR f.invalidated_at IS NULL)
       AND (f.valid_from IS NULL OR f.valid_from <= @effectiveAt)
       AND (f.valid_to IS NULL OR f.valid_to > @effectiveAt)
    UNION ALL
    SELECT f.id, f.subject_entity_id, f.object_entity_id, n.depth + 1,
           n.path || f.object_entity_id
      FROM neighborhood n
      JOIN facts f ON f.subject_entity_id = n.object_entity_id
     WHERE n.depth < @maxDepth
       AND f.knowledge_system_id = @knowledgeSystemId
       AND f.object_entity_id IS NOT NULL
       AND NOT f.object_entity_id = ANY(n.path)
       AND (@includeInvalidated OR f.invalidated_at IS NULL)
       AND (f.valid_from IS NULL OR f.valid_from <= @effectiveAt)
       AND (f.valid_to IS NULL OR f.valid_to > @effectiveAt)
)
SELECT DISTINCT id, subject_entity_id, object_entity_id FROM neighborhood;
```

将读取行映射为领域记录，不把 Npgsql 或 EF Core 类型暴露给调用方。

- [ ] **步骤 4：运行测试确认通过**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~GraphStoreTests.Neighborhood`

预期：PASS，查询有界、按有效时间过滤，并且不会跨知识系统读取。

- [ ] **步骤 5：提交**

```powershell
git add src/ISEStudio/Graph/GraphContracts.cs src/ISEStudio/Graph/GraphStore.cs src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs
git commit -m "feat(graph): query temporal neighborhoods with recursive cte"
```

### 任务 5：建立迁移和性能验收基线

**文件：**
- 修改：`src/ISEStudio.IntegrationTests/Graph/GraphSchemaTests.cs`
- 新建：`src/ISEStudio.IntegrationTests/Graph/GraphPerformanceTests.cs`
- 修改：`docs/architecture.md`

**接口：**
- 消费：任务 2 至任务 4 的 schema 与 `IGraphStore`。
- 产出：可重复的 PostgreSQL schema 断言、图遍历基准样本和架构说明。

- [ ] **步骤 1：写入失败的索引和性能守卫测试**

```csharp
[Fact]
public async Task Fact_traversal_indexes_exist()
{
    var indexes = await _fixture.GetIndexesAsync("facts");
    Assert.Contains(indexes, definition => definition.Contains("knowledge_system_id") &&
                                          definition.Contains("subject_entity_id") &&
                                          definition.Contains("invalidated_at IS NULL"));
    Assert.Contains(indexes, definition => definition.Contains("knowledge_system_id") &&
                                          definition.Contains("object_entity_id") &&
                                          definition.Contains("invalidated_at IS NULL"));
}

[Fact]
public async Task Five_hop_neighborhood_of_one_thousand_facts_completes_within_two_seconds()
{
    var root = await _fixture.CreateTraversalFixtureAsync(factCount: 1000);
    var stopwatch = Stopwatch.StartNew();
    await _fixture.GraphStore.GetNeighborhoodAsync(new GraphNeighborhoodQuery(
        _fixture.KnowledgeSystemId, root, 5, DateTimeOffset.UtcNow, false),
        TestContext.Current.CancellationToken);
    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter "FullyQualifiedName~Fact_traversal_indexes_exist|FullyQualifiedName~Five_hop_neighborhood"`

预期：若索引或查询计划不满足约束则 FAIL；记录初始运行时间作为迁移基线。

- [ ] **步骤 3：完成索引、fixture 辅助方法与架构文档**

仅在任务 2 的迁移缺少相应 DDL 时追加 EF Core migration；不得手工修改已经应用的 migration。`PostgresGraphFixture` 的 `GetIndexesAsync` 从 `pg_indexes` 查询，`CreateTraversalFixtureAsync` 使用批量插入建立确定性图。`docs/architecture.md` 增加“关系图账本”小节，明确 PostgreSQL 为唯一权威图事实库、事实为仅追加记录、递归 CTE 的有界规则，以及 Oxigraph 不管理运行时图。

- [ ] **步骤 4：运行完整图核心验证**

运行：`dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ISEStudio.IntegrationTests.Graph --no-restore`

预期：PASS。随后运行：`dotnet build src/ISEStudio.sln --no-restore -warnaserror`

预期：Build succeeded，且无警告升级为错误。

- [ ] **步骤 5：提交**

```powershell
git add src/ISEStudio.IntegrationTests/Graph docs/architecture.md src/ISEStudio/Infrastructure/Persistence/Migrations
git commit -m "test(graph): verify postgres traversal constraints"
```

## 计划后续顺序

图核心完成且通过任务 5 后，再分别规划并实施下列独立可验收阶段：

1. 摄取与版本化来源：迁移 `sources`、`documents`、`chunks`、对象存储、解析、分块和幂等作业。
2. 本体与 RDF/OWL 导入：移植类型层级、关系 domain/range、提案和导入投影；让 Oxigraph/dotNetRDF 仅位于此导入边界。
3. LLM 抽取、实体/类型解析、审核、推理和告警：对固定 Rust 基线夹具做结果契约测试。
4. 搜索：先实现 PostgreSQL FTS 与 pgvector，按基准结果决定是否在 `ISearchIndex` 后引入 Lucene.NET。
5. API、MCP 和 ontopilot 前端工作流：在 `ontopilot/frontend` 复用现有路由、组件和 API 客户端，完成有意的 API 破坏性变更。
6. 生产切换：以恢复演练、全量数据校验、维护窗口和可验证回滚为独立发布计划。
