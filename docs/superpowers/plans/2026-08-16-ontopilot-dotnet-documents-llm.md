# OnToPilot .NET 文档与 LLM 实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐项执行。步骤使用 `- [ ]` 跟踪。

**目标：** 在不修改前端上传流程的前提下，将 CAS/MinIO、文档解析、分块、模型 Provider、向量和 TBox/ABox 抽取任务迁到 .NET。

**架构：** `IBlobStore` 隔离本地 CAS 与 MinIO；`IDocumentParser` 先尝试 DoclingDotNet，再使用格式专用 fallback；抽取只依赖 `IChatClient` 和 `IEmbeddingGenerator`，并由 `ExtractionOrchestrator` 协调 SQL 状态、RDF capture 与 provenance。

**技术栈：** AWSSDK.S3、DoclingDotNet 1.2.0、PdfPig 0.1.15、DocumentFormat.OpenXml 3.3.0、ClosedXML 0.104.1、Microsoft.Extensions.AI 10.7.0

## 全局约束

- 上传继续走现有后端端点，不引入要求修改前端的预签名直传。
- MinIO object key 使用 SHA-256，但迁移期保留 `Document.storage_path` 的旧 `aa/bb/hash` 可追溯映射。
- 实际支持扩展名以当前入口为准：`pdf/docx/doc/xlsx/xls/txt/md/markdown/csv`；HTML/PPTX 不在本阶段扩大范围。
- LLM 与 embedding 容量按 endpoint 隔离，同 endpoint 可重入但不得超过 `concurrency_limit` 1-64。
- 抽取失败必须将 RDF capture 回滚，并把任务标记为 failed，不留下孤立 triples。

---

### 任务 1：实现本地 CAS 与 MinIO BlobStore

**文件：**

- 创建：`src/OnToPilot/Storage/IBlobStore.cs`
- 创建：`src/OnToPilot/Storage/LocalCasBlobStore.cs`
- 创建：`src/OnToPilot/Storage/MinioBlobStore.cs`
- 创建：`src/OnToPilot/Storage/BlobKey.cs`
- 测试：`src/OnToPilot.Tests/Storage/LocalCasBlobStoreTests.cs`
- 测试：`src/OnToPilot.IntegrationTests/Storage/MinioBlobStoreTests.cs`

**接口：**

- 输出：`PutAsync`、`GetAsync`、`ExistsAsync`、`RemoveAsync`；写入返回 SHA-256 与兼容 storage path。

- [ ] **步骤 1：写存储契约失败测试**

```csharp
[Theory]
[MemberData(nameof(Stores))]
public async Task Put_is_content_addressed_and_idempotent(IBlobStore store)
{
    var bytes = "same content"u8.ToArray();
    var first = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);
    var second = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);
    Assert.Equal(first.Sha256, second.Sha256);
    Assert.Equal($"{first.Sha256[..2]}/{first.Sha256[2..4]}/{first.Sha256}", first.LegacyStoragePath);
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~LocalCasBlobStore`
预期：失败，`IBlobStore` 不存在。

- [ ] **步骤 3：实现流式哈希与 S3 path-style 客户端**

```csharp
public sealed record BlobWriteResult(string Sha256, string LegacyStoragePath);

public interface IBlobStore
{
    Task<BlobWriteResult> PutAsync(Stream content, CancellationToken cancellationToken);
    Task<Stream?> GetAsync(string sha256, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken);
}
```

- [ ] **步骤 4：验证本地与 MinIO 实现**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~LocalCasBlobStore; dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~MinioBlobStore`
预期：幂等写入、缺失读取、删除、同内容去重和多 KS 引用不误删通过。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Storage src/OnToPilot.Tests/Storage src/OnToPilot.IntegrationTests/Storage
git commit -m "feat: add compatible minio blob storage"
```

### 任务 2：冻结并移植 Parser/Chunker 行为

**文件：**

- 创建：`backend/scripts/export_parsing_fixtures.py`
- 创建：`migration/fixtures/parsing/manifest.json`
- 创建：`src/OnToPilot/Parsing/IDocumentParser.cs`
- 创建：`src/OnToPilot/Parsing/DocumentParser.cs`
- 创建：`src/OnToPilot/Parsing/TokenEstimator.cs`
- 创建：`src/OnToPilot/Parsing/Chunker.cs`
- 测试：`src/OnToPilot.Tests/Parsing/ParserFallbackTests.cs`
- 测试：`src/OnToPilot.Tests/Parsing/ChunkerParityTests.cs`

**接口：**

- 输出：`ParseResult(Text, Backend, StructuredDocument)` 与 `ChunkSpan(Idx, Text, CharStart, CharEnd, TokenEstimate)`。

- [ ] **步骤 1：从 Python 生成确定性分块 fixture**

```python
cases = {
    "english": "First sentence. Second sentence.\n\nThird paragraph.",
    "chinese": "第一句。第二句。\n\n第三段。",
    "mixed": "Pump P-101 温度为 80°C。Next sentence.",
}
for name, text in cases.items():
    output[name] = [chunk.model_dump() for chunk in chunk_text(text, size=24, overlap=6)]
```

- [ ] **步骤 2：写 parity 失败测试并运行**

运行：`cd backend; python scripts/export_parsing_fixtures.py ../migration/fixtures/parsing/manifest.json; cd ..; dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ParserFallback|FullyQualifiedName~ChunkerParity"`
预期：fixture 生成成功；.NET 测试因实现缺失失败。

- [ ] **步骤 3：实现分层 parser 与结构化优先 chunker**

```csharp
public sealed record ParseResult(string Text, string Backend, object? StructuredDocument = null);
public sealed record ChunkSpan(int Idx, string Text, int CharStart, int CharEnd, int TokenEstimate);

public IReadOnlyList<ChunkSpan> ChunkDocument(ParseResult result) =>
    result.StructuredDocument is { } document && ChunkStructured(document) is { Count: > 0 } spans
        ? spans
        : Chunk(result.Text);
```

- [ ] **步骤 4：验证 fallback 与精确偏移**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ParserFallback|FullyQualifiedName~ChunkerParity"`
预期：PDF `## Page N`、XLSX `## Sheet: name`、空文本、段句边界、overlap、`char_start/end` 和 token estimate 与 fixture 一致。

- [ ] **步骤 5：提交**

```bash
git add backend/scripts/export_parsing_fixtures.py migration/fixtures/parsing src/OnToPilot/Parsing src/OnToPilot.Tests/Parsing
git commit -m "feat: port document parsing and chunking"
```

### 任务 3：实现 Provider、容量与 embedding

**文件：**

- 创建：`src/OnToPilot/Llm/LlmProviderConfig.cs`
- 创建：`src/OnToPilot/Llm/IChatClientFactory.cs`
- 创建：`src/OnToPilot/Llm/ChatClientFactory.cs`
- 创建：`src/OnToPilot/Llm/EmbeddingGeneratorFactory.cs`
- 创建：`src/OnToPilot/Llm/EndpointCapacityCoordinator.cs`
- 测试：`src/OnToPilot.Tests/Llm/ProviderRoutingTests.cs`
- 测试：`src/OnToPilot.Tests/Llm/EndpointCapacityTests.cs`

**接口：**

- 输出：支持 `openai/deepseek/anthropic/gemini/ollama/azure-openai/openai-compatible` 的 `IChatClient` 与 embedding 工厂。

- [ ] **步骤 1：移植容量失败测试**

```csharp
[Fact]
public async Task Chat_and_embedding_use_separate_capacity_keys()
{
    await using var chat = await Capacity.AcquireAsync(new("chat", Endpoint), 1, CancellationToken.None);
    await using var embedding = await Capacity.AcquireAsync(new("embedding", Endpoint), 1, CancellationToken.None);
    Assert.NotNull(chat);
    Assert.NotNull(embedding);
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ProviderRouting|FullyQualifiedName~EndpointCapacity"`
预期：失败，工厂和协调器不存在。

- [ ] **步骤 3：实现显式 Provider 路由和可重入 lease**

```csharp
public IChatClient Create(LlmProviderConfig config) => config.Provider.ToLowerInvariant() switch
{
    "openai" or "deepseek" or "openai-compatible" => CreateOpenAiCompatible(config),
    "anthropic" => CreateAnthropic(config),
    "gemini" => CreateGemini(config),
    "ollama" => CreateOllama(config),
    "azure-openai" => CreateAzureOpenAi(config),
    _ => throw new InvalidOperationException($"Unsupported provider: {config.Provider}"),
};
```

- [ ] **步骤 4：验证容量与能力降级**

运行：`dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~ProviderRouting|FullyQualifiedName~EndpointCapacity"`
预期：同 endpoint 限流、异 endpoint 并行、嵌套可重入、chat/embedding 隔离和 function-calling 降级测试通过。

- [ ] **步骤 5：提交**

```bash
git add src/OnToPilot/Llm src/OnToPilot.Tests/Llm
git commit -m "feat: add provider neutral ai clients"
```

### 任务 4：实现抽取编排和任务恢复

**文件：**

- 创建：`src/OnToPilot/Extraction/ExtractionOrchestrator.cs`
- 创建：`src/OnToPilot/Extraction/TBoxExtractionService.cs`
- 创建：`src/OnToPilot/Extraction/ABoxExtractionService.cs`
- 创建：`src/OnToPilot/Extraction/TerminologyService.cs`
- 创建：`src/OnToPilot/Extraction/PromptSnapshotService.cs`
- 创建：`src/OnToPilot.Tests/Extraction/ExtractionStateTests.cs`
- 创建：`src/OnToPilot.IntegrationTests/Extraction/ExtractionWorkflowTests.cs`

**接口：**

- 输出：TBox、ABox、combined 三类任务；保留 `prompt_snapshot`、chunk IDs、phase、进度、计数、unknown classes 和 terminology 指标。

- [ ] **步骤 1：写失败后 RDF/SQL 一致性测试**

```csharp
[Fact]
public async Task Failed_merge_reverts_rdf_and_marks_job_failed()
{
    FakeChat.EnqueueValidDelta();
    Merger.FailWith(new InvalidOperationException("merge failed"));
    var before = Store.DumpNQuads(Ks.TBoxGraph);
    var job = await Orchestrator.StartTBoxAsync(Request, CancellationToken.None);
    await Jobs.WaitAsync(job.Id);
    Assert.Equal("failed", (await Jobs.GetAsync(job.Id)).Status);
    Assert.Equal(before, Store.DumpNQuads(Ks.TBoxGraph));
}
```

- [ ] **步骤 2：运行并确认失败**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~ExtractionState`
预期：失败，编排器不存在。

- [ ] **步骤 3：实现明确状态机**

```csharp
public enum ExtractionPhase { TBox, ABox, Terminology, Finalizing }
public enum JobStatus { Pending, Running, Completed, Failed }

public Task<ExtractionJobEntity> StartTBoxAsync(ExtractionRequest request, CancellationToken cancellationToken);
public Task<ExtractionJobEntity> StartABoxAsync(ExtractionRequest request, CancellationToken cancellationToken);
public Task<ExtractionJobEntity> StartCombinedAsync(ExtractionRequest request, CancellationToken cancellationToken);
```

- [ ] **步骤 4：用 Fake Client 验证完整工作流**

运行：`dotnet test src/OnToPilot.Tests --filter FullyQualifiedName~ExtractionState; dotnet test src/OnToPilot.IntegrationTests --filter FullyQualifiedName~ExtractionWorkflow`
预期：上传、解析、TBox、ABox、术语、provenance、轮询进度、失败恢复全部通过，不调用外部服务。

- [ ] **步骤 5：运行阶段门禁并提交**

运行：`dotnet test src/OnToPilot.Tests --filter "Category=Documents|Category=Llm|Category=Extraction"`
预期：全部通过。

```bash
git add src backend/scripts migration/fixtures
git commit -m "feat: port extraction orchestration"
```
