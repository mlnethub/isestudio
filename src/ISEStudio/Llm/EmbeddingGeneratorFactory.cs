using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using ISEStudio.Configuration;

namespace ISEStudio.Llm;

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

    private readonly IOptions<ISEStudioOptions> _options;

    public EmbeddingGeneratorFactory(IOptions<ISEStudioOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Convenience for unit tests that need a factory wired with default
    /// <see cref="ISEStudioOptions"/>. Production code always goes
    /// through DI; this helper exists only to keep test files short.
    /// </summary>
    internal static EmbeddingGeneratorFactory CreateForTest() =>
        new(Options.Create(new ISEStudioOptions()));

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

    private IEmbeddingGenerator<string, Embedding<float>> CreateOpenAiCompatible(
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

        var options = BuildOpenAiClientOptions(endpoint);
        var credential = string.IsNullOrWhiteSpace(config.ApiKey)
            ? new ApiKeyCredential("not-required")
            : new ApiKeyCredential(config.ApiKey);

        var client = new OpenAIClient(credential, options);
        return client.GetEmbeddingClient(config.Model).AsIEmbeddingGenerator();
    }

    /// <summary>
    /// Build the <see cref="OpenAIClientOptions"/> applied to every
    /// embedding call through this factory. Exposed as <c>internal</c>
    /// so the test project can assert the configured
    /// <see cref="OpenAIClientOptions.NetworkTimeout"/> and
    /// <see cref="OpenAIClientOptions.RetryPolicy"/> without going
    /// through a real embedding request.
    /// </summary>
    internal OpenAIClientOptions BuildOpenAiClientOptions(string endpoint)
    {
        var studio = _options.Value;
        var opts = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
        };
        // Mirrors ChatClientFactory.BuildOpenAiClientOptions: a
        // configured 0 falls back to the SDK default.
        if (studio.LlmNetworkTimeoutSeconds > 0)
        {
            opts.NetworkTimeout = TimeSpan.FromSeconds(studio.LlmNetworkTimeoutSeconds);
        }
        opts.RetryPolicy = new ClientRetryPolicy(maxRetries: studio.LlmMaxRetries);
        return opts;
    }
}
