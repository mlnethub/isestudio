using Microsoft.Extensions.AI;
using ISEStudio.Llm;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// xUnit collection name that every test class touching the shared
/// <see cref="FakeChatClientFactory.Default"/> must join. The factory is a
/// process-wide singleton; without collection-level serialization, xUnit
/// runs the three extraction test classes in parallel and one class's
/// ctor stomps another's <c>UseClient</c> install.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ExtractionTestCollection
{
    public const string Name = "ExtractionSharedChatClient";
}

/// <summary>
/// Test-only <see cref="IChatClientFactory"/> singleton shared by every
/// extraction test. The factory holds one mutable client reference; tests
/// swap the reference via <see cref="UseClient"/> (or wrap a per-test
/// <see cref="FakeChat"/>) and call <see cref="Reset"/> between tests so
/// parallel runs do not bleed state.
/// </summary>
public sealed class FakeChatClientFactory : IChatClientFactory
{
    /// <summary>Process-wide singleton registered by
    /// <c>AuthTestWebApplicationFactory</c>. All extraction tests share
    /// this instance.</summary>
    public static FakeChatClientFactory Default { get; } = new();

    private IChatClient? _client;
    private readonly object _gate = new();

    /// <summary>Install the client every <see cref="Create"/> call returns.
    /// Pass <c>null</c> to make the factory throw — useful for asserting
    /// the orchestrator never reached the chat layer.</summary>
    public void UseClient(IChatClient? client)
    {
        lock (_gate) _client = client;
    }

    /// <summary>Detach the client so the next test starts clean. Always
    /// call from test setup or <see cref="IDisposable.Dispose"/>.</summary>
    public void Reset()
    {
        lock (_gate) _client = null;
    }

    public IChatClient Create(LlmProviderConfig config)
    {
        var client = _client;
        if (client is null)
        {
            throw new InvalidOperationException(
                "FakeChatClientFactory has no client installed. " +
                "Call FakeChatClientFactory.Default.UseClient(...) in test setup.");
        }
        return client;
    }
}