using System.Text.Json.Serialization;

namespace ISEStudio.Application.Prompts;

/// <summary>
/// Wire shape — matches <c>PromptOut</c> in
/// <c>migration/baseline/openapi-python.json</c>.
/// </summary>
public sealed record PromptOut(
    string Key,
    string Category,
    string Title,
    string Description,
    [property: JsonPropertyName("default_content")] string DefaultContent,
    [property: JsonPropertyName("effective_content")] string EffectiveContent,
    [property: JsonPropertyName("variables")] IReadOnlyList<string> Variables,
    [property: JsonPropertyName("is_overridden")] bool IsOverridden,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("updated_by")] string? UpdatedBy);

public sealed record PromptListOut(
    [property: JsonPropertyName("items")] IReadOnlyList<PromptOut> Items,
    [property: JsonPropertyName("total_overrides")] int TotalOverrides);

public sealed record PromptUpdateIn(
    [property: JsonPropertyName("content")] string Content);