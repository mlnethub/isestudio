# OnToPilot .NET RDF 核心实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐项执行。步骤使用 `- [ ]` 跟踪。

**目标：** 将当前 Oxigraph 本体读写、TBox/SKOS/ABox 治理、冲突、发布和导入导出行为移植到 .NET，并以现有 gold fixture 验证语义一致。

**架构：** `StoreWrapper` 是唯一 Oxigraph.NET 入口，按图串行化 capture 写入并记录可逆 N-Triples diff。每个知识系统至少使用 TBox、ABox、Vocabulary 三张可变具名图；发布使用独立只读 serving store。

**技术栈：** Oxigraph 0.5.8、Oxigraph.Extensions.DotNetRDF 0.5.8、dotNetRDF 3.5.2、xUnit

## 全局约束

- 先编译 0.5.8 API 探针，再确定只读打开、序列化和加载方法的真实签名。
- 同图 capture 不能重入；竞争写入 15 秒后转换为 HTTP 409 兼容错误。
- RDF 写入不是 EF 事务的一部分，失败路径必须先 revert RDF，再回滚 SQL。
- N-Triples diff 必须保留 blank node、语言标签和 datatype。
- SHACL 不替代 `TBoxGuard` 的角色证据和规范化过程逻辑。

---

### 任务 1：验证包 API 并实现 StoreWrapper/capture

**文件：**

- 创建：`src/OnToPilot.OxigraphProbe/OnToPilot.OxigraphProbe.csproj`
- 创建：`src/OnToPilot.OxigraphProbe/Program.cs`
- 创建：`src/OnToPilot/Ontology/StoreWrapper.cs`
- 创建：`src/OnToPilot/Ontology/QuadChangeCapture.cs`
- 创建：`src/OnToPilot/Ontology/GraphWriteCoordinator.cs`
- 测试：`src/OnToPilot.Tests/Ontology/StoreWrapperTests.cs`

**接口：**

- 输出：`AddQuads`、`RemoveQuads`、`Match`、`Count`、`ContainsQuad`、`DumpNQuads`、`ReplaceGraph`、`CaptureAsync`、`ReadLockAsync`。

- [ ] **步骤 1：创建只编译的 0.5.8 API 探针**

```csharp
using var store = new Store(args[0]);
var graph = new NamedNode("urn:probe");
store.Add(new Quad(new NamedNode("urn:s"), new NamedNode("urn:p"), new Literal("v"), graph));
Console.WriteLine(store.Match(null, null, null, graph).Count());
```

- [ ] **步骤 2：恢复并编译探针**

运行：`dotnet restore src/OnToPilot.OxigraphProbe; dotnet build src/OnToPilot.OxigraphProbe -warnaserror`
预期：成功；若签名不同，只按编译器和 0.5.8 XML 文档修正探针，再把确认签名写入 `docs/migration/oxigraph-0.5.8-api.md`。

- [ ] **步骤 3：写 capture/revert 失败测试**

```csharp
[Fact]
public async Task Failed_capture_restores_exact_pre_operation_graph()
{
    Store.AddQuads(Graph, [Existing]);
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
        await using var capture = await Store.CaptureAsync(Graph, revertOnError: true);
        Store.RemoveQuads(Graph, [Existing]);
        Store.AddQuads(Graph, [Added]);
        throw new InvalidOperationException();
    });
    Assert.Equal([Existing], Store.Match(graph: Graph));
}
```

- [ ] **步骤 4：实现按图锁与净 diff**

```csharp
public sealed record QuadDiff(byte[] Added, byte[] Removed);

public async ValueTask<QuadChangeCapture> CaptureAsync(
    string graphIri, bool revertOnError, CancellationToken cancellationToken = default)
{
    var lease = await _coordinator.AcquireAsync(graphIri, TimeSpan.FromSeconds(15), cancellationToken);
    return new QuadChangeCapture(this, graphIri, lease, revertOnError);
}
```

- [ ] **步骤 5：验证并提交**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~StoreWrapper`
预期：CRUD、格式 round-trip、capture、revert、同图竞争和异图并发测试通过。

```bash
git add src/OnToPilot.OxigraphProbe src/OnToPilot/Ontology src/OnToPilot.Tests/Ontology docs/migration/oxigraph-0.5.8-api.md
git commit -m "feat: wrap oxigraph store and reversible writes"
```

### 任务 2：移植 TBox 构建、编辑和 Guard

**文件：**

- 创建：`src/OnToPilot/Ontology/Vocabulary.cs`
- 创建：`src/OnToPilot/Ontology/SchemaBuilder.cs`
- 创建：`src/OnToPilot/Ontology/OntologyEditor.cs`
- 创建：`src/OnToPilot/Ontology/TBoxGuard.cs`
- 创建：`src/OnToPilot/Ontology/RoleEvidence.cs`
- 复制 fixture：`src/OnToPilot.Tests/Fixtures/tbox_abox_boundary.json`
- 测试：`src/OnToPilot.Tests/Ontology/SchemaBuilderTests.cs`
- 测试：`src/OnToPilot.Tests/Ontology/TBoxBoundaryGoldTests.cs`

**接口：**

- 输入：结构化 class/property/axiom mutation。
- 输出：与 Python `build_mutation()`、`build_view()` 和 `sanitize_ontology_delta()` 等价的结果。

- [ ] **步骤 1：写 gold fixture 失败测试**

```csharp
[Theory]
[MemberData(nameof(BoundaryCases))]
public void Guard_matches_python_role_boundary_fixture(BoundaryCase fixture)
{
    var result = Guard.Sanitize(fixture.Input, fixture.Context);
    Assert.Equal(fixture.ExpectedClasses, result.Classes.Select(x => x.Label));
    Assert.Equal(fixture.ExpectedIndividuals, result.Individuals.Select(x => x.Label));
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~SchemaBuilder|FullyQualifiedName~TBoxBoundaryGold"`
预期：失败，核心类型不存在。

- [ ] **步骤 3：实现明确的 mutation/view DTO**

```csharp
public sealed record OntologyMutation(
    IReadOnlyList<ClassMutation> Classes,
    IReadOnlyList<PropertyMutation> ObjectProperties,
    IReadOnlyList<PropertyMutation> DataProperties,
    IReadOnlyList<AxiomMutation> Axioms);

public OntologyView BuildView(string graphIri);
public IReadOnlyList<Quad> BuildMutation(string baseIri, OntologyMutation mutation);
```

- [ ] **步骤 4：逐项移植过程 Guard 并验证**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~SchemaBuilder|FullyQualifiedName~TBoxBoundaryGold"`
预期：country、wine region、organization、plugin、Kubernetes kind、XSD datatype 六类 gold case 全部通过。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Ontology src/OnToPilot.Tests/Ontology src/OnToPilot.Tests/Fixtures
git commit -m "feat: port tbox schema and guard"
```

### 任务 3：实现 ABox、SKOS、provenance 与 SHACL

**文件：**

- 创建：`src/OnToPilot/Ontology/ABoxManager.cs`
- 创建：`src/OnToPilot/Ontology/ABoxValidator.cs`
- 创建：`src/OnToPilot/Ontology/SkosManager.cs`
- 创建：`src/OnToPilot/Ontology/StatementProvenanceService.cs`
- 创建：`src/OnToPilot/Ontology/ShaclValidator.cs`
- 创建：`src/OnToPilot/Ontology/Shapes/tbox-shapes.ttl`
- 测试：`src/OnToPilot.Tests/Ontology/ABoxManagerTests.cs`
- 测试：`src/OnToPilot.Tests/Ontology/SkosManagerTests.cs`
- 测试：`src/OnToPilot.Tests/Ontology/ShaclValidatorTests.cs`

**接口：**

- 输出：ABox individual/assertion CRUD、事实 key、SKOS scheme/concept/resolve/filter、SHACL report。

- [ ] **步骤 1：写跨图与词汇过滤失败测试**

```csharp
[Fact]
public void Tbox_abox_and_vocabulary_are_isolated_named_graphs()
{
    ABox.CreateIndividual(Ks, "urn:i", "urn:Class");
    Skos.CreateConcept(Ks, new("urn:c", "Pump", "en"));
    Assert.Empty(Store.Match(subject: "urn:i", graph: Ks.TBoxGraph));
    Assert.Empty(Store.Match(subject: "urn:c", graph: Ks.ABoxGraph));
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ABoxManager|FullyQualifiedName~SkosManager|FullyQualifiedName~ShaclValidator"`
预期：失败，服务尚不存在。

- [ ] **步骤 3：实现稳定事实 key 与 SHACL 映射**

```csharp
public static string IndividualKey(string iri) => $"ind|{iri}";
public static string DataKey(string subject, string property, string value) => $"data|{subject}|{property}|{value}";
public static string ObjectKey(string subject, string property, string target) => $"obj|{subject}|{property}|{target}";
```

- [ ] **步骤 4：验证过滤组合与 Guard 对照**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ABoxManager|FullyQualifiedName~SkosManager|FullyQualifiedName~ShaclValidator"`
预期：mapping/origin/status/date 组合过滤、循环拒绝、label 规则、domain/range、属性类型互斥与 disjoint 约束通过。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Ontology src/OnToPilot.Tests/Ontology
git commit -m "feat: port abox skos provenance and shacl"
```

### 任务 4：实现冲突、发布存储和 RDF 导入导出

**文件：**

- 创建：`src/OnToPilot/Ontology/ConflictDetector.cs`
- 创建：`src/OnToPilot/Ontology/ReleaseArtifactStore.cs`
- 创建：`src/OnToPilot/Ontology/ReleaseManager.cs`
- 创建：`src/OnToPilot/Ontology/RdfImportService.cs`
- 创建：`src/OnToPilot/Ontology/RdfExportService.cs`
- 测试：`src/OnToPilot.Tests/Ontology/ConflictDetectorTests.cs`
- 测试：`src/OnToPilot.Tests/Ontology/ReleaseStoreTests.cs`
- 测试：`src/OnToPilot.Tests/Ontology/ReleaseManagerTests.cs`
- 测试：`src/OnToPilot.Tests/Ontology/RdfRoundTripTests.cs`

**接口：**

- 输出：稳定 conflict signature、N-Quads 分片 manifest、三层 immutable release、独立 serving graph、merge/replace 导入。

- [ ] **步骤 1：写发布不可变性失败测试**

```csharp
[Fact]
public async Task Published_release_isolated_from_later_workspace_changes()
{
    var release = await Releases.CaptureAsync(Ks, Actor, CancellationToken.None);
    await Releases.PublishAsync(release.Id, Actor, CancellationToken.None);
    Store.AddQuads(Ks.TBoxGraph, [LaterQuad]);
    Assert.DoesNotContain(LaterQuad, Releases.ReadPublished(release.Id, RdfLayer.TBox));
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ConflictDetector|FullyQualifiedName~Release|FullyQualifiedName~RdfRoundTrip"`
预期：失败，服务不存在。

- [ ] **步骤 3：实现制品 manifest 与校验**

```csharp
public sealed record ReleaseFileManifest(
    string Layer, string FileName, long StatementCount, string Sha256);

public sealed record ReleaseManifest(
    string Version, IReadOnlyList<ReleaseFileManifest> Files, long ProvenanceCount);
```

- [ ] **步骤 4：验证旧版回归语义**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ConflictDetector|FullyQualifiedName~Release|FullyQualifiedName~RdfRoundTrip"`
预期：分片大小/进度/hash、语义 diff 忽略文件顺序、删除 v1 后重新分配 v1、serving graph 隔离、四种导出格式语言标签 round-trip 全部通过。

- [ ] **步骤 5：运行阶段门禁并提交**

运行：`dotnet test src/OnToPilot.Tests --filter Category=RdfCore; dotnet build src/OnToPilot.sln -warnaserror`
预期：全部通过。

```bash
git add src
git commit -m "feat: port conflict release and rdf interchange"
```
