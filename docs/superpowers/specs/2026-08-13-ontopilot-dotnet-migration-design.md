# OnToPilot 后端迁移到 .NET 10 技术规格

**状态**: 已批准
**日期**: 2026-08-13
**范围**: Backend（FastAPI → ASP.NET Core 10），Frontend 保持不变

---

## 1. 背景与目标

### 1.1 迁移动机

- 将 OnToPilot 后端从 Python 3.12 / FastAPI / pyoxigraph 迁移到 .NET 10 / ASP.NET Core 10 / Oxigraph.NET
- 使用 `Ai4c-AI/oxigraph` 的 .NET 绑定，复用与 Python 版相同的 Oxigraph Rust 存储引擎
- 引入 dotNetRDF 3.5.2 补充 SHACL 本体验证能力
- Frontend（React + TypeScript + Vite）保持不变，通过 REST API 与新后端通信

### 1.2 技术栈对照

| 层级 | 当前（Python） | 迁移后（.NET 10） |
|---|---|---|
| 框架 | FastAPI | ASP.NET Core 10 |
| RDF 存储 | pyoxigraph（Rust Oxigraph） | **Oxigraph 0.5.8**（同一 Rust 引擎） |
| RDF 互操作 | rdflib（导出） | **dotNetRDF 3.5.2** + **Oxigraph.Extensions.DotNetRDF 0.5.8** |
| 本体验证 | TBox Guard（Python） | **dotNetRDF ShapesGraph**（SHACL） |
| ORM | SQLAlchemy + asyncpg | EF Core + Npgsql |
| 认证 | FastAPI Session Cookie | ASP.NET Core Session Cookie |
| LLM 调用 | openrouter.py | **Microsoft.Extensions.AI 10.7.0** + **Microsoft.Extensions.AI.OpenAI** |

### 1.3 成功标准

- Frontend 无需修改，REST API 契约兼容
- 现有 RDF 数据通过跨绑定兼容性冒烟测试后可直接复用；验证失败时通过 N-Quads 导出/导入迁移
- SHACL 验证覆盖 TBox Guard 现有检查项
- 迁移后的后端可通过 `dotnet test` 覆盖核心本体逻辑

---

## 2. 技术栈详情

### 2.1 Oxigraph.NET（核心存储）

```xml
<PackageReference Include="Oxigraph" Version="0.5.8" />
<PackageReference Include="Oxigraph.Extensions.DotNetRDF" Version="0.5.8" />
```

- 上游源码：[`Ai4c-AI/oxigraph` 的 `dotnet` 分支](https://github.com/Ai4c-AI/oxigraph/tree/dotnet)
- NuGet `0.5.8` 对应源码提交：`5a9c77726f3cb7e9a51c7343e7f3eadd16ff6369`
- 目标框架：`net10.0`
- 原生资产：`win-x64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`
- 版本策略：应用锁定 NuGet `0.5.8`；上游分支当前已进入 `0.6.0-dev`，不得直接以分支 HEAD 替代已发布包

**与 pyoxigraph API 对照表：**

| pyoxigraph (Python) | Oxigraph.NET (C#) |
|---|---|
| `Store(path)` | `new Store(path)` |
| `BlankNode(value)` | `new BlankNode(value)` |
| `Literal(value, lang)` | `new Literal(value, language: lang)` |
| `Literal(value, datatype=iri)` | `new Literal(value, datatype: new NamedNode(iri))` |
| `NamedNode(iri)` | `new NamedNode(iri)` |
| `Quad(s, p, o, g)` | `new Quad(s, p, o, g)` |
| `store.add(q)` | `store.Add(q)` |
| `store.remove(q)` | `store.Remove(q)` |
| `store.quads_for_pattern(s, p, o, g)` | `store.Match(s, p, o, g)` |
| `store.bulk_extend(quads)` | `store.BulkExtend(quads)` |
| `serialize(quads, N-Triples)` | `store.Dump(RdfFormat.NTriples)` |
| `parse(data, N-Triples)` | `RdfFormat.NTriples` + `store.Match()` |
| `store.count()` | `store.Count` |
| `store.contains(q)` | `store.Contains(q)` |
| SPARQL Query | `store.Query(sparql)` |
| SPARQL Update | `store.Update(sparql)` |

**持久化兼容性**：Oxigraph.NET 与 pyoxigraph 均通过同一 Oxigraph Rust 库使用 RocksDB，因此直接复用存储目录具备技术可行性，但上游未明确承诺跨绑定、跨版本的目录兼容性。迁移时必须复制存储目录，在副本上用 .NET 只读打开并校验四元组总数、具名图集合和抽样查询；校验通过后才允许切换。校验失败时，从 Python 端导出 N-Quads，再由 .NET 端导入。任何时候都禁止 Python 与 .NET 进程并发打开同一 RocksDB 目录。

### 2.2 dotNetRDF 3.5.2

```
包: dotNetRDF 3.5.2
目标框架: net10.0
```

**补充能力：**

- `ShapesGraph.Validate(IGraph data, IGraph shapes)` — SHACL 验证
- `CompressingTurtleWriter` — Turtle 导出
- `JsonLdWriter` — JSON-LD 导出
- `RdfXmlWriter` — RDF/XML 导出

**从 dotNetRDF 导入 Oxigraph：**

```csharp
// via Oxigraph.Extensions.DotNetRDF
var store = new Store(path);
var graph = new Graph();
store.LoadFromGraph(graph); // 扩展方法
```

`Oxigraph.Extensions.DotNetRDF 0.5.8` 只提供 dotNetRDF → Oxigraph 的节点、三元组和图转换。Oxigraph → dotNetRDF 不依赖未提供的反向扩展方法，统一通过 Turtle、N-Triples 或 N-Quads 序列化后交给 dotNetRDF 解析。

### 2.3 互操作桥

```
Oxigraph.Extensions.DotNetRDF 0.5.8
├── Oxigraph 0.5.8
└── dotNetRDF 3.5.2
```

---

## 3. 项目结构

```
src/
├── OnToPilot/                      # 主后端项目 (ASP.NET Core 10)
│   ├── Controllers/                # API 控制器（一对一映射现有 FastAPI 路由）
│   │   ├── AuthController.cs
│   │   ├── DocumentsController.cs
│   │   ├── ExtractionController.cs
│   │   ├── KnowledgeController.cs
│   │   ├── VocabularyController.cs
│   │   ├── ReleasesController.cs
│   │   ├── ConflictsController.cs
│   │   ├── ResolutionController.cs
│   │   ├── McpTokensController.cs
│   │   ├── AccessTokensController.cs
│   │   ├── SettingsController.cs
│   │   └── HealthController.cs
│   ├── Models/                     # 数据库模型（EF Core）
│   │   ├── User.cs
│   │   ├── KnowledgeSystem.cs
│   │   ├── Document.cs
│   │   ├── ExtractionTask.cs
│   │   ├── PromptSnapshot.cs
│   │   └── AuditLog.cs
│   ├── Services/                   # 业务编排层（调用 Ontology + LLM）
│   │   ├── IntegrationApiFacade.cs # 对外 API 聚合入口（MCP Tools 依赖于此）
│   │   ├── ExtractionOrchestrator.cs
│   │   └── MigrationCoordinator.cs # 迁移阶段协调（SQL + RDF + MinIO）
│   ├── Ontology/                   # RDF 本体逻辑（领域层）
│   │   ├── StoreWrapper.cs         # Oxigraph.NET 封装（替代 store.py）
│   │   ├── SchemaBuilder.cs        # 替代 schema.py
│   │   ├── SkosManager.cs          # 替代 skos.py
│   │   ├── ABoxManager.cs          # 替代 abox.py
│   │   ├── ConflictDetector.cs     # 替代 conflicts.py
│   │   ├── ReleaseManager.cs       # 替代 release_service.py
│   │   ├── TBoxGuard.cs            # TBox 守卫逻辑
│   │   └── ShaclValidator.cs       # dotNetRDF SHACL 验证
│   ├── Parsing/                    # 文档解析与分块
│   │   ├── DocumentParser.cs
│   │   └── Chunker.cs
│   ├── Llm/                        # LLM 基础设施（Provider 封装）
│   │   ├── LlmClientFactory.cs     # IChatClient 工厂（替代 openrouter.py）
│   │   └── EmbeddingGenerator.cs   # IEmbeddingGenerator 实现
│   ├── Mcp/                        # MCP 服务器（ModelContextProtocol）
│   │   ├── OnToPilotMcpTools.cs   # MCP Tool 实现（[McpServerTool]）
│   │   ├── OnToPilotMcpResources.cs # MCP Resource 实现（[McpServerResource]）
│   │   └── OnToPilotMcpPrompts.cs # MCP Prompt 实现（[McpServerPrompt]）
│   ├── Storage/                    # 对象存储（MinIO）
│   │   └── MinioBlobStore.cs
│   ├── Middleware/
│   │   └── SessionAuthMiddleware.cs
│   └── Program.cs
├── OnToPilot.Domain/               # 共享领域模型（不得引用 ASP.NET Core）
│   ├── Common/
│   │   ├── Entity.cs               # 基础 Entity<TId>
│   │   ├── ValueObject.cs          # 基础 ValueObject
│   │   └── DomainEvent.cs          # Domain Event 标记接口
│   ├── KnowledgeSystem/
│   │   ├── KnowledgeSystemId.cs    # 值对象
│   │   ├── UserId.cs               # 值对象
│   │   └── Events/                 # 领域事件
│   │       ├── TBoxChangedEvent.cs
│   │       └── ReleasePublishedEvent.cs
│   └── Shared/
│       ├── IRepository.cs
│       └── IDomainService.cs
└── OnToPilot.Tests/                # 单元测试
    ├── Ontology/
    │   ├── StoreWrapperTests.cs
    │   ├── SchemaBuilderTests.cs
    │   └── ShaclValidatorTests.cs
    └── ...
```

---

## 4. 模块迁移映射

### 4.1 RDF 存储层

**文件**: `store.py` (337 行) → `StoreWrapper.cs`

核心职责不变：四元组读写、具名图管理、变更捕获（`capture`）、回滚支持。

```csharp
// 新增: Oxigraph.NET Store 封装
public sealed class StoreWrapper : IDisposable
{
    private readonly Store _store;
    private readonly Dictionary<string, ManualResetEvent> _graphLocks;

    public void AddQuads(string graphIri, IEnumerable<Quad> quads);
    public void RemoveQuads(string graphIri, IEnumerable<Quad> quads);
    public IReadOnlyList<Quad> Match(string? subject, NamedNode? predicate, ITerm? obj, IGraphName? graph);
    public string DumpNQuads(string graphIri);
    public void BulkExtend(string graphIri, IEnumerable<Quad> quads);
    public ulong Count(string graphIri);
    public bool ContainsQuad(string graphIri, Quad quad);

    // 变更捕获（对应 Python _Recorder）
    public QuadChangeCapture? Capture(string graphIri);
}

public sealed class QuadChangeCapture : IDisposable
{
    public (byte[] Added, byte[] Removed) Diff();
    public void Revert();
}
```

**兼容性**：优先在存储副本上验证跨绑定直接复用；验证不通过时使用 N-Quads 导出/导入，不依赖 RocksDB 内部格式兼容。

### 4.2 TBox 构建

**文件**: `schema.py` (376 行) → `SchemaBuilder.cs`

将 `build_mutation()` 和 `build_view()` 从 Python 映射到 C#。

### 4.3 SKOS 词汇

**文件**: `skos.py` (569 行) → `SkosManager.cs`

SKOS ConceptScheme / Concept 增删改查逻辑。

### 4.4 ABox 管理

**文件**: `abox.py`, `abox_extract.py` → `ABoxManager.cs`

### 4.5 TBox Guard

**文件**: `tbox_guard.py` (283 行) → `TBoxGuard.cs`

使用 dotNetRDF ShapesGraph 替代 Python 的手动检查。

```csharp
// TBox Guard 检查项 → SHACL Shapes
// 1. 类的定义域/值域一致性
// 2. 属性类型唯一性（ObjectProperty vs DatatypeProperty 互斥）
// 3. 无循环 subclass-of
// 4. disjointWith 一致性
```

### 4.6 发布服务

**文件**: `release_service.py` (337 行) → `ReleaseManager.cs`

### 4.7 冲突检测

**文件**: `conflicts.py` (609 行) → `ConflictDetector.cs`

### 4.8 RDF 导入

**文件**: `rdf_import.py` (354 行) → `RdfImportService.cs`

使用 Oxigraph.NET 的 `LoadFromFile()` / `Load()` 方法。

### 4.9 LLM 抽取

**文件**: `extract.py` (1731 行) → `ExtractionService.cs`

使用 **Microsoft.Extensions.AI**（`IChatClient` 接口）替代 Python `openrouter.py`，参考 [OpenClaw.Gateway.Extensions.LlmClientFactory](E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Extensions\LlmClientFactory.cs) 的实现模式。

```csharp
// Microsoft.Extensions.AI 抽象层
using Microsoft.Extensions.AI;

// 支持的 Provider（与 openrouter.py 兼容）
// openai / deepseek / anthropic / gemini / ollama / azure-openai / openai-compatible
public sealed class ExtractionService
{
    private readonly IChatClient _chatClient;

    public ExtractionService(IChatClient chatClient) => _chatClient = chatClient;

    public async Task<ExtractionResult> ExtractAsync(
        string prompt,
        DocumentChunk chunk,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, BuildUserMessage(chunk))
        };

        var response = await _chatClient.GetResponseAsync(messages, options: null, ct);
        return ParseExtractionResult(response);
    }
}
```

**Provider 路由逻辑（参考 `LlmClientFactory`）：**

```csharp
IChatClient CreateChatClient(LlmProviderConfig config) => config.Provider.ToLowerInvariant() switch
{
    "openai" or "deepseek" or "groq" or "together" or "lmstudio" =>
        new OpenAI.OpenAIClient(apiKey, options)
            .GetChatClient(config.Model)
            .AsIChatClient(),
    "anthropic" or "claude" =>
        new AnthropicClient { ApiKey = config.ApiKey, BaseUrl = config.Endpoint }
            .AsIChatClient(config.Model),
    "gemini" or "google" =>
        new GeminiChatClient(new GeminiClientOptions { ApiKey = config.ApiKey }),
    "ollama" =>
        new OllamaChatClient(config.Endpoint, config.Model),
    "azure-openai" =>
        new AzureOpenAIClient(config.Endpoint, new AzureKeyCredential(config.ApiKey))
            .GetChatClient(config.Model),
    _ => throw new InvalidOperationException($"Unsupported provider: {config.Provider}")
};
```

**向量嵌入（对应 `embeddings.py`）：**

```csharp
// IEmbeddingGenerator<string, Embedding<float>>
public interface IEmbeddingGenerator<TTerm, TEmbedding>
{
    Task<GeneratedEmbeddings<TEmbedding>> GenerateAsync(
        IEnumerable<TTerm> values,
        EmbeddingGeneratorOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

**参考实现：**

- 完整 Provider 工厂：[LlmClientFactory.cs](E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Extensions\LlmClientFactory.cs)
- Provider 注册中心：[LlmProviderRegistry.cs](E:\GitHub\openclaw.net\src\OpenClaw.Gateway\LlmProviderRegistry.cs)
- AgentRuntime 中的实际调用模式：[AgentRuntime.cs](E:\GitHub\openclaw.net\src\OpenClaw.Agent\AgentRuntime.cs)

### 4.10 REST API 控制器

每个 FastAPI 路由模块对应一个 ASP.NET Core Controller：

| FastAPI 模块 | ASP.NET Core Controller |
|---|---|
| `abox.py` | `AboxController` |
| `auth.py` | `AuthController` |
| `conflicts.py` | `ConflictsController` |
| `documents.py` | `DocumentsController` |
| `extraction.py` | `ExtractionController` |
| `knowledge.py` | `KnowledgeController` |
| `mcp_tokens.py` | `McpTokensController` |
| `ontology.py` | `OntologyController` |
| `prompts.py` | `PromptsController` |
| `providers.py` | `ProvidersController` |
| `published.py` | `PublishedController` |
| `releases.py` | `ReleasesController` |
| `resolution.py` | `ResolutionController` |
| `settings_api.py` | `SettingsController` |
| `tokens.py` | `TokensController` |
| `vocabulary.py` | `VocabularyController` |
| `history.py` | `HistoryController` |
| `rdf_import.py` | `RdfImportController` |
| `external.py` | `ExternalApiController` |

**API 契约兼容性**：所有 Controller 返回的 JSON 结构与现有 FastAPI 路由完全一致，External API 遵循 [docs/external-api.zh-CN.md](../../external-api.zh-CN.md) 定义的 Token Scope 体系。

---

### 4.11 文档解析与分块

**文件**: `parsing/parser.py` (131 行) + `parsing/chunker.py` (272 行) → `Parsing/` 目录

#### 4.11.1 文档解析（Parser）

使用 **[DoclingDotNet](https://github.com/sparkeh9/DoclingDotNet)** `1.2.0` — Docling 的纯 .NET 高性能移植版，无需 Python 运行时或原生库。

**支持格式对照：**

| 文件格式 | Python（当前） | .NET（DoclingDotNet 1.2.0） |
| --- | --- | --- |
| PDF / DOCX / XLSX / PPTX / MD / HTML / EPUB | `docling` | **DoclingDotNet**（布局感知，保留 heading/table/list/caption 结构） |
| PDF 降级 | `pypdf` | **PdfPig** |
| DOCX 降级 | `python-docx` | **DocumentFormat.OpenXml** |
| XLSX 降级 | `openpyxl` | **ClosedXML** |
| TXT / MD / CSV | 内联 `path.read_text()` | `File.ReadAllText()` |

**DoclingDotNet 用法：**

```csharp
var converter = new DocumentConverter();
var result = await converter.ConvertAsync(filePath);  // 文件路径字符串重载
// result.Document.ExportToMarkdown() — Markdown 文本
// result.Document — 结构化文档，用于 HybridChunker
```

**分层降级策略（与 Python 版一致）：**

```csharp
public sealed class DocumentParser : IDocumentParser
{
    public async Task<ParseResult> ParseAsync(Stream stream, string extension, CancellationToken ct = default)
    {
        // 1. 优先 DoclingDotNet（布局感知）
        var doclingResult = await TryDoclingDotNetAsync(stream, extension, ct);
        if (doclingResult is not null && !string.IsNullOrWhiteSpace(doclingResult.Text))
            return doclingResult;

        // 2. 降级到格式专用解析器
        return extension.ToLowerInvariant() switch
        {
            "pdf" => await ParsePdfAsync(stream, ct),
            "docx" or "doc" => await ParseDocxAsync(stream, ct),
            "xlsx" or "xls" => await ParseXlsxAsync(stream, ct),
            "txt" or "md" or "markdown" or "csv" => ParseText(stream),
            _ => throw new NotSupportedException($"Unsupported extension: .{extension}")
        };
    }

    private async Task<ParseResult> TryDoclingDotNetAsync(Stream stream, string extension, CancellationToken ct)
    {
        try
        {
            // DoclingDotNet.ConvertAsync(filePath) 需要文件路径，
            // 故先将 Stream 写入临时文件
            var tempPath = Path.GetTempFileName();
            await using (var fileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write))
            {
                await stream.CopyToAsync(fileStream, ct);
            }

            var converter = new DocumentConverter();
            var result = await converter.ConvertAsync(tempPath);
            var markdown = result.Document.ExportToMarkdown();

            // DoclingDotNet 不可用判断：解析成功但返回空文本（异常或质量问题）
            if (string.IsNullOrWhiteSpace(markdown))
            {
                _logger.LogWarning(
                    "DoclingDotNet returned empty markdown for extension {Extension}. Falling back.",
                    extension);
                return null;
            }

            return new ParseResult(markdown, "doclingdotnet", result.Document);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DoclingDotNet failed for extension {Extension}. Falling back.",
                extension);
            return null;
        }
    }

    private async Task<ParseResult> ParsePdfAsync(Stream stream, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        memory.Position = 0;
        using var document = PdfDocument.Open(memory);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
            sb.AppendLine($"## Page {page.Number}\n{page.Text}");
        return new ParseResult(sb.ToString(), "pdfpig");
    }

    private async Task<ParseResult> ParseDocxAsync(Stream stream, CancellationToken ct)
    {
        using var doc = new WordprocessingDocument(stream);
        var body = doc.MainDocumentPart?.Document.Body;
        var sb = new StringBuilder();
        foreach (var para in body?.Elements<Paragraph>() ?? [])
        {
            var t = para.InnerText;
            if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t);
        }
        foreach (var table in body?.Elements<Table>() ?? [])
            foreach (var row in table.Elements<TableRow>())
                sb.AppendLine(string.Join(" | ", row.Elements<TableCell>().Select(c => c.InnerText.Trim())));
        return new ParseResult(sb.ToString(), "openxml");
    }

    private async Task<ParseResult> ParseXlsxAsync(Stream stream, CancellationToken ct)
    {
        using var wb = new XLWorkbook(stream);
        var sb = new StringBuilder();
        foreach (var ws in wb.Worksheets)
        {
            sb.AppendLine($"## Sheet: {ws.Name}");
            foreach (var row in ws.RowsUsed())
            {
                var cells = row.Cells().Select(c => c.GetString());
                if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                    sb.AppendLine(string.Join("\t", cells));
            }
        }
        return new ParseResult(sb.ToString(), "closedxml");
    }

    private ParseResult ParseText(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        return new ParseResult(reader.ReadToEnd(), "text");
    }
}

public sealed record ParseResult(
    string Text,
    string Backend,
    object? StructuredDocument = null);

public interface IDocumentParser
{
    Task<ParseResult> ParseAsync(Stream stream, string extension, CancellationToken ct = default);
}
```

**NuGet 包：**

```xml
<PackageReference Include="DoclingDotNet" Version="1.2.0" />
<PackageReference Include="PdfPig" Version="0.1.15" />
<PackageReference Include="DocumentFormat.OpenXml" Version="3.3.0" />
<PackageReference Include="ClosedXML" Version="0.104.1" />
```

#### 4.11.2 文档分块（Chunker）

`chunker.py` 的分块逻辑为纯算法，无外部依赖，直接移植为 C#：

**核心组件对照：**

| chunker.py | C# 实现 |
|---|---|
| `_estimate_tokens(text)` | `TokenEstimator.Estimate(string text)` |
| `_TOKEN_PIECES` 正则 | `TokenEstimator.TOKEN_PIECES` (C# Regex) |
| `_PARA_SPLIT` / `_SENTENCE_END` 正则 | `Chunker.PARA_SPLIT` / `Chunker.SENTENCE_END` |
| `chunk_text(text, size, overlap)` | `Chunker.Chunk(string text, int? sizeChars, int? overlapChars)` |
| `chunk_docling_document(document)` | `Chunker.ChunkStructured(object structuredDoc)` |
| `chunk_document(text, structured_doc)` | `Chunker.ChunkDocument(ParseResult)` — 优先结构化分块 |

```csharp
public sealed class TokenEstimator
{
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var total = 0;
        foreach (Match piece in TOKEN_PIECES.Matches(text))
        {
            var s = piece.Value;
            if (s.Length > 0 && char.IsAscii(s[0]) && char.IsLetterOrDigit(s[0]))
                total += Math.Max(1, (int)Math.Ceiling(s.Length / 4.0));
            else
                total += 1;
        }
        return Math.Max(1, total);
    }

    private static readonly Regex TOKEN_PIECES = new(
        @"[㐀-䶿一-鿿豈-﫿]|[A-Za-z0-9_]+|[^\s]",
        RegexOptions.Compiled);
}

public sealed record ChunkSpan(int Idx, string Text, int CharStart, int CharEnd, int TokenEstimate);

public sealed class Chunker
{
    private readonly int _defaultChunkChars;
    private readonly int _defaultOverlapChars;
    private readonly int _defaultChunkTokens;

    public Chunker(int defaultChunkChars = 800, int defaultOverlapChars = 200, int defaultChunkTokens = 256)
    {
        _defaultChunkChars = defaultChunkChars;
        _defaultOverlapChars = defaultOverlapChars;
        _defaultChunkTokens = defaultChunkTokens;
    }

    /// <summary>主入口：优先用 DoclingDotNet 结构化文档分块，否则纯文本分块。</summary>
    public IReadOnlyList<ChunkSpan> ChunkDocument(ParseResult parseResult)
    {
        if (parseResult.StructuredDocument is not null)
        {
            var spans = ChunkStructured(parseResult.StructuredDocument);
            if (spans.Count > 0) return spans;
        }
        return Chunk(parseResult.Text);
    }

    /// <summary>纯文本分块（对应 Python chunk_text）。</summary>
    public IReadOnlyList<ChunkSpan> Chunk(string text, int? sizeChars = null, int? overlapChars = null);

    /// <summary>结构化文档分块（对应 Python chunk_docling_document）。</summary>
    private IReadOnlyList<ChunkSpan> ChunkStructured(object structuredDoc);
}
```

**配置映射：**

| Python `settings` | C# `ChunkerOptions` |
|---|---|
| `chunk_size_tokens` | `ChunkerOptions.DefaultChunkTokens` |
| `chunk_size_chars` | `ChunkerOptions.DefaultChunkChars` |
| `chunk_overlap_chars` | `ChunkerOptions.DefaultOverlapChars` |

---

## 5. 数据库迁移

### 5.1 SQL 层

- **当前**: SQLAlchemy + asyncpg + PostgreSQL（生产）/ SQLite（开发）
- **迁移后**: EF Core **10** + Npgsql（PostgreSQL）

> ⚠️ 目标框架为 .NET 10，必须使用 EF Core 10（随 .NET 10 发布），不得使用 EF Core 8。

数据模型一对一映射，迁移脚本处理存量数据，详见 [Section 6.2  SQL 迁移](#62-sql-存量数据迁移)。

### 5.2 RDF 数据

Oxigraph 持久化目录（`data/oxigraph/`）优先采用“副本验证后复用”，不能预先视为无需迁移：

```csharp
// Python
store = Store("data/oxigraph")

// C# — 首次验证只打开复制后的目录
using var store = Store.OpenReadOnly("data/oxigraph-migration-copy");
```

迁移验证必须满足以下条件：

1. 停止写入并复制 Python 版存储目录，保留原目录作为回滚点。
2. 使用 .NET `Store.OpenReadOnly()` 打开副本。
3. 对比四元组总数、具名图集合，并执行固定 SPARQL 抽样查询。
4. 验证通过后，才允许 .NET 独占打开正式目录并执行写入冒烟测试。
5. 任一步失败则放弃目录复用，改走 Python 导出 N-Quads、.NET 导入新目录的逻辑迁移。

Python 与 .NET 进程不得并发访问同一个 RocksDB 目录。

### 5.3 对象存储（MinIO）

当前 `storage/blobstore.py` 实现本地文件系统内容寻址存储（CAS），迁移后使用 MinIO（S3 兼容对象存储）。

**功能对照：**

| blobstore.py | .NET MinIO 实现 |
|---|---|
| SHA-256 内容寻址 | MinIO `PutObject` / `GetObject`（key = SHA-256） |
| 两级分片 `blobs/<aa>/<bb>/<sha256>` | MinIO Bucket 内直接以 `sha256` 为 key（无需分片） |
| 原子写入（temp + rename） | MinIO 单次 `PutObject`（内置原子性） |
| `store_bytes()` | `BlobStore.PutAsync(sha256, stream)` |
| `read_bytes()` | `BlobStore.GetAsync(sha256)` → `Stream` |
| `delete()` | `BlobStore.RemoveAsync(sha256)` |

**MinIO 客户端包：**

```text
AWSSDK.S3（Amazon S3 SDK for .NET）
// 或
Minio（社区包，API 更贴近 MinIO 语义）
```

**推荐使用 `AWSSDK.S3`**，因为：

- MinIO 兼容 S3 API，`AWSSDK.S3` 开箱即用
- 与 ASP.NET Core `IFormFile` / `Stream` 集成顺畅
- 支持预签名 URL（用于直接前端上传）

```csharp
// MinIO BlobStore 实现
public sealed class MinioBlobStore : IBlobStore
{
    private readonly AmazonS3Client _s3;

    public MinioBlobStore(string endpoint, string accessKey, string secretKey, string bucket)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,        // e.g. "http://localhost:9000"
            ForcePathStyle = true        // MinIO 需要
        };
        _s3 = new AmazonS3Client(accessKey, secretKey, config);
        _bucket = bucket;
    }

    public async Task<string> PutAsync(Stream data, string sha256, CancellationToken ct = default)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = sha256,               // 内容寻址 key = SHA-256
            InputStream = data,
        }, ct);
        return sha256;
    }

    public async Task<Stream?> GetAsync(string sha256, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, sha256, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> RemoveAsync(string sha256, CancellationToken ct = default)
    {
        try
        {
            await _s3.DeleteObjectAsync(_bucket, sha256, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> ExistsAsync(string sha256, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, sha256, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}

public interface IBlobStore
{
    Task<string> PutAsync(Stream data, string sha256, CancellationToken ct = default);
    Task<Stream?> GetAsync(string sha256, CancellationToken ct = default);
    Task<bool> RemoveAsync(string sha256, CancellationToken ct = default);
    Task<bool> ExistsAsync(string sha256, CancellationToken ct = default);
}
```

**环境变量映射：**

| .env 变量 | MinIO 含义 |
|---|---|
| `MINIO_ENDPOINT` | MinIO 服务地址（如 `localhost:9000`） |
| `MINIO_ACCESS_KEY` | Access Key |
| `MINIO_SECRET_KEY` | Secret Key |
| `MINIO_BUCKET` | Bucket 名称（默认 `ontopilot-blobs`） |
| `MINIO_USE_SSL` | `false`（本地开发） |

**数据迁移（本地文件 → MinIO）：**

```bash
# 一次性脚本：将现有 blob 目录迁移到 MinIO
mc alias set local http://localhost:9000 $MINIO_ACCESS_KEY $MINIO_SECRET_KEY
mc mirror backend/data/blobs local/ontopilot-blobs/
```

**与现有代码的衔接：**

- `Document` 模型中的文件上传路径改为返回 SHA-256 key
- 前端通过预签名 URL 直接上传到 MinIO（绕过后端）
- 发布制品（release/ 目录下的 `.nq`、`.jsonl`）仍写入 Oxigraph，MinIO 仅存储源文档

### 5.4 Docker 迁移

#### docker-compose.yml

重写 backend 服务，新增 minio 服务：

```yaml
services:
  postgres:          # 不变
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: ontopilot
      POSTGRES_USER: ontopilot
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?Set POSTGRES_PASSWORD in the root .env file}
    volumes:
      - ontopilot-postgres:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ontopilot -d ontopilot"]
      interval: 5s
      timeout: 5s
      retries: 20
    restart: unless-stopped

  minio:             # 新增
    image: minio/minio:latest
    environment:
      MINIO_ROOT_USER: ${MINIO_ACCESS_KEY:?Set MINIO_ACCESS_KEY}
      MINIO_ROOT_PASSWORD: ${MINIO_SECRET_KEY:?Set MINIO_SECRET_KEY}
    command: server /data --console-address ":9001"
    volumes:
      - ontopilot-minio:/data
    healthcheck:
      test: ["CMD", "mc", "ready", "local"]
      interval: 10s
      timeout: 5s
      retries: 5
    restart: unless-stopped
    expose:
      - "9000"   # S3 API
      - "9001"   # Console

  backend:           # 重写
    build: ./backend
    env_file: ./backend/.env
    environment:
      DATABASE_HOST: postgres
      DATABASE_PORT: 5432
      DATABASE_NAME: ontopilot
      DATABASE_USER: ontopilot
      DATABASE_PASSWORD: ${POSTGRES_PASSWORD}
      SYSTEM_LANGUAGE: ${SYSTEM_LANGUAGE:-en}
      MCP_PUBLIC_URL: ${MCP_PUBLIC_URL:-http://localhost:8080/mcp}
      MINIO_ENDPOINT: minio:9000
      MINIO_ACCESS_KEY: ${MINIO_ACCESS_KEY}
      MINIO_SECRET_KEY: ${MINIO_SECRET_KEY}
      MINIO_BUCKET: ${MINIO_BUCKET:-ontopilot-blobs}
      MINIO_USE_SSL: "false"
    depends_on:
      postgres:
        condition: service_healthy
      minio:
        condition: service_healthy
    volumes:
      - ontopilot-data:/app/data
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://localhost:8080/health || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 20
    restart: unless-stopped
    expose:
      - "8080"

  frontend:          # 不变
    build: ./frontend
    depends_on:
      backend:
        condition: service_healthy
    ports:
      - "${ONTOPILOT_BIND_ADDRESS:-0.0.0.0}:${ONTOPILOT_PORT:-8080}:80"
    restart: unless-stopped

volumes:
  ontopilot-data:
  ontopilot-postgres:
  ontopilot-minio:   # 新增
```

#### backend/Dockerfile

Python 3.12 替换为 .NET 10 多阶段构建：

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 复制项目文件并恢复依赖
COPY src/OnToPilot/OnToPilot.csproj ./OnToPilot/
RUN dotnet restore OnToPilot/OnToPilot.csproj

# 复制源码并发布
COPY src/OnToPilot/ ./OnToPilot/
RUN dotnet publish OnToPilot/OnToPilot.csproj \
    -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# ASP.NET Core 默认监听 8080（而非 5000），与 docker-compose expose 一致
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OnToPilot.dll"]
```

#### 新增环境变量（backend/.env.example）

| 变量 | 说明 | 示例 |
|------|------|------|
| `MINIO_ACCESS_KEY` | MinIO Access Key | `ontopilot` |
| `MINIO_SECRET_KEY` | MinIO Secret Key | `ontopilot123` |
| `MINIO_BUCKET` | Bucket 名称 | `ontopilot-blobs` |

#### 启动命令（不变）

```bash
cp backend/.env.example backend/.env
# 编辑 .env，填入 POSTGRES_PASSWORD、MINIO_ACCESS_KEY、MINIO_SECRET_KEY
docker compose up -d --build
open http://localhost:8080
```

#### 数据迁移（本地 blobs → MinIO）

存量 blob 目录一次性迁移到 MinIO：

```bash
# 使用 mc（MinIO Client）镜像执行迁移，无需本地安装
docker run --rm \
  -e MINIO_ACCESS_KEY=$MINIO_ACCESS_KEY \
  -e MINIO_SECRET_KEY=$MINIO_SECRET_KEY \
  minio/mc \
  sh -c "\
    mc alias set local http://minio:9000 $MINIO_ACCESS_KEY $MINIO_SECRET_KEY && \
    mc mirror /app/data/blobs local/ontopilot-blobs/ \
  "
```

---

## 6. 测试、CI/CD、可观测性与部署

### 6.1 测试策略

#### 单元测试

覆盖核心本体逻辑，不得依赖外部服务（数据库、Oxigraph 存储、LLM Provider）：

| 测试文件 | 覆盖范围 |
|---|---|
| `StoreWrapperTests.cs` | 四元组 CRUD、变更捕获、回滚 |
| `SchemaBuilderTests.cs` | TBox 构建、build_mutation / build_view |
| `ShaclValidatorTests.cs` | SHACL 验证（逐项对照 TBox Guard） |
| `ChunkerTests.cs` | Token 估算、分块算法、overlap |
| `TokenEstimatorTests.cs` | 中英文 token 计数精度 |
| `IntegrationApiFacadeTests.cs` | 各 Tool 方法的正常/异常路径 |

#### 集成测试

使用 `Testcontainers` 启动真实依赖：

```csharp
// 使用 Testcontainers.PostgreSql 测试 EF Core 迁移
// 使用 Testcontainers.MinIo 测试 BlobStore
[Fact]
public async Task EF_Core_migration_scripts_should_produce_identical_schema()
{
    await using var container = new PostgreSqlBuilder().Build();
    await container.StartAsync();

    var options = new DbContextOptionsBuilder<OnToPilotDbContext>()
        .UseNpgsql(container.GetConnectionString())
        .Options;

    // 应用所有 EF Core 迁移
    using var ctx = new OnToPilotDbContext(options);
    ctx.Database.Migrate();

    // 验证表结构与 SQLAlchemy 模型一致
    var tables = await ctx.Database.SqlQuery<string>(
        $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'")
        .ToListAsync();

    Assert.Contains("users", tables);
    Assert.Contains("knowledge_systems", tables);
    Assert.Contains("documents", tables);
}
```

#### API 契约测试

在 CI 中用 [Netlingeri](https://github.com/omaind/Netlingeri) 或 `curl` + 动态端口验证 FastAPI 与 ASP.NET Core 返回结构完全一致：

```bash
# 冒烟测试：比较两者的 /api/v1/knowledge 响应 JSON Schema
python -c "import requests; print(sorted(requests.get('http://localhost:8000/api/v1/knowledge').json().keys()))"
dotnet run --urls=http://localhost:8080 &
sleep 5
dotnet run --project tests/OnToPilot.ApiContract.Tests \
    -- --python-url=http://localhost:8000 --dotnet-url=http://localhost:8080
```

#### 端到端测试

用 Playwright 覆盖关键用户路径：

- 上传 PDF → 抽取本体 → 发布
- SKOS 词汇增删改
- MCP Tool 调用（`ontopilot.get_ontology` 等）

### 6.2 SQL 存量数据迁移

#### 迁移脚本位置

```
migrations/
└── SqlAlchemyToEfCore/
    ├── 001_generate_guid_ids.sql      # 为所有表生成新 GUID 主键
    ├── 002_export_data.sql            # 导出 CSV（绕过自增 ID）
    ├── 003_import_ef_core.sql         # 导入 EF Core 兼容格式
    └── rollback.sql                   # 回滚脚本（恢复自增 ID）
```

#### ID 映射策略

| 当前（SQLAlchemy） | 迁移后（EF Core） |
|---|---|
| `id` INTEGER AUTOINCREMENT | `Id` GUID（`gen_random_uuid()`） |
| 外键 | 保留为 `uuid`，级联更新 |

执行步骤：

```bash
# 1. 在备份库验证
pg_dump -h $PROD_HOST -U ontopilot -d ontopilot \
    -Fc -f /tmp/ontopilot_backup.dump
docker compose exec postgres pg_restore \
    -d ontopilot_test /tmp/ontopilot_backup.dump

# 2. 生成 GUID 并迁移数据
psql -d ontopilot_test -f migrations/SqlAlchemyToEfCore/001_generate_guid_ids.sql
psql -d ontopilot_test -f migrations/SqlAlchemyToEfCore/002_export_data.sql
# (ETL 脚本将 CSV 导入 EF Core 格式)
psql -d ontopilot_test -f migrations/SqlAlchemyToEfCore/003_import_ef_core.sql

# 3. 验证行数一致
python -c "import asyncpg; ..." # Python 端行数
dotnet ef database shell -c "SELECT COUNT(*) FROM users;" # .NET 端行数

# 4. 生产执行（停机窗口）
docker compose stop backend
psql -d ontopilot -f migrations/SqlAlchemyToEfCore/001_generate_guid_ids.sql
# ... 后续步骤同上
docker compose up -d backend-dotnet
```

#### 并发策略

迁移窗口期间**禁止 Python 后端写入 PostgreSQL**。通过以下方式之一实现：

- 停止 Python backend 容器（推荐）
- 或在 PostgreSQL 中撤销 `ontopilot` 用户的写权限（`REVOKE UPDATE ON ALL TABLES IN SCHEMA public FROM ontopilot;`）

#### 回滚预案

若迁移失败，执行 `migrations/SqlAlchemyToEfCore/rollback.sql`，然后重启 Python backend。

### 6.3 可观测性基础设施

#### 结构化日志（Serilog）

```csharp
// Program.cs
builder.Services.AddSerilog((services, config) => config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "OnToPilot")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteTo.File(
        new RenderedCompactJsonFormatter(),
        "/app/logs/ontopilot-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30));
```

#### OpenTelemetry 埋点

所有关键路径均注入 `ActivitySource`：

```csharp
// LLM 调用埋点
public sealed class LlmCallActivitySource
{
    public const string Name = "OnToPilot.Llm";
    public static readonly ActivitySource Instance = new(Name, "1.0.0");
}

public async Task<ExtractionResult> ExtractAsync(...)
{
    using var activity = LlmCallActivitySource.Instance.StartActivity("Llm.Extract");
    activity?.SetTag("llm.provider", _chatClient.GetType().Name);
    activity?.SetTag("llm.model", _model);

    var sw = Stopwatch.StartNew();
    try
    {
        var response = await _chatClient.GetResponseAsync(...);
        activity?.SetTag("llm.duration_ms", sw.ElapsedMilliseconds);
        activity?.SetTag("llm.success", true);
        return ParseExtractionResult(response);
    }
    catch (Exception ex)
    {
        activity?.SetTag("llm.success", false);
        activity?.SetTag("error.type", ex.GetType().Name);
        throw;
    }
}
```

**埋点覆盖范围：**

| 路径 | Activity 名称 |
|---|---|
| LLM 抽取 | `Llm.Extract` |
| RDF 读写 | `Rdf.StoreWrapper.*` |
| SHACL 验证 | `Rdf.Shacl.Validate` |
| 文档解析 | `Parsing.Parse` |
| MinIO 上传/下载 | `Storage.Minio.*` |
| MCP Tool 调用 | `Mcp.Tool.*` |

#### 指标（.NET Metrics API）

```csharp
// 在关键路径注册计数器
public static readonly Meter Meter = new("OnToPilot", "1.0.0");
public static readonly Counter<long> ExtractionCounter =
    Meter.CreateCounter<long>("ontopilot.extraction.total", description: "Total extraction requests");
public static readonly Histogram<double> ExtractionDuration =
    Meter.CreateHistogram<double>("ontopilot.extraction.duration_ms", "Extraction duration in ms");
```

**Grafana 看板（建议）：**

- 请求 QPS / 延迟 P50 / P99
- LLM Provider 错误率（按 Provider 分组）
- RDF 存储四元组总数趋势
- MinIO Bucket 大小趋势
- MCP Tool 调用成功率

### 6.4 迁移执行序列

以下为完整迁移步骤，明确了 MinIO 和 RDF 存储的执行顺序：

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        迁移执行序列                                      │
├─────────────────────────────────────────────────────────────────────────┤
│ T-7d   1. [准备] 在备份环境完成 SQL 迁移脚本验证（见 6.2）              │
│        2. [准备] 完成 RDF 存储副本只读验证（见 5.2）                    │
│        3. [准备] 完成 MinIO mc mirror 预演（dry-run）                   │
│                                                                         │
│ T-0    4. [停写] 停止 Python backend，撤销 PostgreSQL 写权限            │
│        5. [备份] pg_dump 全量备份 PostgreSQL                            │
│        6. [复制] 复制 RDF 存储目录 → oxigraph-migration-copy/          │
│        7. [并行] RDF 副本只读验证 ＋ MinIO mc mirror 同步 blob          │
│        8. [验证] RDF 写入冒烟测试（.NET 独占打开，写入后回滚）          │
│        9. [验证] MinIO bucket 完整性与 Python 端 blob 列表对比          │
│        10. [SQL 迁移] 执行 001~003 脚本，验证行数一致                   │
│        11. [切换] 启动 .NET backend，切换前端 API 环境变量              │
│        12. [观察] 监控 24h，无异常则进入确认阶段                        │
│                                                                         │
│ T+1d   13. [确认] 删除 Python 环境和原 RDF 目录（保留备份 30d）         │
└─────────────────────────────────────────────────────────────────────────┘
```

**关键约束：**

- 步骤 7（MinIO 迁移）和步骤 8（RDF 验证）可并行执行
- 步骤 4~12 之间 Python backend 必须停止，PostgreSQL 只能读
- 步骤 12 观察期内若有问题，立即执行回滚（停止 .NET，重启 Python，还原 PostgreSQL 权限）

### 6.5 蓝绿部署与灰度策略

#### 方案：Nginx Path-Based 路由（推荐）

在 frontend 和 backend 之间增加 Nginx 代理，按 path 切分流量：

```nginx
# nginx.conf
upstream python_backend {
    server python-backend:8080;
}
upstream dotnet_backend {
    server dotnet-backend:8080;
}

server {
    listen 8090;  # 8090 对外，backend 监听 8080（docker-compose 已 expose 8080）

    # 旧系统（迁移期间）
    location /api/legacy/ {
        proxy_pass http://python-backend:8080/api/legacy/;
        proxy_set_header Host $host;
    }

    # 新系统
    location / {
        proxy_pass http://dotnet-backend:8080/;
        proxy_set_header Host $host;
    }
}
```

#### 切换步骤

1. **初始状态（100% Python）**：Nginx 路由默认走 `python-backend`，`dotnet-backend` 已在后台启动但无流量
2. **5% 灰度**：通过 Nginx `split_clients` 将 5% 请求切换到 `dotnet-backend`，观察 24h 错误率
3. **50% 灰度**：确认 5% 阶段无错误后，将比例提升至 50%
4. **全量切换**：确认 50% 阶段无错误后，Nginx 路由 100% 到 `dotnet-backend`，停止 `python-backend`
5. **回滚**：修改 Nginx 配置将流量切回 `python-backend` 即可秒级回滚（重启 `python-backend` 容器）

#### Docker Compose 灰度配置

```yaml
services:
  nginx:
    image: nginx:alpine
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
    ports:
      - "8090:8090"
    depends_on:
      - python-backend
      - dotnet-backend

  python-backend:    # 仅迁移期间存在
    image: ontopilot-python:${VERSION}
    profiles: ["migration"]

  dotnet-backend:
    image: ontopilot-dotnet:${VERSION}
```

```bash
# 迁移开始（两套并行）
docker compose --profile migration up -d

# 迁移完成（删除 Python 后端）
docker compose --profile migration down
docker compose up -d dotnet-backend
```

---

## 7. Frontend 兼容性

Frontend 通过 REST API 与后端通信。迁移后需保证：

1. **认证方式兼容**：`AuthController` 使用与 FastAPI 相同的 HttpOnly Session Cookie
2. **API 路由兼容**：Controller 路由与现有 FastAPI 路径一一对应
3. **JSON 响应结构兼容**：所有 DTO 与 FastAPI 返回的 JSON Schema 一致
4. **MCP 端点兼容**：`/mcp` Streamable HTTP 端点行为不变，使用 `ModelContextProtocol.AspNetCore` 包实现，遵循 MCP Spec（JSON-RPC over HTTP）
5. **对外 API 兼容**：`ExternalApiController` 完整实现 [docs/external-api.zh-CN.md](../../external-api.zh-CN.md) 定义的 Token Scope 体系（`ontology:read`、`vocabulary:read`、`instances:read`、`query:read`、`provenance:read`）

### 7.1 API 版本策略

**当前阶段（.NET 迁移）**：无需版本化。ASP.NET Core Controller 路由与 FastAPI 路由完全对应，JSON Schema 兼容，Frontend 无需修改。

**未来 Breaking Change 策略**：

当需要发布不兼容的 API 变更时，采用路径版本化 + 6 个月并行策略：

```csharp
// 路由示例
[ApiController]
[Route("api/v2/knowledge")]
public class KnowledgeControllerV2 : ControllerBase
{
    // 新的不兼容接口
}
```

| 阶段 | 时间 | 路由 | 说明 |
| --- | --- | --- | --- |
| 新接口上线 | T+0 | `/api/v2/` | .NET 后端同时支持 v1 和 v2 |
| 提示期 | T+0 ~ T+3m | `/api/v1/` | 通过 `Deprecation` 响应头提示升级 |
| 废弃期 | T+3m ~ T+6m | `/api/v1/` | 返回 410 Gone，文档指引到 v2 |
| 下线 | T+6m | - | 删除 v1 路由代码 |

**版本化触发条件（满足任一即为 Breaking Change）：**

- 删除或重命名字段
- 改变字段类型（如 `string` → `number`）
- 改变语义（如 `null` 的含义）
- 删除或重命名 API 端点

**非 Breaking Change（无需版本化）：**

- 新增可选字段
- 新增 API 端点
- 新增枚举值（客户端忽略未知值即可）

---

## 8. MCP 服务器（ModelContextProtocol）

使用官方 C# SDK `ModelContextProtocol.AspNetCore 2.x`，参考 [OpenClaw.Gateway.Mcp](E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Mcp) 实现模式。

### 8.1 注册与启动

```csharp
// Program.cs
services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "OnToPilot MCP",
            Version = "1.0.0"
        };
    })
    .WithHttpTransport(options =>
    {
        options.Stateless = true; // discovery-first
    })
    .WithTasks(new InMemoryMcpTaskStore())
    .WithTools<OnToPilotMcpTools>()
    .WithResources<OnToPilotMcpResources>()
    .WithPrompts<OnToPilotMcpPrompts>();
```

### 8.2 Tool 实现（[McpServerTool]）

参考 [OpenClawMcpTools.cs](E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Mcp\OpenClawMcpTools.cs)，每个 Tool 方法标注 `[McpServerTool]`：

```csharp
[McpServerToolType]
public sealed class OnToPilotMcpTools
{
    private readonly IntegrationApiFacade _facade;

    public OnToPilotMcpTools(IntegrationApiFacade facade) => _facade = facade;

    [McpServerTool(Name = "ontopilot.get_ontology", ReadOnly = true),
     Description("Get the TBox ontology structure for a knowledge system.")]
    public async Task<string> GetOntology(
        [Description("Knowledge system public ID.")] string ksId,
        CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.GetOntologyAsync(ksId, ct),
            OnToPilotJsonContext.Default.OntologyResponse);

    [McpServerTool(Name = "ontopilot.list_classes", ReadOnly = true),
     Description("List TBox classes with optional search filter.")]
    public async Task<string> ListClasses(
        [Description("Knowledge system public ID.")] string ksId,
        [Description("Optional search term.")] string? q = null,
        [Description("Max results.")] int limit = 100,
        CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.ListClassesAsync(ksId, q, limit, ct),
            OnToPilotJsonContext.Default.ClassListResponse);

    [McpServerTool(Name = "ontopilot.search_vocabulary", ReadOnly = true),
     Description("Resolve a controlled term via SKOS vocabulary.")]
    public async Task<string> SearchVocabulary(
        [Description("Knowledge system public ID.")] string ksId,
        [Description("Search term.")] string q,
        [Description("Language filter (e.g. zh-CN).")] string? language = null,
        CancellationToken ct)
        => JsonSerializer.Serialize(
            await _facade.SearchVocabularyAsync(ksId, q, language, ct),
            OnToPilotJsonContext.Default.VocabularySearchResponse);

    // 对应 FastAPI mcp_tokens.py 的 Tool Scope：mcp:read / mcp:write / mcp:manage
}
```

### 8.3 Resource 实现（[McpServerResource]）

```csharp
[McpServerResourceType]
public sealed class OnToPilotMcpResources
{
    [McpServerResource(UriTemplate = "ontopilot://status", Name = "System Status", MimeType = "application/json")]
    public string GetStatus()
        => JsonSerializer.Serialize(_facade.GetStatus(), OnToPilotJsonContext.Default.StatusResponse);

    [McpServerResource(UriTemplate = "ontopilot://knowledge-systems", Name = "Knowledge Systems", MimeType = "application/json")]
    public async Task<string> ListKnowledgeSystems(CancellationToken ct)
        => JsonSerializer.Serialize(await _facade.ListKnowledgeSystemsAsync(ct),
            OnToPilotJsonContext.Default.KsListResponse);
}
```

### 8.4 MCP 路由与认证

参考 [McpServiceExtensions.UseOpenClawMcpAuth()](E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Mcp\McpServiceExtensions.cs#L69-L97)，对 `/mcp` 路径复用现有 Token 认证中间件：

```csharp
// 在 UseOpenClawMcpAuth 中检查 MCP Token Scope（mcp:read / mcp:write / mcp:manage）
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
    {
        if (!TryAuthorizeMcpToken(ctx, out var scope))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
        // 将 scope 注入 DI Container，供 OnToPilotMcpTools 读取
        ctx.Items["McpTokenScope"] = scope;
    }
    await next(ctx);
});
```

#### Token 存储模型

MCP Token 存储在 PostgreSQL `mcp_tokens` 表中，与 FastAPI 版本共用同一张表：

```csharp
public sealed class McpToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; }  // SHA-256 哈希存储
    public McpScope Scope { get; set; }    // mcp:read / mcp:write / mcp:manage
    public string Name { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum McpScope
{
    Read,    // mcp:read
    Write,   // mcp:write
    Manage   // mcp:manage（含 Read）
}
```

#### Token 认证流程

```csharp
private bool TryAuthorizeMcpToken(HttpContext ctx, out McpScope scope)
{
    scope = McpScope.Read;
    var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
        return false;

    var token = authHeader["Bearer ".Length..];
    var tokenHash = ComputeSha256(token);

    var mcpToken = _dbContext.McpTokens
        .FirstOrDefault(t => t.TokenHash == tokenHash);

    if (mcpToken is null) return false;
    if (mcpToken.ExpiresAt is not null && mcpToken.ExpiresAt < DateTime.UtcNow)
        return false;  // 过期 Token 拒绝

    scope = mcpToken.Scope;
    ctx.Items["McpTokenUserId"] = mcpToken.UserId;
    return true;
}
```

#### 刷新机制

- Token 过期时间由用户设置（默认 90 天），无自动刷新机制
- 用户需在 UI 或通过 API 手动轮换 Token
- `mcp:manage` Token 操作触发审计日志记录（`AuditLog` 表）

### 8.5 Scope 映射

| MCP Token Scope | OnToPilotMcpTools 可用操作 |
|---|---|
| `mcp:read` | 读取本体、类、属性、实例、词汇表、证据、历史 |
| `mcp:write` | 预览/应用 TBox、ABox、SKOS 修改，处理审核项，启动抽取 |
| `mcp:manage` | 发布、部署、停止/删除发布版本、回滚审计变更 |

---

## 9. SHACL 验证（dotNetRDF 新增）

OnToPilot 当前使用 Python `tbox_guard.py` 做 TBox 一致性检查，迁移后使用 dotNetRDF 的 SHACL ShapesGraph：

```csharp
public sealed class ShaclValidator
{
    private readonly IGraph _shapesGraph;

    public ShaclValidator(string shapesFilePath)
    {
        _shapesGraph = new Graph();
        FileLoader.Load(_shapesGraph, shapesFilePath);
    }

    public ShaclReport Validate(IGraph dataGraph)
    {
        var shapes = new ShapesGraph(_shapesGraph);
        var report = shapes.Validate(dataGraph);
        return MapToShaclReport(report);
    }
}
```

**TBox Guard 检查项转换为 SHACL Shapes：**

| 检查项 | SHACL Shape 策略 |
|---|---|
| 类必须 rdfs:label | `sh:property` with `sh:minCount 1` on `rdfs:label` |
| 属性类型唯一 | `sh:property` with `sh:or` 互斥 |
| domain/range 一致 | `sh:property` with `sh:class` / `sh:datatype` |
| 无循环 subclass-of | SPARQL 验证（SHACL 无直接支持） |
| disjointWith 对称 | `sh:property` with `sh:sparql` 约束 |

---

## 10. 实现顺序

### Phase 1：基础设施（2-3 周）
1. 创建 ASP.NET Core 10 项目结构
2. 迁移数据库模型（EF Core），确保 SQL 数据迁移脚本可运行
3. 实现 Session 认证（`AuthController`）
4. 搭建 API 控制器骨架（与 FastAPI 路由一一对应）
5. `StoreWrapper` 封装 + Oxigraph.NET 集成

### Phase 2：核心本体逻辑（3-4 周）
6. `SchemaBuilder` — TBox 构建
7. `SkosManager` — SKOS 词汇管理
8. `ABoxManager` — ABox 管理
9. `TBoxGuard` + `ShaclValidator` — 本体验证
10. `ConflictDetector` — 冲突检测
11. `ReleaseManager` — 发布服务

### Phase 3：业务 API（2-3 周）
12. 完成所有 Controller 与现有 Frontend 的联调
13. MCP 端点兼容
14. RDF 导入/导出（`RdfImportService`）

### Phase 4：LLM 集成（1-2 周）

15. `ExtractionService` — Microsoft.Extensions.AI IChatClient，直接调用 OpenAI / Anthropic / Gemini / Ollama 等 Provider
16. `EmbeddingService` — IEmbeddingGenerator 向量嵌入（对应 `embeddings.py`）
17. 抽取任务队列与状态管理

### Phase 5：测试与数据迁移（1-2 周）

1. 单元测试覆盖（`StoreWrapperTests`, `SchemaBuilderTests`, `ShaclValidatorTests`）
2. SQL 数据迁移脚本验证
3. RDF 存储副本的只读兼容性验证、写入冒烟测试与 N-Quads 回退迁移验证
4. 端到端联调（Frontend → ASP.NET Core → Oxigraph.NET）

---

## 11. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| Oxigraph.NET 与 pyoxigraph 行为或存储格式存在差异 | 中 | 高 | 锁定 NuGet 0.5.8；在目录副本上执行只读和写入验证；失败时回退到 N-Quads 逻辑迁移 |
| dotNetRDF SHACL 与 Python TBox Guard 检查项不完全等价 | 中 | 低 | TBox Guard 逻辑已固化，可逐项对照迁移 |
| Frontend API 契约兼容问题 | 低 | 高 | API 路由和 JSON Schema 一一映射，自动化契约测试 |
| EF Core 迁移脚本执行时间过长导致业务中断 | 中 | 高 | 选择低峰期执行，准备停机窗口；详细设计见 [Section 6.2](#62-sql-存量数据迁移) |
| Microsoft.Extensions.AI Provider 覆盖不足 | 低 | 中 | LlmClientFactory 支持所有主流 Provider；OpenAI-Compatible 兜底 |
| DoclingDotNet 解析质量不达预期，导致抽取质量下降 | 中 | 高 | 补充质量评估流程，保留人工抽检；详见 [Section 4.11.1](#4111-文档解析parser) |
| MinIO 预签名 URL 生成方式与前端上传流程不兼容 | 中 | 中 | 前端上传流程专项验证；使用 `AWSSDK.S3` 预签名 API |
| Microsoft.Extensions.AI 各 Provider 的 Function Calling 支持不一致 | 中 | 中 | 在 ExtractionService 中做 Provider 能力探测，不支持者降级为普通 Chat 调用 |
| MCP Streamable HTTP 与现有前端 MCP 客户端的兼容性 | 低 | 高 | 提前用 Postman/MCP Inspector 测试 `/mcp` 端点 |
| Section 6 缺失导致关键迁移步骤未定义 | ~~高~~ | ~~高~~ | ✅ 已补充（见 [Section 6](#6-测试cicd可观测性与部署)） |

---

## 12. 放弃项

- **不迁移 FastAPI 本身**：直接重写为 ASP.NET Core Controller，不做 pyFastAPI 兼容层
- **不保留 Python 抽取微服务**：迁移到 Microsoft.Extensions.AI IChatClient 直接调用 Provider
- **不修改 Frontend**：React 代码不变
- **不引入新 RDF 存储**：继续使用 Oxigraph（RocksDB）；优先验证后复用存储目录，必要时通过 N-Quads 迁移到新的 Oxigraph 目录
