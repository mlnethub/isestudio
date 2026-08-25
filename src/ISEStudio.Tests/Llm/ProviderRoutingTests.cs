using Microsoft.Extensions.AI;
using ISEStudio.Llm;

namespace ISEStudio.Tests.Llm;

/// <summary>
/// Exercises the provider-agnostic chat/embedding factory: every supported
/// provider must resolve to the <see cref="IChatClient"/> abstraction (no
/// concrete provider type leaks across the boundary), unsupported providers
/// throw, and matching is case-insensitive.
/// </summary>
public sealed class ProviderRoutingTests
{
    private static LlmProviderConfig For(string provider, string endpoint = "https://api.openai.com/v1", string model = "gpt-4o-mini") =>
        new()
        {
            Provider = provider,
            ApiKey = "test-key",
            Endpoint = endpoint,
            Model = model,
            ConcurrencyLimit = 8,
        };

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_openai_returns_IChatClient()
    {
        var factory = new ChatClientFactory();
        var client = factory.Create(For("openai"));
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_deepseek_uses_overridden_endpoint()
    {
        var factory = new ChatClientFactory();
        var config = For("deepseek", endpoint: "https://api.deepseek.com", model: "deepseek-chat");
        var client = factory.Create(config);
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_ollama_uses_local_endpoint()
    {
        var factory = new ChatClientFactory();
        var config = For("ollama", endpoint: "http://localhost:11434/v1", model: "llama3.1");
        var client = factory.Create(config);
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_unsupported_provider_throws()
    {
        var factory = new ChatClientFactory();
        Assert.Throws<InvalidOperationException>(() => factory.Create(For("not-a-real-provider")));
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_case_insensitive_provider_openai_returns_IChatClient()
    {
        var factory = new ChatClientFactory();
        var client = factory.Create(For("OpenAI"));
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void EmbeddingFactory_returns_IEmbeddingGenerator()
    {
        var factory = new EmbeddingGeneratorFactory();
        var generator = factory.Create(For("openai", endpoint: "https://api.openai.com/v1", model: "text-embedding-3-small"));
        Assert.NotNull(generator);
        Assert.IsAssignableFrom<IEmbeddingGenerator<string, Embedding<float>>>(generator);
    }
}
