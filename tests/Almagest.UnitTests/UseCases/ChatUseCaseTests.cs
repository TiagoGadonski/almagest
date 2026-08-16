using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.Domain;
using Almagest.UnitTests.TestDoubles;

namespace Almagest.UnitTests.UseCases;

public class ChatUseCaseTests
{
    private static readonly RetrievalOptions DefaultRetrievalOptions = new(TopK: 5, SimilarityFloor: 0.70, MaxContextTokens: 4000);
    private static readonly ConversationOptions DefaultConversationOptions = new(MaxActiveMessages: 20, RetainRecentMessages: 6);

    [Fact]
    public async Task StreamAsync_NewSession_CreatesSessionAndPersistsBothMessages()
    {
        var conversationStore = new FakeConversationStore();
        var chatService = new FakeChatService(streamFragments: ["Hello", " world"]);
        var summarizer = new ConversationSummarizer(chatService);
        var useCase = new ChatUseCase(
            conversationStore, new FakeEmbeddingService("test-model"), new FakeChunkStore(), chatService,
            summarizer, DefaultRetrievalOptions, DefaultConversationOptions);

        var result = await useCase.StreamAsync(sessionId: null, "Hi there");
        var fragments = new List<string>();
        await foreach (var fragment in result.AnswerFragments)
        {
            fragments.Add(fragment);
        }

        Assert.Equal(["Hello", " world"], fragments);
        Assert.NotEqual(Guid.Empty, result.SessionId);
        Assert.Equal(2, conversationStore.AppendedMessages.Count);
        Assert.Equal(MessageRole.User, conversationStore.AppendedMessages[0].Role);
        Assert.Equal("Hi there", conversationStore.AppendedMessages[0].Content);
        Assert.Equal(MessageRole.Assistant, conversationStore.AppendedMessages[1].Role);
        Assert.Equal("Hello world", conversationStore.AppendedMessages[1].Content);
    }

    [Fact]
    public async Task StreamAsync_ExistingSession_IncludesPriorMessagesInThePrompt()
    {
        var conversationStore = new FakeConversationStore();
        var session = Session.Create();
        await conversationStore.SaveSessionAsync(session);
        await conversationStore.AppendMessageAsync(Message.Create(session.Id, MessageRole.User, "What's the capital of France?", 0));
        await conversationStore.AppendMessageAsync(Message.Create(session.Id, MessageRole.Assistant, "Paris.", 1));

        var chatService = new FakeChatService(streamFragments: ["It's a big city."]);
        var summarizer = new ConversationSummarizer(chatService);
        var useCase = new ChatUseCase(
            conversationStore, new FakeEmbeddingService("test-model"), new FakeChunkStore(), chatService,
            summarizer, DefaultRetrievalOptions, DefaultConversationOptions);

        var result = await useCase.StreamAsync(session.Id, "How big is it?");
        await foreach (var _ in result.AnswerFragments)
        {
        }

        Assert.Equal(session.Id, result.SessionId);
        Assert.Contains("What's the capital of France?", chatService.LastUserPrompt);
        Assert.Contains("Paris.", chatService.LastUserPrompt);
        Assert.Contains("How big is it?", chatService.LastUserPrompt);
    }

    [Fact]
    public async Task StreamAsync_ActiveWindowExceedsThreshold_SummarizesOldestMessages()
    {
        var conversationStore = new FakeConversationStore();
        var session = Session.Create();
        await conversationStore.SaveSessionAsync(session);
        for (var i = 0; i < 20; i++)
        {
            await conversationStore.AppendMessageAsync(Message.Create(session.Id, MessageRole.User, $"message {i}", i));
        }

        var chatService = new FakeChatService("a summary", streamFragments: ["ok"]);
        var summarizer = new ConversationSummarizer(chatService);
        var options = new ConversationOptions(MaxActiveMessages: 20, RetainRecentMessages: 6);
        var useCase = new ChatUseCase(
            conversationStore, new FakeEmbeddingService("test-model"), new FakeChunkStore(), chatService,
            summarizer, DefaultRetrievalOptions, options);

        var result = await useCase.StreamAsync(session.Id, "one more");
        await foreach (var _ in result.AnswerFragments)
        {
        }

        // 20 pre-existing + 1 new user message = 21 active messages > threshold of 20 -> summarize.
        Assert.Equal(2, chatService.CallCount); // one CompleteAsync (summarize) + one StreamCompleteAsync (answer)

        var updatedSession = await conversationStore.FindSessionAsync(session.Id);
        Assert.Equal("a summary", updatedSession!.Summary);

        // RetainRecentMessages messages survived the fold, plus the
        // assistant's reply that was appended after the turn completed.
        var remainingActive = await conversationStore.GetMessagesAsync(session.Id, updatedSession.SummarizedThroughPosition);
        Assert.Equal(options.RetainRecentMessages + 1, remainingActive.Count);
    }

    [Fact]
    public async Task StreamAsync_RetrievedChunksBelowFloor_AreExcludedFromThePrompt()
    {
        var chunk = DocumentChunk.Create(Guid.NewGuid(), "irrelevant text", 0, null);
        var chunkStore = new FakeChunkStore([new ScoredChunk(chunk, 0.10, "test-model")]);
        var chatService = new FakeChatService(streamFragments: ["answer"]);
        var summarizer = new ConversationSummarizer(chatService);
        var useCase = new ChatUseCase(
            new FakeConversationStore(), new FakeEmbeddingService("test-model"), chunkStore, chatService,
            summarizer, DefaultRetrievalOptions, DefaultConversationOptions);

        var result = await useCase.StreamAsync(sessionId: null, "question");
        await foreach (var _ in result.AnswerFragments)
        {
        }

        Assert.DoesNotContain("irrelevant text", chatService.LastUserPrompt);
    }

    [Fact]
    public async Task StreamAsync_EmbedsWithQueryPurpose_NotDocumentPurpose()
    {
        var embeddingService = new FakeEmbeddingService("test-model");
        var chatService = new FakeChatService(streamFragments: ["answer"]);
        var summarizer = new ConversationSummarizer(chatService);
        var useCase = new ChatUseCase(
            new FakeConversationStore(), embeddingService, new FakeChunkStore(), chatService,
            summarizer, DefaultRetrievalOptions, DefaultConversationOptions);

        var result = await useCase.StreamAsync(sessionId: null, "question");
        await foreach (var _ in result.AnswerFragments)
        {
        }

        Assert.Equal(EmbeddingPurpose.Query, embeddingService.LastPurpose);
    }
}
