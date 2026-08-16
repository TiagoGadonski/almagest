using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.Domain;
using Almagest.UnitTests.TestDoubles;

namespace Almagest.UnitTests.UseCases;

public class IngestDocumentUseCaseTests
{
    private static readonly ChunkingOptions DefaultOptions = new(TargetTokens: 800, OverlapRatio: 0.12);

    [Fact]
    public async Task ExecuteAsync_ParsesChunksEmbedsExtractsMetadataAndPersists()
    {
        var parser = new FakeDocumentParser(DocumentSource.Markdown, new ParsedDocument("full text", []));
        var chunker = new FakeTextChunker([new TextChunk("first chunk", 0, "Intro"), new TextChunk("second chunk", 1, null)]);
        var embeddingService = new FakeEmbeddingService("test-model");
        var chunkStore = new FakeChunkStore();
        var metadata = new MetadataExtractionResult(true, DocumentMetadata.Create(Guid.NewGuid(), "report", null, null));
        var metadataExtractor = new FakeMetadataExtractor(metadata);

        var useCase = new IngestDocumentUseCase([parser], chunker, embeddingService, chunkStore, metadataExtractor, DefaultOptions);

        using var content = new MemoryStream();
        var result = await useCase.ExecuteAsync(new IngestRequest("Title", DocumentSource.Markdown, content));

        Assert.Equal(2, result.ChunkCount);
        Assert.True(result.MetadataExtracted);
        Assert.Equal(2, chunkStore.Saved.Count);
        Assert.All(chunkStore.Saved, saved => Assert.Equal(result.DocumentId, saved.Chunk.DocumentId));
        Assert.All(chunkStore.Saved, saved => Assert.Equal("test-model", saved.EmbeddingModelId));
        Assert.Equal(["first chunk", "second chunk"], embeddingService.LastRequest);
        Assert.Equal(EmbeddingPurpose.Document, embeddingService.LastPurpose);
        Assert.Equal(result.DocumentId, chunkStore.SavedDocument?.Id);
        Assert.Equal("report", chunkStore.SavedMetadata?.DocumentType);
    }

    [Fact]
    public async Task ExecuteAsync_MetadataExtractionFails_PersistsChunksWithoutMetadata()
    {
        var parser = new FakeDocumentParser(DocumentSource.Markdown, new ParsedDocument("full text", []));
        var chunker = new FakeTextChunker([new TextChunk("first chunk", 0, null)]);
        var chunkStore = new FakeChunkStore();
        var metadataExtractor = new FakeMetadataExtractor(new MetadataExtractionResult(false, null));

        var useCase = new IngestDocumentUseCase(
            [parser], chunker, new FakeEmbeddingService("test-model"), chunkStore, metadataExtractor, DefaultOptions);

        using var content = new MemoryStream();
        var result = await useCase.ExecuteAsync(new IngestRequest("Title", DocumentSource.Markdown, content));

        Assert.False(result.MetadataExtracted);
        Assert.Single(chunkStore.Saved);
        Assert.Null(chunkStore.SavedMetadata);
    }

    [Fact]
    public async Task ExecuteAsync_NoParserRegisteredForSource_Throws()
    {
        var useCase = new IngestDocumentUseCase(
            [], new FakeTextChunker([]), new FakeEmbeddingService("m"), new FakeChunkStore(), new FakeMetadataExtractor(), DefaultOptions);

        using var content = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(new IngestRequest("Title", DocumentSource.Pdf, content)));
    }

    [Fact]
    public async Task ExecuteAsync_NoChunksProduced_SkipsEmbedAndSave()
    {
        var parser = new FakeDocumentParser(DocumentSource.Markdown, new ParsedDocument("text", []));
        var embeddingService = new FakeEmbeddingService("test-model");
        var chunkStore = new FakeChunkStore();

        var useCase = new IngestDocumentUseCase(
            [parser], new FakeTextChunker([]), embeddingService, chunkStore, new FakeMetadataExtractor(), DefaultOptions);

        using var content = new MemoryStream();
        var result = await useCase.ExecuteAsync(new IngestRequest("Title", DocumentSource.Markdown, content));

        Assert.Equal(0, result.ChunkCount);
        Assert.Empty(chunkStore.Saved);
        Assert.Null(embeddingService.LastRequest);
    }
}
