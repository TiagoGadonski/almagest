using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.Domain;
using Almagest.UnitTests.TestDoubles;

namespace Almagest.UnitTests.UseCases;

public class AskQuestionUseCaseTests
{
    private static readonly RetrievalOptions Options = new(TopK: 5, SimilarityFloor: 0.70, MaxContextTokens: 4000);

    [Fact]
    public async Task ExecuteAsync_RelevantChunksFound_ReturnsGroundedAnswerWithCitations()
    {
        var chunk = DocumentChunk.Create(Guid.NewGuid(), "Paris is the capital of France.", 0, "Geography");
        var chunkStore = new FakeChunkStore([new ScoredChunk(chunk, 0.85, "test-model")]);
        var chatService = new FakeChatService($"Paris [chunk:{chunk.Id}]");

        var useCase = new AskQuestionUseCase(new FakeEmbeddingService("test-model"), chunkStore, chatService, Options);

        var result = await useCase.ExecuteAsync("What is the capital of France?");

        Assert.True(result.Found);
        Assert.Equal($"Paris [chunk:{chunk.Id}]", result.Answer);
        Assert.Single(result.Citations);
        Assert.Equal(chunk.Id, result.Citations[0].ChunkId);
        Assert.True(chatService.WasCalled);
        Assert.Contains(chunk.Text, chatService.LastUserPrompt);
    }

    [Fact]
    public async Task ExecuteAsync_NothingClearsFloor_ReturnsNotFoundWithoutCallingChat()
    {
        var chunk = DocumentChunk.Create(Guid.NewGuid(), "unrelated text", 0, null);
        var chunkStore = new FakeChunkStore([new ScoredChunk(chunk, 0.40, "test-model")]);
        var chatService = new FakeChatService();

        var useCase = new AskQuestionUseCase(new FakeEmbeddingService("test-model"), chunkStore, chatService, Options);

        var result = await useCase.ExecuteAsync("Anything?");

        Assert.False(result.Found);
        Assert.Empty(result.Citations);
        Assert.False(chatService.WasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_EmbeddingModelMismatch_ExcludesChunkEvenIfSimilarityIsHigh()
    {
        var chunk = DocumentChunk.Create(Guid.NewGuid(), "high similarity but stale embedding", 0, null);
        var chunkStore = new FakeChunkStore([new ScoredChunk(chunk, 0.99, "old-model")]);
        var chatService = new FakeChatService();

        var useCase = new AskQuestionUseCase(new FakeEmbeddingService("current-model"), chunkStore, chatService, Options);

        var result = await useCase.ExecuteAsync("Anything?");

        Assert.False(result.Found);
        Assert.False(chatService.WasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_EmbedsTheQuestionItself()
    {
        var embeddingService = new FakeEmbeddingService("test-model");
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);

        await useCase.ExecuteAsync("What is X?");

        Assert.Equal(["What is X?"], embeddingService.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_EmbedsWithQueryPurpose_NotDocumentPurpose()
    {
        var embeddingService = new FakeEmbeddingService("test-model");
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);

        await useCase.ExecuteAsync("What is X?");

        Assert.Equal(EmbeddingPurpose.Query, embeddingService.LastPurpose);
    }

    [Fact]
    public async Task ExecuteAsync_PrecomputedEmbeddingGiven_DoesNotEmbedAgain()
    {
        var chunk = DocumentChunk.Create(Guid.NewGuid(), "Paris is the capital of France.", 0, "Geography");
        var chunkStore = new FakeChunkStore([new ScoredChunk(chunk, 0.85, "test-model")]);
        var embeddingService = new FakeEmbeddingService("test-model");
        var useCase = new AskQuestionUseCase(embeddingService, chunkStore, new FakeChatService(), Options);
        var precomputed = new float[] { 1f, 2f, 3f };

        var result = await useCase.ExecuteAsync("What is the capital of France?", precomputed);

        Assert.Equal(0, embeddingService.CallCount); // reused, not re-embedded
        Assert.True(result.Found);
    }

    [Fact]
    public async Task ExecuteAsync_NoPrecomputedEmbedding_EmbedsExactlyOnce()
    {
        var embeddingService = new FakeEmbeddingService("test-model");
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);

        await useCase.ExecuteAsync("What is X?");

        Assert.Equal(1, embeddingService.CallCount);
    }
}
