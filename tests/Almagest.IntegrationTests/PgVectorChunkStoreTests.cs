using Almagest.Application.Ports;
using Almagest.Domain;
using Almagest.Infrastructure.Persistence;

namespace Almagest.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class PgVectorChunkStoreTests
{
    private readonly PostgresFixture _fixture;

    public PgVectorChunkStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveAsync_ThenSearchAsync_ReturnsTheClosestVectorFirst()
    {
        var store = new PgVectorChunkStore(_fixture.DataSource);
        var document = Document.Create($"Test Document {Guid.NewGuid()}", DocumentSource.Markdown);

        var closeChunk = DocumentChunk.Create(document.Id, "close chunk text", 0, null);
        var farChunk = DocumentChunk.Create(document.Id, "far chunk text", 1, null);

        var closeVector = BuildVector(1.0f);
        var farVector = BuildVector(-1.0f);

        var embeddedChunks = new List<EmbeddedChunk>
        {
            new(closeChunk, closeVector, "test-model"),
            new(farChunk, farVector, "test-model"),
        };

        // Tagged and filtered by a document type unique to this test: other
        // tests in this shared, uncleaned Postgres fixture also use
        // BuildVector(1.0f), and an unfiltered, unscoped search would pick
        // up their rows too, making the ranking assertion below depend on
        // test execution order across the whole collection.
        var metadata = DocumentMetadata.Create(document.Id, documentType: "closest-vector-test", null, null);
        await store.SaveAsync(document, embeddedChunks, metadata);

        var queryVector = BuildVector(1.0f); // identical to closeVector
        var results = await store.SearchAsync(queryVector, topK: 2, new ChunkSearchFilter(DocumentType: "closest-vector-test"));

        Assert.Equal(2, results.Count);
        Assert.Equal(closeChunk.Id, results[0].Chunk.Id);
        Assert.True(results[0].Similarity > results[1].Similarity);
    }

    [Fact]
    public async Task SearchAsync_WithDocumentTypeFilter_ExcludesNonMatchingDocuments()
    {
        var store = new PgVectorChunkStore(_fixture.DataSource);

        var matchingDocument = Document.Create($"Report {Guid.NewGuid()}", DocumentSource.Pdf);
        var matchingChunk = DocumentChunk.Create(matchingDocument.Id, "matching chunk", 0, null);
        var matchingMetadata = DocumentMetadata.Create(matchingDocument.Id, documentType: "report", null, null);
        await store.SaveAsync(matchingDocument, [new EmbeddedChunk(matchingChunk, BuildVector(1.0f), "test-model")], matchingMetadata);

        var otherDocument = Document.Create($"Note {Guid.NewGuid()}", DocumentSource.Markdown);
        var otherChunk = DocumentChunk.Create(otherDocument.Id, "other chunk", 0, null);
        var otherMetadata = DocumentMetadata.Create(otherDocument.Id, documentType: "note", null, null);
        await store.SaveAsync(otherDocument, [new EmbeddedChunk(otherChunk, BuildVector(1.0f), "test-model")], otherMetadata);

        var results = await store.SearchAsync(BuildVector(1.0f), topK: 10, new ChunkSearchFilter(DocumentType: "report"));

        Assert.All(results, r => Assert.NotEqual(otherChunk.Id, r.Chunk.Id));
        Assert.Contains(results, r => r.Chunk.Id == matchingChunk.Id);
    }

    private static float[] BuildVector(float seed)
    {
        var vector = new float[1024];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = seed * (i % 7 == 0 ? 1f : 0.1f);
        }

        return vector;
    }
}
