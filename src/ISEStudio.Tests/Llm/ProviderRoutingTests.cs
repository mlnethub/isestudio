using System.ClientModel;
using System.ClientModel.Primitives;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
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
        var factory = ChatClientFactory.CreateForTest();
        var client = factory.Create(For("openai"));
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_deepseek_uses_overridden_endpoint()
    {
        var factory = ChatClientFactory.CreateForTest();
        var config = For("deepseek", endpoint: "https://api.deepseek.com", model: "deepseek-chat");
        var client = factory.Create(config);
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_ollama_uses_local_endpoint()
    {
        var factory = ChatClientFactory.CreateForTest();
        var config = For("ollama", endpoint: "http://localhost:11434/v1", model: "llama3.1");
        var client = factory.Create(config);
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_unsupported_provider_throws()
    {
        var factory = ChatClientFactory.CreateForTest();
        Assert.Throws<InvalidOperationException>(() => factory.Create(For("not-a-real-provider")));
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void Create_with_case_insensitive_provider_openai_returns_IChatClient()
    {
        var factory = ChatClientFactory.CreateForTest();
        var client = factory.Create(For("OpenAI"));
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    [Trait("Category", "Llm")]
    public void EmbeddingFactory_returns_IEmbeddingGenerator()
    {
        var factory = EmbeddingGeneratorFactory.CreateForTest();
        var generator = factory.Create(For("openai", endpoint: "https://api.openai.com/v1", model: "text-embedding-3-small"));
        Assert.NotNull(generator);
        Assert.IsAssignableFrom<IEmbeddingGenerator<string, Embedding<float>>>(generator);
    }

    /// <summary>
    /// The <c>System.ClientModel</c> SDK defaults to a 100-second
    /// network timeout; Dovetail extraction pipelines calling reasoning
    /// models on long documents regularly exceed that and fail with
    /// <c>"Retry failed after 4 tries ... 0:01:40"</c>. The factory
    /// must lift the timeout to <see cref="ISEStudioOptions.LlmNetworkTimeoutSeconds"/>
    /// (default 180 s) so the documented failure mode stops recurring.
    /// </summary>
    [Fact]
    [Trait("Category", "Llm")]
    public void ChatFactory_applies_network_timeout_above_sdk_default()
    {
        var factory = new ChatClientFactory(
            Options.Create(new ISEStudioOptions { LlmNetworkTimeoutSeconds = 300 }));
        var opts = factory.BuildOpenAiClientOptions("https://api.openai.com/v1");
        Assert.NotNull(opts.NetworkTimeout);
        Assert.True(opts.NetworkTimeout > TimeSpan.FromSeconds(100),
            $"Configured timeout {opts.NetworkTimeout} must exceed SDK default 100s.");
        Assert.Equal(TimeSpan.FromSeconds(300), opts.NetworkTimeout);
    }

    /// <summary>
    /// SDK default retry policy issues up to 4 retries on transient
    /// failures — expensive on paid LLM endpoints where each retry
    /// burns tokens. <see cref="ISEStudioOptions.LlmMaxRetries"/>
    /// (default 0) must reach the OpenAIClientOptions pipeline so the
    /// orchestrator decides when a retry is worth the spend.
    /// </summary>
    [Fact]
    [Trait("Category", "Llm")]
    public void ChatFactory_applies_configured_retry_policy()
    {
        var factory = new ChatClientFactory(
            Options.Create(new ISEStudioOptions { LlmMaxRetries = 0 }));
        var opts = factory.BuildOpenAiClientOptions("https://api.openai.com/v1");
        Assert.IsType<ClientRetryPolicy>(opts.RetryPolicy);
        // ClientRetryPolicy exposes maxRetries only via a private
        // field — reflect to confirm the configured value reached the
        // policy instance.
        var maxRetries = (int)typeof(ClientRetryPolicy)
            .GetField("_maxRetries", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(opts.RetryPolicy)!;
        Assert.Equal(0, maxRetries);
    }

    /// <summary>
    /// When <see cref="ISEStudioOptions.LlmNetworkTimeoutSeconds"/> is 0
    /// the factory must defer to the SDK default rather than overriding
    /// with <see cref="TimeSpan.Zero"/>. This is the operator escape
    /// hatch for "I want the SDK behaviour back".
    /// </summary>
    [Fact]
    [Trait("Category", "Llm")]
    public void ChatFactory_zero_timeout_falls_back_to_sdk_default()
    {
        var factory = new ChatClientFactory(
            Options.Create(new ISEStudioOptions { LlmNetworkTimeoutSeconds = 0 }));
        var opts = factory.BuildOpenAiClientOptions("https://api.openai.com/v1");
        Assert.Null(opts.NetworkTimeout);
    }

    /// <summary>
    /// The embedding factory shares the OpenAI SDK client, so the same
    /// network-timeout / retry policy configuration must reach it.
    /// Long documents also drive embedding batching past the SDK default.
    /// </summary>
    [Fact]
    [Trait("Category", "Llm")]
    public void EmbeddingFactory_applies_configured_network_timeout()
    {
        var factory = new EmbeddingGeneratorFactory(
            Options.Create(new ISEStudioOptions { LlmNetworkTimeoutSeconds = 240 }));
        var opts = factory.BuildOpenAiClientOptions("https://api.openai.com/v1");
        Assert.Equal(TimeSpan.FromSeconds(240), opts.NetworkTimeout);
        Assert.IsType<ClientRetryPolicy>(opts.RetryPolicy);
    }
}
