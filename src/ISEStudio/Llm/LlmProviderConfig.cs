namespace ISEStudio.Llm;

/// <summary>
/// Provider-agnostic configuration for an LLM (chat or embedding) endpoint.
/// Mirrors the fields a single config block needs to construct an
/// <see cref="Microsoft.Extensions.AI.IChatClient"/> or
/// <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// </summary>
public sealed record LlmProviderConfig
{
    /// <summary>
    /// Lower-bound (inclusive) for <see cref="ConcurrencyLimit"/>.
    /// </summary>
    public const int MinConcurrencyLimit = 1;

    /// <summary>
    /// Upper-bound (inclusive) for <see cref="ConcurrencyLimit"/>.
    /// </summary>
    public const int MaxConcurrencyLimit = 64;

    /// <summary>
    /// Default value used when the caller does not supply a
    /// <see cref="ConcurrencyLimit"/>.
    /// </summary>
    public const int DefaultConcurrencyLimit = 8;

    /// <summary>
    /// One of <c>openai</c>, <c>deepseek</c>, <c>openai-compatible</c>,
    /// <c>ollama</c>, <c>anthropic</c>, <c>gemini</c>, <c>azure-openai</c>.
    /// Matched case-insensitively by the factory.
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// API key for the provider. Nullable for embedding-only providers
    /// that don't require auth (e.g. local Ollama).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Base URL for the provider. If blank, the factory applies the
    /// provider's default endpoint.
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Model identifier (e.g. <c>gpt-4o-mini</c>, <c>claude-3-5-sonnet-latest</c>,
    /// <c>text-embedding-3-small</c>).
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// Per-key concurrency budget enforced by
    /// <see cref="EndpointCapacityCoordinator"/>. Constrained to 1-64 at
    /// construction time; null falls back to
    /// <see cref="DefaultConcurrencyLimit"/>.
    /// </summary>
    public int? ConcurrencyLimit { get; init; }

    /// <summary>
    /// Effective concurrency limit (always between
    /// <see cref="MinConcurrencyLimit"/> and <see cref="MaxConcurrencyLimit"/>).
    /// </summary>
    public int EffectiveConcurrencyLimit =>
        ValidateConcurrencyLimit(ConcurrencyLimit ?? DefaultConcurrencyLimit);

    /// <summary>
    /// Clamp and validate <paramref name="value"/> into the allowed 1-64 range.
    /// Throws <see cref="ArgumentOutOfRangeException"/> if the caller supplied
    /// a value outside that range; the clamp is performed eagerly so config
    /// validation surfaces bad input before the coordinator ever sees it.
    /// </summary>
    public static int ValidateConcurrencyLimit(int value)
    {
        if (value < MinConcurrencyLimit || value > MaxConcurrencyLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Concurrency limit must be between {MinConcurrencyLimit} and {MaxConcurrencyLimit}.");
        }
        return value;
    }
}
