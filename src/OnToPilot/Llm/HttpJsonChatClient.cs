using Microsoft.Extensions.AI;

namespace OnToPilot.Llm;

/// <summary>
/// Minimal <see cref="IChatClient"/> adapter that wraps an
/// <see cref="HttpClient"/> for providers that do not yet ship a
/// <c>Microsoft.Extensions.AI.*</c> provider package (currently
/// Anthropic and Google Gemini).
///
/// <para>The adapter records the provider's <see cref="ChatClientMetadata"/>
/// so callers can inspect the configured endpoint and model without
/// triggering network traffic. The actual HTTP call surfaces a clear
/// <see cref="NotImplementedException"/> with a TODO — wire the
/// provider-specific JSON schema in before this is used for real
/// inference.</para>
/// </summary>
internal sealed class HttpJsonChatClient : IChatClient
{
    private readonly string _endpoint;
    private readonly string _apiKey;

    public HttpJsonChatClient(
        string endpoint,
        string model,
        string? apiKey,
        ChatClientMetadata metadata)
    {
        _endpoint = endpoint;
        _apiKey = apiKey ?? string.Empty;
        Metadata = metadata;
    }

    public ChatClientMetadata Metadata { get; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Provider-specific HTTP shape is a TODO. Until that's wired in
        // the factory still returns a non-null IChatClient (so callers
        // can wire capacity / DI / config) but actual inference fails
        // fast with a clear message rather than silently mis-routing.
        throw new NotImplementedException(
            $"HttpJsonChatClient has no provider-specific implementation for endpoint '{_endpoint}'. " +
            "Wire the Anthropic Messages / Gemini generateContent JSON shape before using this route.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(
            $"HttpJsonChatClient has no provider-specific implementation for endpoint '{_endpoint}'. " +
            "Wire the Anthropic Messages / Gemini generateContent JSON shape before using this route.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        // No resources to release.
    }
}
