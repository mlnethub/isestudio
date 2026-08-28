using System.Text.Json;

namespace ISEStudio.Application.Releases;

/// <summary>
/// Wire DTO matching the Python <c>_release_out()</c> shape
/// (<c>backend/app/api/releases.py</c>:68) so the frontend
/// <c>OntologyRelease</c> TypeScript interface lines up. Snake-case via
/// the global <c>JsonNamingPolicy.SnakeCaseLower</c> configured in
/// <c>Program.cs</c>. Phase 3: <c>KnowledgeSystemId</c> is the internal
/// <see cref="Guid"/>; legacy <c>long</c> deprecated.
/// </summary>
public sealed record ReleaseOut(
    Guid Id,
    Guid KnowledgeSystemId,
    string Version,
    string Status,
    string Title,
    string Notes,
    JsonElement Manifest,
    string CreatedBy,
    string? ReviewedBy,
    string? PublishedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? PublishedAt,
    object? Deployment,
    string? ServiceUrl);