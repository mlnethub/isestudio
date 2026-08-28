using Microsoft.Extensions.AI;

namespace ISEStudio.Tests.Extraction.Dovetail.TBox.Steps;

internal sealed class TestChatClient(string cannedResponse) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, cannedResponse)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
