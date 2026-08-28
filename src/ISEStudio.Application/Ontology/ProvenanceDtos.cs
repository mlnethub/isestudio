using System.Text.Json;

namespace ISEStudio.Application.Ontology;

/// <summary>
/// One source document backing at least one extracted axiom in the
/// knowledge system. The <see cref="ChunkCount"/> /
/// <see cref="AxiomCount"/> are precomputed on the server; the
/// frontend uses them to render the "axiom provenance" matrix without
/// re-aggregating per row.
/// </summary>
public sealed record SourceOut(
    Guid DocumentId, string Filename, string? Folder, bool Exists, int ChunkCount, int AxiomCount);

/// <summary>
/// Per-chunk extraction provenance. <see cref="PromptSnapshot"/> +
/// <see cref="Review"/> are stored as <see cref="JsonDocument"/> so the
/// audit trail preserves the raw, unmodified LLM prompt + any
/// reviewer-issued correction at fetch time. Mirrors Python
/// <c>backend/app/ontology/provenance.py::ProvenanceSource</c>.
/// </summary>
public sealed record ProvenanceSourceOut(
    Guid? ChunkId, Guid? DocumentId, Guid? JobId, string? Model,
    JsonDocument? PromptSnapshot, string Method, string? Actor, JsonDocument? Review);

/// <summary>
/// One axiom's full provenance — every chunk + job + reviewer that
/// touched the axiom since the workspace was opened.
/// </summary>
public sealed record ProvenanceGroupOut(string AxiomKey, IReadOnlyList<ProvenanceSourceOut> Sources);