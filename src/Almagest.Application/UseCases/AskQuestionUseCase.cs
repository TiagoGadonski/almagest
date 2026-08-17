using System.Text;
using Almagest.Application.Ports;

namespace Almagest.Application.UseCases;

public sealed record RetrievalOptions(int TopK, double SimilarityFloor, int MaxContextTokens);

public sealed record Citation(Guid ChunkId, Guid DocumentId, string? SectionTitle, double Similarity);

public sealed record AskResult(string Answer, bool Found, IReadOnlyList<Citation> Citations);

public sealed class AskQuestionUseCase
{
    private const string GroundingSystemPrompt = """
        You are a precise research assistant. Answer only using the excerpts
        provided below -- never draw on outside/parametric knowledge. Every
        factual claim must cite the chunk id it came from, formatted like
        [chunk:<id>]. If the excerpts do not contain enough information to
        answer, say so explicitly instead of guessing.
        """;

    private const int CharsPerTokenEstimate = 4; // no tokenizer dependency yet -- rough stand-in

    private readonly IEmbeddingService _embeddingService;
    private readonly IChunkStore _chunkStore;
    private readonly IChatService _chatService;
    private readonly RetrievalOptions _options;

    public AskQuestionUseCase(
        IEmbeddingService embeddingService, IChunkStore chunkStore, IChatService chatService, RetrievalOptions options)
    {
        _embeddingService = embeddingService;
        _chunkStore = chunkStore;
        _chatService = chatService;
        _options = options;
    }

    // queryEmbedding: precomputed vector for this question, if the caller
    // already has one (Almagest.Eval computes it once and reuses it for
    // both recall@5's raw SearchAsync and this call, instead of paying for
    // two identical embedding requests per question -- see
    // tests/eval/questions.md). Null (the default, every non-eval caller)
    // embeds internally exactly as before.
    public async Task<AskResult> ExecuteAsync(
        string question, float[]? queryEmbedding = null, CancellationToken cancellationToken = default)
    {
        queryEmbedding ??= (await _embeddingService.EmbedAsync([question], EmbeddingPurpose.Query, cancellationToken).ConfigureAwait(false))[0];

        var candidates = await _chunkStore.SearchAsync(queryEmbedding, _options.TopK, filter: null, cancellationToken).ConfigureAwait(false);

        // A chunk embedded with a different model lives in a different vector
        // space -- its similarity score to this query is meaningless, not just
        // "low". Excluded outright rather than merely down-ranked.
        var comparable = candidates.Where(c => c.EmbeddingModelId == _embeddingService.ModelId);
        var relevant = comparable.Where(c => c.Similarity >= _options.SimilarityFloor).ToList();

        if (relevant.Count == 0)
        {
            return new AskResult(
                "I couldn't find anything in your documents that answers this question.",
                Found: false,
                Citations: []);
        }

        var context = BuildContext(relevant);
        var userPrompt = $"Question: {question}\n\nExcerpts:\n{context}";

        var answer = await _chatService.CompleteAsync(GroundingSystemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);

        var citations = relevant
            .Select(c => new Citation(c.Chunk.Id, c.Chunk.DocumentId, c.Chunk.SectionTitle, c.Similarity))
            .ToList();

        return new AskResult(answer, Found: true, citations);
    }

    private string BuildContext(IReadOnlyList<ScoredChunk> chunks)
    {
        var remaining = _options.MaxContextTokens * CharsPerTokenEstimate;
        var builder = new StringBuilder();

        foreach (var scored in chunks.OrderByDescending(c => c.Similarity))
        {
            var section = scored.Chunk.SectionTitle is { } title ? $", section \"{title}\"" : string.Empty;
            var entry = $"[chunk:{scored.Chunk.Id}] (similarity {scored.Similarity:F2}{section})\n{scored.Chunk.Text}\n\n";

            if (entry.Length > remaining && builder.Length > 0)
            {
                break;
            }

            builder.Append(entry);
            remaining -= entry.Length;
        }

        return builder.ToString();
    }
}
