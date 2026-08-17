using OnToPilot.Llm;

namespace OnToPilot.Extraction;

/// <summary>
/// Everything <see cref="ExtractionOrchestrator"/> needs to run one extraction
/// job: which knowledge system to write into, which uploaded blob to read,
/// and which LLM endpoint to route through.
/// </summary>
/// <param name="KnowledgeSystemId">FK of the target knowledge system.</param>
/// <param name="BlobSha">
/// Lowercase-hex SHA-256 of the uploaded document — the object key
/// <see cref="Storage.IBlobStore"/> stores it under.
/// </param>
/// <param name="FileName">
/// Original file name. Only the extension is load-bearing: it selects the
/// parser in <see cref="Parsing.IDocumentParser"/>.
/// </param>
/// <param name="Provider">Provider name routed by <see cref="IChatClientFactory"/>.</param>
/// <param name="Model">Model identifier recorded on the job row.</param>
/// <param name="Endpoint">
/// Provider base URL. Doubles as the capacity-bucket key so two knowledge
/// systems pointed at the same endpoint share one concurrency budget.
/// </param>
/// <param name="ApiKey">Provider credential; null for endpoints that need none.</param>
/// <param name="ConcurrencyLimit">
/// Per-endpoint concurrency budget handed to
/// <see cref="EndpointCapacityCoordinator"/>.
/// </param>
public sealed record ExtractionRequest(
    Guid KnowledgeSystemId,
    string BlobSha,
    string FileName,
    string Provider,
    string Model,
    string Endpoint,
    string? ApiKey,
    int ConcurrencyLimit = 4)
{
    /// <summary>
    /// Capacity bucket this request draws chat permits from. Keyed by the
    /// provider <see cref="Endpoint"/> (not the knowledge-system graph IRI)
    /// so two requests pointed at the same endpoint share one permit budget
    /// regardless of which knowledge system they write into.
    /// </summary>
    public EndpointCapacityKey CapacityKey => new("chat", Endpoint);

    /// <summary>Provider config for <see cref="IChatClientFactory.Create"/>.</summary>
    public LlmProviderConfig ToProviderConfig() => new()
    {
        Provider = Provider,
        Model = Model,
        Endpoint = Endpoint,
        ApiKey = ApiKey,
        ConcurrencyLimit = LlmProviderConfig.ValidateConcurrencyLimit(ConcurrencyLimit),
    };
}
