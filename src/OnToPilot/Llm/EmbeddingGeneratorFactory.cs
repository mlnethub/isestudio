using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace OnToPilot.Llm;

/// <summary>
/// Builds provider-agnostic embedding generators from
/// <see cref="LlmProviderConfig"/>. Returns the
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> abstraction
/// (concrete provider types stay internal).
///
/// <para>Embedding models are routed through the same OpenAI-compatible
/// endpoint convention as chat — <c>openai</c>, <c>deepseek</c>,
/// <c>openai-compatible</c>, <c>ollama</c>, and <c>azure-openai</c> all
/// work via the official OpenAI SDK. Anthropic does not expose an
/// embedding endpoint and falls through to <see cref="InvalidOperationException"/>;
/// <c>gemini</c> is similarly routed to the OpenAI SDK because Google's
/// embedding endpoint is not yet on the supported provider list and
/// needs its own implementation.</para>
/// </summary>
public sealed class EmbeddingGeneratorFactory
{
    private const string OpenAiDefaultEndpoint = "https://api.openai.com/v1";
    private const string OllamaDefaultEndpoint = "http://localhost:11434/v1";

    /// <summary>
    /// Build an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for
    /// the provider named in <paramref name="config"/>. Throws
    /// <see cref="InvalidOperationException"/> for unsupported providers.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>> Create(LlmProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var provider = config.Provider.ToLowerInvariant();
        return provider switch
        {
            "openai" or "deepseek" or "openai-compatible" or "ollama" or "azure-openai"
                => CreateOpenAiCompatible(config, provider),
            _ => throw new InvalidOperationException(
                $"Unsupported embedding provider: {config.Provider}"),
        };
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAiCompatible(
        LlmProviderConfig config,
        string provider)
    {
        var endpoint = provider switch
        {
            "ollama" => string.IsNullOrWhiteSpace(config.Endpoint)
                ? OllamaDefaultEndpoint
                : config.Endpoint,
            _ => string.IsNullOrWhiteSpace(config.Endpoint)
                ? OpenAiDefaultEndpoint
                : config.Endpoint,
        };

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
        };
        var credential = string.IsNullOrWhiteSpace(config.ApiKey)
            ? new ApiKeyCredential("not-required")
            : new ApiKeyCredential(config.ApiKey);

        var client = new OpenAIClient(credential, options);
        return client.GetEmbeddingClient(config.Model).AsIEmbeddingGenerator();
    }
}
