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
        TimeSpan delayBetweenQuestions,
        Action<string>? logProgress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        logProgress ??= _ => { };
        delay ??= Task.Delay;

        var results = new List<EvalQuestionResult>();
        var failures = new List<EvalQuestionFailure>();

        for (var i = 0; i < questions.Count; i++)
        {
            var question = questions[i];
            logProgress($"question {i + 1} of {questions.Count}: {question.Question}");

            try
            {
                // Embedded once, reused for both calls below -- this used to
                // be two identical embedding requests per question (one here,
                // one again inside AskQuestionUseCase.ExecuteAsync), which on
                // Voyage's free tier doubled the cost of every question and
                // was the actual cause of runs failing mid-way on rate limits
                // in an alternating pattern.
                var queryEmbedding = (await embeddingService
                    .EmbedAsync([question.Question], EmbeddingPurpose.Query, cancellationToken)
                    .ConfigureAwait(false))[0];
                var retrieved = await chunkStore.SearchAsync(queryEmbedding, topK, filter: null, cancellationToken).ConfigureAwait(false);

                var documentIds = retrieved.Select(r => r.Chunk.DocumentId).Distinct().ToList();
                var titlesById = await lookupDocumentTitles(documentIds, cancellationToken).ConfigureAwait(false);
                var retrievedTitles = documentIds.Select(id => titlesById.GetValueOrDefault(id, string.Empty)).ToList();

                var answer = await askQuestionUseCase.ExecuteAsync(question.Question, queryEmbedding, cancellationToken).ConfigureAwait(false);

                results.Add(EvalScorer.Score(question, answer.Answer, retrievedTitles));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single question failing (typically a free-tier rate
                // limit -- see tests/eval/questions.md) is not a reason to
                // lose every question after it. A run that aborts on
                // question 3 of 14 reports nothing usable; one that skips
                // question 3 and keeps going reports 13 real results plus
                // one named gap.
                logProgress($"  failed: {ex.GetType().Name}: {ex.Message}");
                failures.Add(new EvalQuestionFailure(question, $"{ex.GetType().Name}: {ex.Message}"));
            }

            var isLastQuestion = i == questions.Count - 1;
            if (!isLastQuestion && delayBetweenQuestions > TimeSpan.Zero)
            {
                await delay(delayBetweenQuestions, cancellationToken).ConfigureAwait(false);
            }
        }

        return new EvalReport(results, failures);
    }
}
