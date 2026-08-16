using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.Application.UseCases;

public sealed record IngestRequest(string Title, DocumentSource Source, Stream Content);

public sealed record IngestResult(Guid DocumentId, int ChunkCount, bool MetadataExtracted);

// parse -> chunk -> embed in batch -> extract metadata -> persist. Note:
// ITextChunker.Chunk throws NotImplementedException until RecursiveTextChunker
// is implemented, so ExecuteAsync is fully wired but cannot complete an
// end-to-end run yet.
public sealed class IngestDocumentUseCase
{
    private readonly IReadOnlyDictionary<DocumentSource, IDocumentParser> _parsers;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly IChunkStore _chunkStore;
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly ChunkingOptions _chunkingOptions;

    public IngestDocumentUseCase(
        IEnumerable<IDocumentParser> parsers,
        ITextChunker chunker,
        IEmbeddingService embeddingService,
        IChunkStore chunkStore,
        IMetadataExtractor metadataExtractor,
        ChunkingOptions chunkingOptions)
    {
        _parsers = parsers.ToDictionary(parser => parser.Source);
        _chunker = chunker;
        _embeddingService = embeddingService;
        _chunkStore = chunkStore;
        _metadataExtractor = metadataExtractor;
        _chunkingOptions = chunkingOptions;
    }

    public async Task<IngestResult> ExecuteAsync(IngestRequest request, CancellationToken cancellationToken = default)
    {
        if (!_parsers.TryGetValue(request.Source, out var parser))
        {
            throw new InvalidOperationException($"No parser registered for document source '{request.Source}'.");
        }

        var document = Document.Create(request.Title, request.Source);

        var parsed = await parser.ParseAsync(request.Content, cancellationToken).ConfigureAwait(false);
        var textChunks = _chunker.Chunk(parsed, _chunkingOptions);

        if (textChunks.Count == 0)
        {
            return new IngestResult(document.Id, 0, false);
        }

        var embeddings = await _embeddingService
            .EmbedAsync(textChunks.Select(chunk => chunk.Text).ToList(), cancellationToken)
            .ConfigureAwait(false);

        var embeddedChunks = textChunks
            .Zip(embeddings, (textChunk, embedding) =>
            {
                var chunk = DocumentChunk.Create(document.Id, textChunk.Text, textChunk.Position, textChunk.SectionTitle);
                return new EmbeddedChunk(chunk, embedding, _embeddingService.ModelId);
            })
            .ToList();

        var extraction = await _metadataExtractor.ExtractAsync(document.Id, parsed.Text, cancellationToken).ConfigureAwait(false);
        var metadata = extraction.Succeeded ? extraction.Metadata : null;

        await _chunkStore.SaveAsync(document, embeddedChunks, metadata, cancellationToken).ConfigureAwait(false);

        return new IngestResult(document.Id, embeddedChunks.Count, extraction.Succeeded);
    }
}
