# OnToPilot 后端迁移到 .NET 10 技术规格

**状态**: 设计中
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
│   ├── Ontology/                   # RDF 本体逻辑
│   │   ├── StoreWrapper.cs         # Oxigraph.NET 封装（替代 store.py）
│   │   ├── SchemaBuilder.cs        # 替代 schema.py
│   │   ├── SkosManager.cs          # 替代 skos.py
│   │   ├── ABoxManager.cs          # 替代 abox.py
│   │   ├── ConflictDetector.cs     # 替代 conflicts.py
│   │   ├── ReleaseManager.cs       # 替代 release_service.py
│   │   ├── TBoxGuard.cs            # TBox 守卫逻辑
│   │   └── ShaclValidator.cs       # dotNetRDF SHACL 验证
│   ├── Llm/                        # LLM 调用层
│   │   ├── ExtractionService.cs    # Microsoft.Extensions.AI IChatClient 抽取
│   │   └── EmbeddingService.cs     # IEmbeddingGenerator 向量嵌入
│   ├── Mcp/                        # MCP 服务器（ModelContextProtocol）
│   │   ├── OnToPilotMcpTools.cs   # MCP Tool 实现（[McpServerTool]）
│   │   ├── OnToPilotMcpResources.cs # MCP Resource 实现（[McpServerResource]）
│   │   └── OnToPilotMcpPrompts.cs # MCP Prompt 实现（[McpServerPrompt]）
│   ├── Middleware/
│   │   └── SessionAuthMiddleware.cs
│   └── Program.cs
├── OnToPilot.Domain/               # 共享领域模型
│   └── ...
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
var result = await converter.ConvertAsync(filePath);
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
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            memory.Position = 0;
            var converter = new DocumentConverter();
            var result = await converter.ConvertAsync(memory, extension);
            var markdown = result.Document.ExportToMarkdown();
            return new ParseResult(markdown, "doclingdotnet", result.Document);
        }
        catch { return null; }
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
- **迁移后**: EF Core 8 + Npgsql（PostgreSQL）

数据模型一对一映射，迁移脚本处理存量数据。

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

---

## 6. Frontend 兼容性

Frontend 通过 REST API 与后端通信。迁移后需保证：

1. **认证方式兼容**：`AuthController` 使用与 FastAPI 相同的 HttpOnly Session Cookie
2. **API 路由兼容**：Controller 路由与现有 FastAPI 路径一一对应
3. **JSON 响应结构兼容**：所有 DTO 与 FastAPI 返回的 JSON Schema 一致
4. **MCP 端点兼容**：`/mcp` Streamable HTTP 端点行为不变，使用 `ModelContextProtocol.AspNetCore` 包实现，遵循 MCP Spec（JSON-RPC over HTTP）
5. **对外 API 兼容**：`ExternalApiController` 完整实现 [docs/external-api.zh-CN.md](../../external-api.zh-CN.md) 定义的 Token Scope 体系（`ontology:read`、`vocabulary:read`、`instances:read`、`query:read`、`provenance:read`）

---

## 7. MCP 服务器（ModelContextProtocol）

使用官方 C# SDK `ModelContextProtocol.AspNetCore 2.x`，参考 [OpenClaw.Gateway.Mcp](E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Mcp) 实现模式。

### 7.1 注册与启动

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

### 7.2 Tool 实现（[McpServerTool]）

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

### 7.3 Resource 实现（[McpServerResource]）

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

### 7.4 MCP 路由与认证

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

### 7.5 Scope 映射

| MCP Token Scope | OnToPilotMcpTools 可用操作 |
|---|---|
| `mcp:read` | 读取本体、类、属性、实例、词汇表、证据、历史 |
| `mcp:write` | 预览/应用 TBox、ABox、SKOS 修改，处理审核项，启动抽取 |
| `mcp:manage` | 发布、部署、停止/删除发布版本、回滚审计变更 |

---

## 8. SHACL 验证（dotNetRDF 新增）

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

## 9. 实现顺序

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

## 10. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| Oxigraph.NET 与 pyoxigraph 行为或存储格式存在差异 | 中 | 高 | 锁定 NuGet 0.5.8；在目录副本上执行只读和写入验证；失败时回退到 N-Quads 逻辑迁移 |
| dotNetRDF SHACL 与 Python TBox Guard 检查项不完全等价 | 中 | 低 | TBox Guard 逻辑已固化，可逐项对照迁移 |
| Frontend API 契约兼容问题 | 低 | 高 | API 路由和 JSON Schema 一一映射，自动化契约测试 |
| EF Core 迁移脚本复杂 | 中 | 中 | Phase 1 先完成 SQL 迁移验证 |
| Microsoft.Extensions.AI Provider 覆盖不足 | 低 | 中 | LlmClientFactory 支持所有主流 Provider；OpenAI-Compatible 兜底 |

---

## 11. 放弃项

- **不迁移 FastAPI 本身**：直接重写为 ASP.NET Core Controller，不做 pyFastAPI 兼容层
- **不保留 Python 抽取微服务**：迁移到 Microsoft.Extensions.AI IChatClient 直接调用 Provider
- **不修改 Frontend**：React 代码不变
- **不引入新 RDF 存储**：继续使用 Oxigraph（RocksDB）；优先验证后复用存储目录，必要时通过 N-Quads 迁移到新的 Oxigraph 目录
