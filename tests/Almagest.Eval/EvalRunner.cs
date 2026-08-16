using Almagest.Application.Ports;
using Almagest.Application.UseCases;

namespace Almagest.Eval;

// Looks up document titles for a set of document ids. A raw read for
// reporting purposes only -- not a new Application port, since nothing
// about retrieval or the agent depends on it.
public delegate Task<IReadOnlyDictionary<Guid, string>> DocumentTitleLookup(
    IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken);

public static class EvalRunner
{
    public static async Task<EvalReport> RunAsync(
        IReadOnlyList<EvalQuestion> questions,
        AskQuestionUseCase askQuestionUseCase,
        IEmbeddingService embeddingService,
        IChunkStore chunkStore,
        DocumentTitleLookup lookupDocumentTitles,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var results = new List<EvalQuestionResult>();

        foreach (var question in questions)
        {
            var queryEmbedding = (await embeddingService.EmbedAsync([question.Question], EmbeddingPurpose.Query, cancellationToken).ConfigureAwait(false))[0];
            var retrieved = await chunkStore.SearchAsync(queryEmbedding, topK, filter: null, cancellationToken).ConfigureAwait(false);

            var documentIds = retrieved.Select(r => r.Chunk.DocumentId).Distinct().ToList();
            var titlesById = await lookupDocumentTitles(documentIds, cancellationToken).ConfigureAwait(false);
            var retrievedTitles = documentIds.Select(id => titlesById.GetValueOrDefault(id, string.Empty)).ToList();

            var answer = await askQuestionUseCase.ExecuteAsync(question.Question, cancellationToken).ConfigureAwait(false);

            results.Add(EvalScorer.Score(question, answer.Answer, retrievedTitles));
        }

        return new EvalReport(results);
    }
}
