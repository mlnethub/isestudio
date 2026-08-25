using System.Text.Json;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Extraction;

/// <summary>
/// Wire shape for one <see cref="ExtractionJobEntity"/> row. Mirrors the
/// Python backend's <c>ExtractionJob</c> SQLModel so existing client
/// tooling keeps working during the .NET migration. Field names are
/// pinned to snake_case via <see cref="JsonPropertyNameAttribute"/> so
/// the wire shape stays stable regardless of the controller-layer
/// serializer's naming convention.
/// </summary>
public sealed class ExtractionJobOut
{
    /// <summary>Job primary key. Same value the 409 envelope carries in <c>job_id</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>FK to the owning knowledge system.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("knowledge_system_id")]
    public Guid KnowledgeSystemId { get; set; }

    /// <summary>Job flavour: <c>tbox</c> | <c>abox</c> | <c>both</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; set; } = "tbox";

    /// <summary>Lifecycle status: <c>pending</c> | <c>running</c> | <c>completed</c> | <c>failed</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    /// <summary>Model identifier used for this run.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Immutable effective prompt contents used by this run. Serialised
    /// as a JSON object so callers can inspect the exact instructions
    /// without round-tripping through the binary column type.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("prompt_snapshot")]
    public Dictionary<string, object?>? PromptSnapshot { get; set; }

    /// <summary>Chunk IDs included in this run (ordered).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("chunk_ids")]
    public IReadOnlyList<int> ChunkIds { get; set; } = Array.Empty<int>();

    [System.Text.Json.Serialization.JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("log")]
    public string Log { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_chunks")]
    public int TotalChunks { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("processed_chunks")]
    public int ProcessedChunks { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("classes_added")]
    public int ClassesAdded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("properties_added")]
    public int PropertiesAdded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("axioms_added")]
    public int AxiomsAdded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("individuals_added")]
    public int IndividualsAdded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("assertions_added")]
    public int AssertionsAdded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("pending_added")]
    public int PendingAdded { get; set; }

    /// <summary>
    /// <c>{label: times_seen}</c> histogram for classes referenced by
    /// the ABox extractor that aren't in the TBox yet. Empty when the
    /// job has no ABox phase or has not recorded any unknowns.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("unknown_classes")]
    public Dictionary<string, int> UnknownClasses { get; set; }
        = new(StringComparer.Ordinal);

    /// <summary>Live stage: <c>tbox</c> | <c>abox</c> | <c>terminology</c> | <c>finalizing</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("terms_added")]
    public int TermsAdded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("terms_mapped")]
    public int TermsMapped { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("terminology_proposals")]
    public int TerminologyProposals { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("terminology_error")]
    public string? TerminologyError { get; set; }

    /// <summary>
    /// Project the entity into the wire shape. JsonDocument columns are
    /// re-serialised as plain dictionaries so the response body is
    /// self-describing without dragging the binary column type through
    /// System.Text.Json.
    /// </summary>
    public static ExtractionJobOut From(ExtractionJobEntity e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new ExtractionJobOut
        {
            Id = e.Id,
            KnowledgeSystemId = e.KnowledgeSystemId,
            Kind = e.Kind,
            Status = e.Status,
            Model = e.Model,
            PromptSnapshot = JsonDocumentToDict(e.PromptSnapshot),
            ChunkIds = e.ChunkIds?.ToArray() ?? Array.Empty<int>(),
            CreatedAt = e.CreatedAt,
            FinishedAt = e.FinishedAt,
            Log = e.Log,
            Error = e.Error,
            TotalChunks = e.TotalChunks,
            ProcessedChunks = e.ProcessedChunks,
            ClassesAdded = e.ClassesAdded,
            PropertiesAdded = e.PropertiesAdded,
            AxiomsAdded = e.AxiomsAdded,
            IndividualsAdded = e.IndividualsAdded,
            AssertionsAdded = e.AssertionsAdded,
            PendingAdded = e.PendingAdded,
            UnknownClasses = JsonDocumentToIntDict(e.UnknownClasses),
            Phase = e.Phase,
            TermsAdded = e.TermsAdded,
            TermsMapped = e.TermsMapped,
            TerminologyProposals = e.TerminologyProposals,
            TerminologyError = e.TerminologyError,
        };
    }

    private static Dictionary<string, object?>? JsonDocumentToDict(JsonDocument? doc)
    {
        if (doc is null) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }
        return dict;
    }

    private static Dictionary<string, int> JsonDocumentToIntDict(JsonDocument? doc)
    {
        if (doc is null) return new Dictionary<string, int>(StringComparer.Ordinal);
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.TryGetInt32(out var n))
            {
                dict[prop.Name] = n;
            }
        }
        return dict;
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        System.Text.Json.JsonValueKind.Number => element.GetDouble(),
        _ => element.GetRawText(),
    };
}