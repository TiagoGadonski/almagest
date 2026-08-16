using Microsoft.Extensions.AI;

namespace Almagest.UnitTests.TestDoubles;

// Minimal fake of the raw Microsoft.Extensions.AI IChatClient, for testing
// ClaudeMetadataExtractor's tool-calling/validation/retry flow without a
// real Anthropic connection. Only GetResponseAsync is exercised by that
// class -- streaming and GetService are never called here.
public sealed class FakeChatClient(params ChatResponse[] responses) : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new(responses);
    private Exception? _exceptionToThrow;

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    // Simulates a transport/provider-level failure (timeout, 5xx, rate
    // limit) rather than a well-formed response -- distinct from every
    // scenario above, which all get a real ChatResponse back.
    public static FakeChatClient Throwing(Exception exception)
    {
        var client = new FakeChatClient();
        client._exceptionToThrow = exception;
        return client;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());

        if (_exceptionToThrow is { } exception)
        {
            throw exception;
        }

        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));

        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not used by these tests.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
