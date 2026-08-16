using System.Text.Json;

namespace OnToPilot.Infrastructure.Persistence.Entities;

// ---------------------------------------------------------------------------
// Knowledge systems, documents, chunks, providers, system config
// ---------------------------------------------------------------------------

/// <summary>
/// A named ontology graph. Maps to one named graph (IRI) in the Oxigraph
/// store.
/// </summary>
public sealed class KnowledgeSystemEntity : LegacyAddressableEntity
{
    /// <summary>Stable, opaque, public-facing identifier (UUID4 hex).</summary>
    public string PublicId { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-form description shown in the UI.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>FK to the owning user. Owners have full control implicitly.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Named graph IRI in Oxigraph (e.g. <c>http://ontopilot.local/ks/3</c>).</summary>
    public string GraphIri { get; set; } = string.Empty;

    /// <summary>Entity namespace IRI (e.g. <c>http://ontopilot.local/ks/3/onto#</c>).</summary>
    public string BaseIri { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC last-update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Cached count of OWL classes in the graph.</summary>
    public int ClassCount { get; set; }

    /// <summary>Cached count of OWL properties in the graph.</summary>
    public int PropertyCount { get; set; }

    /// <summary>Cached count of axioms in the graph.</summary>
    public int AxiomCount { get; set; }

    /// <summary>Per-KS LLM model override; null = system default.</summary>
    public string? LlmModel { get; set; }

    /// <summary>FK to the <see cref="ProviderEntity"/> used for LLM calls in this KS.</summary>
    public Guid? LlmProviderId { get; set; }

    /// <summary>FK to the <see cref="ProviderEntity"/> used for embeddings in this KS.</summary>
    public Guid? EmbeddingProviderId { get; set; }

    /// <summary>Per-KS embedding model override; null = system default.</summary>
    public string? EmbeddingModel { get; set; }
}

/// <summary>
/// An uploaded source file, bound to exactly one knowledge system. The raw
/// bytes are content-addressed in the blob store and shared across KS, but
/// each KS gets its own row: the same file uploaded into two knowledge
/// systems is two documents. Dedup is therefore scoped per-KS
/// (<c>(KnowledgeSystemId, Sha256)</c> unique), not globally.
/// </summary>
public sealed class DocumentEntity : LegacyAddressableEntity
{
    /// <summary>FK to the owning knowledge system; null = orphan (will be backfilled on startup).</summary>
    public Guid? KnowledgeSystemId { get; set; }

    /// <summary>Content-addressed SHA-256 hex of the raw bytes.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Original filename as uploaded.</summary>
    public string OriginalFilename { get; set; } = string.Empty;

    /// <summary>Virtual folder path, KS-internal (e.g. <c>/manuals/pumps</c>).</summary>
    public string Folder { get; set; } = "/";

    /// <summary>Normalized lowercase extension, no dot.</summary>
    public string Ext { get; set; } = string.Empty;

    /// <summary>MIME type as detected at upload.</summary>
    public string? Mime { get; set; }

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Relative path inside the blob store (e.g. <c>aa/bb/&lt;hash&gt;</c>).</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>UTC upload timestamp.</summary>
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>Parse lifecycle: <c>pending</c> | <c>parsed</c> | <c>failed</c>.</summary>
    public string ParseStatus { get; set; } = "pending";

    /// <summary>Parser backend used (e.g. <c>docling</c>, <c>fallback:pdf</c>).</summary>
    public string? ParserBackend { get; set; }

    /// <summary>Parse error message if <see cref="ParseStatus"/> == <c>failed</c>.</summary>
    public string? ParseError { get; set; }

    /// <summary>Length of the parsed plain-text in characters.</summary>
    public int? TextCharCount { get; set; }

    /// <summary>Number of chunks produced after a successful parse.</summary>
    public int ChunkCount { get; set; }

    /// <summary>UTC timestamp of the most recent TBox extraction covering this document.</summary>
    public DateTimeOffset? TboxExtractedAt { get; set; }

    /// <summary>UTC timestamp of the most recent ABox extraction covering this document.</summary>
    public DateTimeOffset? AboxExtractedAt { get; set; }
}

/// <summary>
/// A contiguous text slice of a parsed document.
/// </summary>
public sealed class ChunkEntity : LegacyAddressableEntity
{
    /// <summary>FK to the parent document.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>0-based order within the document.</summary>
    public int Idx { get; set; }

    /// <summary>Chunk text content.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Starting character offset within the parsed document text.</summary>
    public int CharStart { get; set; }

    /// <summary>Ending character offset (exclusive) within the parsed document text.</summary>
    public int CharEnd { get; set; }

    /// <summary>Approximate token count (used for budgeting).</summary>
    public int TokenEstimate { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One model endpoint entry: an OpenAI-compatible connection (<c>base_url</c> +
/// <c>api_key</c>) bundled with a specific model and its kind
/// (<c>llm</c> | <c>embedding</c>). The <see cref="ApiKey"/> is stored
/// server-side and never returned raw by the API.
/// </summary>
public sealed class ProviderEntity : LegacyAddressableEntity
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the OpenAI-compatible endpoint.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Bearer credential for the endpoint. Server-only.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model identifier passed in requests.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Either <c>llm</c> or <c>embedding</c>.</summary>
    public string Kind { get; set; } = "llm";

    /// <summary>Maximum in-flight requests against this provider.</summary>
    public int ConcurrencyLimit { get; set; } = 10;

    /// <summary>Result of the most recent connection test (persisted).</summary>
    public bool? LastTestOk { get; set; }

    /// <summary>UTC timestamp of the most recent connection test.</summary>
    public DateTimeOffset? LastTestedAt { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Singleton (<c>LegacyId == 1</c>) runtime config an admin can change WITHOUT
/// a restart, overlaying the .env defaults. Holds the system default
/// provider/model for chat + embeddings; a KS may override either.
/// </summary>
public sealed class SystemConfigEntity : LegacyAddressableEntity
{
    /// <summary>Default LLM model; null falls back to appsettings.</summary>
    public string? ExtractModel { get; set; }

    /// <summary>Default embedding model; null falls back to appsettings.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>FK to the default LLM <see cref="ProviderEntity"/>.</summary>
    public Guid? LlmProviderId { get; set; }

    /// <summary>FK to the default embedding <see cref="ProviderEntity"/>.</summary>
    public Guid? EmbeddingProviderId { get; set; }

    /// <summary>Legacy global limit retained only to migrate pre-provider-limit installations.</summary>
    public int? ExtractionConcurrency { get; set; }

    /// <summary>Legacy (pre-Provider) single-connection base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Legacy (pre-Provider) single-connection API key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>UTC last-update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Convenience: the singleton row is always <c>LegacyId == 1</c>.</summary>
    public const long SingletonLegacyId = 1;
}