using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeChatService(string response = "fake answer", IReadOnlyList<string>? streamFragments = null) : IChatService
{
    public string? LastSystemPrompt { get; private set; }

    public string? LastUserPrompt { get; private set; }

    public bool WasCalled { get; private set; }

    public int CallCount { get; private set; }

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        CallCount++;
        LastSystemPrompt = systemPrompt;
        LastUserPrompt = userPrompt;
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<string> StreamCompleteAsync(
        string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        CallCount++;
        LastSystemPrompt = systemPrompt;
        LastUserPrompt = userPrompt;

        foreach (var fragment in streamFragments ?? [response])
        {
            await Task.Yield();
            yield return fragment;
        }
    }
}
