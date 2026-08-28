namespace ISEStudio.Prompts;

/// <summary>
/// Static catalog entry describing a registered prompt template.
/// Source-of-truth for <c>category</c> / <c>title</c> / <c>description</c> /
/// <c>default_content</c> / <c>variables</c>. Per-KS overrides live on
/// <see cref="Infrastructure.Persistence.Entities.KnowledgePromptOverrideEntity"/>.
///
/// Stays in <see cref="ISEStudio.Prompts"/> (not the
/// <see cref="ISEStudio.Application"/> project) because it's an internal
/// catalog row, not a wire-shape DTO; the three wire DTOs
/// (<c>PromptOut</c> / <c>PromptListOut</c> / <c>PromptUpdateIn</c>) live
/// in <see cref="ISEStudio.Application.Prompts"/> since the 10/13
/// dispatcher split.
/// </summary>
public sealed record PromptDef(
    string Key,
    string Category,
    string Title,
    string Description,
    string DefaultContent,
    IReadOnlyList<string> Variables);