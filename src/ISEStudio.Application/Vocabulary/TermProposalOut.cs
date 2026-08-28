using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISEStudio.Application.Vocabulary;

/// <summary>
/// Wire shape for one <c>TermProposalEntity</c> row. Mirrors the Python
/// <c>backend/app/schemas/terminology.py::_proposal_out</c> schema so the
/// application service can serialise the row straight to JSON without an
/// additional mapper. Field names match the Python reference verbatim.
/// Extracted from <c>VocabularyProposalService.cs</c> as part of the
/// vocabulary application-service slice (2026-08-28).
/// </summary>
public sealed record TermProposalOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("term")] string Term,
    [property: JsonPropertyName("target_iri")] string? TargetIri,
    [property: JsonPropertyName("target_label")] string? TargetLabel,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("evidence")] JsonElement? Evidence,
    [property: JsonPropertyName("source_chunk_ids")] JsonElement? SourceChunkIds,
    [property: JsonPropertyName("extraction_job_id")] Guid? ExtractionJobId,
    [property: JsonPropertyName("proposed_by")] string ProposedBy,
    [property: JsonPropertyName("resolved_by")] string? ResolvedBy,
    [property: JsonPropertyName("resolution_note")] string? ResolutionNote,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("resolved_at")] DateTimeOffset? ResolvedAt);