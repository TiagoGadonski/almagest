using Almagest.Domain;

namespace Almagest.Application.Ports;

public interface IChunkStore
{
    Task SaveAsync(
        Document document,
        IReadOnlyList<EmbeddedChunk> chunks,
        DocumentMetadata? metadata,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        ChunkSearchFilter? filter = null,
        CancellationToken cancellationToken = default);
}

public sealed record EmbeddedChunk(DocumentChunk Chunk, float[] Embedding, string EmbeddingModelId);

public sealed record ScoredChunk(DocumentChunk Chunk, double Similarity, string EmbeddingModelId);

public sealed record ChunkSearchFilter(
    string? DocumentType = null,
    DateOnly? DateRangeStart = null,
    DateOnly? DateRangeEnd = null,
    IReadOnlyList<string>? Tags = null);
