using System.Text.Json.Serialization;

namespace ISEStudio.EntityResolution;

/// <summary>
/// Candidate individual suggested for a surface form (mirrors Python
/// <c>EntityResolution.context["candidate"]</c> list element).
/// </summary>
public sealed record ResolutionCandidateOut(
    [property: JsonPropertyName("iri")] string Iri,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("score")] double Score);

/// <summary>
/// Wire shape for <c>GET /api/knowledge/{id}/resolution/queue</c> item.
/// Mirrors <c>ResolutionQueueItem</c> in <c>frontend/src/lib/types.ts:682-697</c>.
/// </summary>
public sealed record ResolutionQueueItemOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("surface_form")] string SurfaceForm,
    [property: JsonPropertyName("class_iri")] string? ClassIri,
    [property: JsonPropertyName("class_label")] string? ClassLabel,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ResolutionCandidateOut> Candidates,
    [property: JsonPropertyName("source_chunk_id")] string? SourceChunkId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>
/// Wire shape for <c>GET /api/knowledge/{id}/resolution/decisions</c> item.
/// Mirrors <c>ResolutionDecision</c> in <c>frontend/src/lib/types.ts:699-714</c>.
/// </summary>
public sealed record ResolutionDecisionOut(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("surface_form")] string SurfaceForm,
    [property: JsonPropertyName("class_label")] string? ClassLabel,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("individual_iri")] string? IndividualIri,
    [property: JsonPropertyName("individual_label")] string? IndividualLabel,
    [property: JsonPropertyName("individual_deleted")] bool IndividualDeleted,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("resolved_by")] string? ResolvedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("resolved_at")] DateTimeOffset? ResolvedAt);

public sealed record ResolutionQueueEnvelope(
    [property: JsonPropertyName("items")] IReadOnlyList<ResolutionQueueItemOut> Items,
    [property: JsonPropertyName("total")] int Total);

public sealed record ResolutionDecisionsEnvelope(
    [property: JsonPropertyName("items")] IReadOnlyList<ResolutionDecisionOut> Items,
    [property: JsonPropertyName("total")] int Total);

/// <summary>
/// Body for <c>POST /api/knowledge/{id}/resolution/{res_id}/resolve</c>.
/// Mirrors OpenAPI <c>ResolveRequest</c>: <c>{ "action": "match"|"new", "individual_iri": string|null }</c>.
/// </summary>
public sealed record ResolutionResolveIn(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("individual_iri")] string? IndividualIri);

/// <summary>
/// Body for <c>PATCH /api/knowledge/{id}/resolution/decisions/{res_id}</c>.
/// Mirrors OpenAPI <c>ReasonUpdate</c>: <c>{ "reason": string }</c>.
/// </summary>
public sealed record ResolutionEditReasonIn(
    [property: JsonPropertyName("reason")] string? Reason);