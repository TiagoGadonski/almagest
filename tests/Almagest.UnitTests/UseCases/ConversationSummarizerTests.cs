using Almagest.Application.UseCases;
using Almagest.Domain;
using Almagest.UnitTests.TestDoubles;

namespace Almagest.UnitTests.UseCases;

public class ConversationSummarizerTests
{
    private static readonly ConversationOptions Options = new(MaxActiveMessages: 20, RetainRecentMessages: 6);

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void ShouldSummarize_TriggersOnlyPastTheConfiguredThreshold(int activeMessageCount, bool expected)
    {
        var summarizer = new ConversationSummarizer(new FakeChatService());

        Assert.Equal(expected, summarizer.ShouldSummarize(activeMessageCount, Options));
    }

    [Fact]
    public async Task SummarizeAsync_NoExistingSummary_SendsMessagesToFoldOnly()
    {
        var chatService = new FakeChatService("summary text");
        var summarizer = new ConversationSummarizer(chatService);
        var sessionId = Guid.NewGuid();
        var messages = new[]
        {
            Message.Create(sessionId, MessageRole.User, "hello", 0),
            Message.Create(sessionId, MessageRole.Assistant, "hi there", 1),
        };

        var summary = await summarizer.SummarizeAsync(existingSummary: null, messages);

        Assert.Equal("summary text", summary);
        Assert.True(chatService.WasCalled);
        Assert.DoesNotContain("Existing summary", chatService.LastUserPrompt);
        Assert.Contains("hello", chatService.LastUserPrompt);
        Assert.Contains("hi there", chatService.LastUserPrompt);
    }

    [Fact]
    public async Task SummarizeAsync_WithExistingSummary_IncludesItInThePrompt()
    {
        var chatService = new FakeChatService("updated summary");
        var summarizer = new ConversationSummarizer(chatService);
        var sessionId = Guid.NewGuid();
        var messages = new[] { Message.Create(sessionId, MessageRole.User, "more context", 5) };

        await summarizer.SummarizeAsync(existingSummary: "previous summary", messages);

        Assert.Contains("previous summary", chatService.LastUserPrompt);
        Assert.Contains("more context", chatService.LastUserPrompt);
    }
}
