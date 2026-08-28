using System.Text.Json.Serialization;

namespace ISEStudio.Application.Vocabulary;

// ----------------------------------------------------------------------
// SKOS vocabulary DTOs
// ----------------------------------------------------------------------
//
// All wire types live on the Application side of the layered architecture
// so the dispatcher arm can serialise them directly to JSON without an
// extra mapper. The records were extracted from SkosManager.cs as part
// of the vocabulary application-service slice (2026-08-28) and follow
// the snake_case naming policy configured in Program.cs AddJsonOptions.

/// <summary>Payload for <c>SkosManager.CreateScheme</c> / <c>UpdateScheme</c>.</summary>
public sealed record SkosSchemeData(
    string? Iri = null,
    string Title = "",
    string DefaultLanguage = "zh-CN",
    string Description = "",
    string Origin = "manual");

/// <summary>One localizable label set for a SKOS concept.</summary>
public sealed record SkosLabel(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("language")] string Language = "");

/// <summary>Payload for <c>SkosManager.CreateConcept</c> / <c>UpdateConcept</c>.</summary>
public sealed record SkosConceptData(
    string? Iri = null,
    string SchemeIri = "",
    string PrefLabel = "",
    string Language = "en",
    IReadOnlyList<SkosLabel>? AltLabels = null,
    IReadOnlyList<SkosLabel>? HiddenLabels = null,
    IReadOnlyList<string>? Broader = null,
    IReadOnlyList<string>? Related = null,
    string Description = "",
    string Notation = "",
    string Status = "active",
    string Origin = "manual",
    string? MappedEntityIri = null)
{
    /// <summary>Returns <see cref="AltLabels"/> or an empty array when null.</summary>
    public IReadOnlyList<SkosLabel> EffectiveAltLabels => AltLabels ?? Array.Empty<SkosLabel>();
    /// <summary>Returns <see cref="HiddenLabels"/> or an empty array when null.</summary>
    public IReadOnlyList<SkosLabel> EffectiveHiddenLabels => HiddenLabels ?? Array.Empty<SkosLabel>();
    /// <summary>Returns <see cref="Broader"/> or an empty array when null.</summary>
    public IReadOnlyList<string> EffectiveBroader => Broader ?? Array.Empty<string>();
    /// <summary>Returns <see cref="Related"/> or an empty array when null.</summary>
    public IReadOnlyList<string> EffectiveRelated => Related ?? Array.Empty<string>();
}

/// <summary>One concept as it appears in <see cref="SkosView"/>.</summary>
public sealed record SkosConceptView(
    [property: JsonPropertyName("iri")] string Iri,
    [property: JsonPropertyName("scheme_iri")] string SchemeIri,
    [property: JsonPropertyName("pref_labels")] IReadOnlyList<SkosLabel> PrefLabels,
    [property: JsonPropertyName("alt_labels")] IReadOnlyList<SkosLabel> AltLabels,
    [property: JsonPropertyName("hidden_labels")] IReadOnlyList<SkosLabel> HiddenLabels,
    [property: JsonPropertyName("display_label")] string DisplayLabel,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("notation")] string Notation,
    [property: JsonPropertyName("broader")] IReadOnlyList<string> Broader,
    [property: JsonPropertyName("related")] IReadOnlyList<string> Related,
    [property: JsonPropertyName("broader_labels")] IReadOnlyList<string> BroaderLabels,
    [property: JsonPropertyName("related_labels")] IReadOnlyList<string> RelatedLabels,
    [property: JsonPropertyName("mapped_entity_iri")] string? MappedEntityIri,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("modified_at")] string ModifiedAt);

/// <summary>One scheme as it appears in <see cref="SkosView"/>.</summary>
public sealed record SkosSchemeView(
    [property: JsonPropertyName("iri")] string Iri,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("titles")] IReadOnlyList<SkosLabel> Titles,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("descriptions")] IReadOnlyList<SkosLabel> Descriptions,
    [property: JsonPropertyName("default_language")] string DefaultLanguage,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("modified_at")] string ModifiedAt,
    [property: JsonPropertyName("concept_count")] int ConceptCount);

/// <summary>Curated view of the SKOS graph for one knowledge system.</summary>
public sealed record SkosView(
    [property: JsonPropertyName("schemes")] IReadOnlyList<SkosSchemeView> Schemes,
    [property: JsonPropertyName("concepts")] IReadOnlyList<SkosConceptView> Concepts,
    [property: JsonPropertyName("stats")] SkosStats Stats);

/// <summary>Roll-up counts surfaced alongside a <see cref="SkosView"/>.</summary>
public sealed record SkosStats(
    [property: JsonPropertyName("scheme_count")] int SchemeCount,
    [property: JsonPropertyName("concept_count")] int ConceptCount,
    [property: JsonPropertyName("label_count")] int LabelCount,
    [property: JsonPropertyName("mapped_count")] int MappedCount,
    [property: JsonPropertyName("unmapped_count")] int UnmappedCount);

/// <summary>One match in <c>SkosManager.Resolve</c>.</summary>
public sealed record SkosMatch(
    [property: JsonPropertyName("concept")] SkosConceptView Concept,
    [property: JsonPropertyName("matched_label")] SkosLabel MatchedLabel,
    [property: JsonPropertyName("match_type")] string MatchType,
    [property: JsonPropertyName("score")] double Score);

/// <summary>One page from <c>SkosManager.ListConcepts</c>.</summary>
public sealed record SkosConceptPage(
    [property: JsonPropertyName("items")] IReadOnlyList<SkosConceptView> Items,
    [property: JsonPropertyName("total")] int Total);