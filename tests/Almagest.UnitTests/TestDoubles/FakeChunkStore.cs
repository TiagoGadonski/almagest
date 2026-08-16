using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeChunkStore(IReadOnlyList<ScoredChunk>? searchResult = null) : IChunkStore
{
    public List<EmbeddedChunk> Saved { get; } = [];

    public Document? SavedDocument { get; private set; }

    public DocumentMetadata? SavedMetadata { get; private set; }

    public ChunkSearchFilter? LastFilter { get; private set; }

    public Task SaveAsync(
        Document document, IReadOnlyList<EmbeddedChunk> chunks, DocumentMetadata? metadata, CancellationToken cancellationToken = default)
    {
        SavedDocument = document;
        SavedMetadata = metadata;
        Saved.AddRange(chunks);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        float[] queryEmbedding, int topK, ChunkSearchFilter? filter = null, CancellationToken cancellationToken = default)
    {
        LastFilter = filter;
        return Task.FromResult(searchResult ?? []);
    }
}
