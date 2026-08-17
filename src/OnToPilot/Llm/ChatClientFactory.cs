using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace OnToPilot.Llm;

/// <summary>
/// Routes a <see cref="LlmProviderConfig"/> to a concrete chat client
/// implementation. Every supported route returns the
/// <see cref="IChatClient"/> abstraction — concrete provider types do
/// not leak across this boundary.
///
/// <para>The match on <see cref="LlmProviderConfig.Provider"/> is
/// case-insensitive.</para>
///
/// <para>Provider status:</para>
/// <list type="bullet">
///   <item><description><c>openai</c>, <c>deepseek</c>, <c>openai-compatible</c>,
///     <c>ollama</c>, <c>azure-openai</c> — built on
///     <c>Microsoft.Extensions.AI.OpenAI</c> + the official OpenAI SDK
///     (all expose OpenAI-compatible HTTP endpoints).</description></item>
///   <item><description><c>anthropic</c> — no <c>Microsoft.Extensions.AI.Anthropic</c>
///     package exists on NuGet at 10.7.0; routed through
///     <see cref="HttpJsonChatClient"/>, a thin <see cref="IChatClient"/>
///     over <see cref="HttpClient"/> targeting the Anthropic Messages API.</description></item>
///   <item><description><c>gemini</c> — no <c>Microsoft.Extensions.AI.Google</c>
///     package exists on NuGet at 10.7.0; routed through
///     <see cref="HttpJsonChatClient"/> targeting the Google Gemini
///     generateContent API.</description></item>
/// </list>
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private const string OpenAiProvider = "openai";
    private const string DeepSeekProvider = "deepseek";
    private const string OpenAiCompatibleProvider = "openai-compatible";
    private const string AnthropicProvider = "anthropic";
    private const string GeminiProvider = "gemini";
    private const string OllamaProvider = "ollama";
    private const string AzureOpenAiProvider = "azure-openai";

    private const string OpenAiDefaultEndpoint = "https://api.openai.com/v1";
    private const string DeepSeekDefaultEndpoint = "https://api.deepseek.com";
    private const string OllamaDefaultEndpoint = "http://localhost:11434/v1";
    private const string AnthropicDefaultEndpoint = "https://api.anthropic.com";
    private const string GeminiDefaultEndpoint = "https://generativelanguage.googleapis.com";

    /// <inheritdoc />
    public IChatClient Create(LlmProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Provider.ToLowerInvariant() switch
        {
            OpenAiProvider or DeepSeekProvider or OpenAiCompatibleProvider
                => CreateOpenAiCompatible(config),
            AnthropicProvider => CreateAnthropic(config),
            GeminiProvider => CreateGemini(config),
            OllamaProvider => CreateOllama(config),
            AzureOpenAiProvider => CreateAzureOpenAi(config),
            _ => throw new InvalidOperationException(
                $"Unsupported provider: {config.Provider}"),
        };
    }

    private static IChatClient CreateOpenAiCompatible(LlmProviderConfig config)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? OpenAiDefaultEndpoint
            : config.Endpoint;
        return BuildOpenAiClient(endpoint, config.ApiKey).GetChatClient(config.Model).AsIChatClient();
    }

    private static IChatClient CreateOllama(LlmProviderConfig config)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? OllamaDefaultEndpoint
            : config.Endpoint;
        // Ollama exposes an OpenAI-compatible /v1/chat/completions
        // endpoint; route through the OpenAI SDK with the overridden base URL.
        return BuildOpenAiClient(endpoint, config.ApiKey).GetChatClient(config.Model).AsIChatClient();
    }

    private static IChatClient CreateAzureOpenAi(LlmProviderConfig config)
    {
        // Azure OpenAI uses the same SDK; the caller supplies the Azure
        // endpoint URL. An api key is required.
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                "Azure OpenAI requires an api key.");
        }
        return BuildOpenAiClient(config.Endpoint, config.ApiKey)
            .GetChatClient(config.Model)
            .AsIChatClient();
    }

    private static IChatClient CreateAnthropic(LlmProviderConfig config)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? AnthropicDefaultEndpoint
            : config.Endpoint;
        return new HttpJsonChatClient(
            endpoint,
            config.Model,
            config.ApiKey,
            new ChatClientMetadata("anthropic", new Uri(endpoint), config.Model));
    }

    private static IChatClient CreateGemini(LlmProviderConfig config)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? GeminiDefaultEndpoint
            : config.Endpoint;
        return new HttpJsonChatClient(
            endpoint,
            config.Model,
            config.ApiKey,
            new ChatClientMetadata("gemini", new Uri(endpoint), config.Model));
    }

    private static OpenAIClient BuildOpenAiClient(string endpoint, string? apiKey)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
        };
        var credential = string.IsNullOrWhiteSpace(apiKey)
            ? new ApiKeyCredential("not-required")
            : new ApiKeyCredential(apiKey);
        return new OpenAIClient(credential, options);
    }
}
