namespace OnToPilot.Documents;

/// <summary>
/// Wire DTOs for the <c>/api/knowledge/{ks_id}/documents*</c> surface.
/// Aligned with <c>backend/app/api/documents.py</c> (510 LOC). All field
/// names flow through the global SnakeCaseLower naming policy, so the C#
/// PascalCase record properties serialise to <c>snake_case</c> keys
/// automatically.
/// </summary>
public sealed record DocumentOut(
    long Id,
    Guid KnowledgeSystemId,
    string Sha256,
    string OriginalFilename,
    string Folder,
    string Ext,
    string? Mime,
    long SizeBytes,
    string StoragePath,
    DateTimeOffset UploadedAt,
    string ParseStatus,
    string? ParserBackend,
    string? ParseError,
    int? TextCharCount,
    int ChunkCount,
    DateTimeOffset? TboxExtractedAt,
    DateTimeOffset? AboxExtractedAt);

/// <summary>
/// Paginated document list envelope. Mirrors Python
/// <c>backend/app/api/documents.py::DocumentListResponse</c>.
/// </summary>
public sealed record DocumentListResponse(
    IReadOnlyList<DocumentOut> Items,
    long Total,
    IReadOnlyList<string> Folders);

/// <summary>
/// Per-document parse outcome. Mirrors Python
/// <c>backend/app/api/documents.py::ParseResponse</c>.
/// </summary>
public sealed record ParseResponse(
    long DocumentId,
    string ParseStatus,
    string? ParserBackend,
    int? TextCharCount,
    int ChunkCount,
    string? Error = null);

/// <summary>
/// Batch-parse request body. Either <c> document_ids</c> or
/// <c> folders</c> must be supplied.
/// </summary>
public sealed record ParseBatchIn(
    IReadOnlyList<long> DocumentIds,
    IReadOnlyList<string> Folders,
    bool Recursive = true);

/// <summary>
/// Batch-parse aggregate response. <c> parsed</c> + <c> failed</c>
/// always sum to <c> total</c>.
/// </summary>
public sealed record ParseBatchResponse(
    IReadOnlyList<ParseResponse> Items,
    int Total,
    int Parsed,
    int Failed);

/// <summary>
/// PATCH body for move / rename. Each field nullable; only set keys
/// are applied.
/// </summary>
public sealed record MoveRequest(
    string? Folder,
    string? OriginalFilename);

/// <summary>
/// Counts of distinct axioms / individuals that trace back to a
/// document's chunks. Matches Python <c>document_contribution</c>.
/// </summary>
public sealed record ContributionOut(
    long DocumentId,
    int ChunkCount,
    int AxiomCount,
    int IndividualCount);

/// <summary>
/// A single axiom that would be retracted if the document were
/// deleted (placeholder shape — Block 6 wires the real data).
/// </summary>
public sealed record ImpactAxiom(
    string AxiomKey,
    string Description);

/// <summary>
/// Per-KS impact grouping (placeholder shape — Block 6 populates).
/// </summary>
public sealed record ImpactSystem(
    long KnowledgeSystemId,
    string KnowledgeSystemName,
    IReadOnlyList<ImpactAxiom> Axioms);

/// <summary>
/// Document-impact envelope. Block 4 returns <c> systems</c> as an empty
/// list; Block 6 wires the real <c> _document_impact</c> computation.
/// </summary>
public sealed record ImpactOut(
    long DocumentId,
    IReadOnlyList<ImpactSystem> Systems);

/// <summary>
/// Chunk projection for the <c> /chunks</c> endpoint. Mirrors
/// <c> Chunk</c> in Python.
/// </summary>
public sealed record ChunkOut(
    long Id,
    long DocumentId,
    int Idx,
    string Text,
    int CharStart,
    int CharEnd,
    int TokenEstimate,
    DateTimeOffset CreatedAt);