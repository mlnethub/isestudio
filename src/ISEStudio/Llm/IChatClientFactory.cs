using Microsoft.Extensions.AI;

namespace ISEStudio.Llm;

/// <summary>
/// Builds provider-agnostic chat clients from
/// <see cref="LlmProviderConfig"/>. The public surface is always
/// <see cref="IChatClient"/>; concrete provider types stay inside the
/// implementation so callers can swap providers without code changes.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Create an <see cref="IChatClient"/> configured for the provider
    /// named in <paramref name="config"/>. Throws
    /// <see cref="InvalidOperationException"/> when
    /// <see cref="LlmProviderConfig.Provider"/> is not one of the
    /// supported names.
    /// </summary>
    IChatClient Create(LlmProviderConfig config);
}
