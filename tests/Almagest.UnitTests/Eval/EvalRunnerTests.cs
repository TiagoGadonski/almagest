using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.Eval;
using Almagest.UnitTests.TestDoubles;

namespace Almagest.UnitTests.Eval;

public class EvalRunnerTests
{
    private static readonly RetrievalOptions Options = new(TopK: 5, SimilarityFloor: 0.0, MaxContextTokens: 4000);

    private static readonly DocumentTitleLookup NoTitles = (_, _) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    [Fact]
    public async Task RunAsync_LogsProgressForEveryQuestion()
    {
        var questions = MakeQuestions("first", "second", "third");
        var embeddingService = new FakeEmbeddingService("test-model");
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);
        var logged = new List<string>();

        await EvalRunner.RunAsync(
            questions, useCase, embeddingService, new FakeChunkStore(), NoTitles, topK: 5,
            delayBetweenQuestions: TimeSpan.Zero, logProgress: logged.Add, delay: NoOpDelay);

        Assert.Contains(logged, line => line.Contains("question 1 of 3"));
        Assert.Contains(logged, line => line.Contains("question 2 of 3"));
        Assert.Contains(logged, line => line.Contains("question 3 of 3"));
    }

    [Fact]
    public async Task RunAsync_WaitsBetweenQuestionsButNotAfterTheLastOne()
    {
        var questions = MakeQuestions("first", "second", "third");
        var embeddingService = new FakeEmbeddingService("test-model");
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);
        var delays = new List<TimeSpan>();

        await EvalRunner.RunAsync(
            questions, useCase, embeddingService, new FakeChunkStore(), NoTitles, topK: 5,
            delayBetweenQuestions: TimeSpan.FromSeconds(25),
            delay: (span, _) => { delays.Add(span); return Task.CompletedTask; });

        // 3 questions -> 2 gaps between them, never a wait after the last one.
        Assert.Equal([TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25)], delays);
    }

    [Fact]
    public async Task RunAsync_ZeroDelay_NeverCallsDelay()
    {
        var questions = MakeQuestions("first", "second");
        var embeddingService = new FakeEmbeddingService("test-model");
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);
        var delayCalls = 0;

        await EvalRunner.RunAsync(
            questions, useCase, embeddingService, new FakeChunkStore(), NoTitles, topK: 5,
            delayBetweenQuestions: TimeSpan.Zero,
            delay: (_, _) => { delayCalls++; return Task.CompletedTask; });

        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public async Task RunAsync_OneQuestionRateLimited_SkipsItAndScoresTheRest()
    {
        var questions = MakeQuestions("good one", "rate limited one", "good two");
        var embeddingService = new FakeEmbeddingService("test-model", texts =>
            texts[0] == "rate limited one"
                ? throw new EmbeddingProviderException(EmbeddingProviderErrorKind.RateLimited, "Voyage AI rate limit exceeded after 5 attempt(s).")
                : [new float[] { 1f }]);
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);

        var report = await EvalRunner.RunAsync(
            questions, useCase, embeddingService, new FakeChunkStore(), NoTitles, topK: 5,
            delayBetweenQuestions: TimeSpan.Zero, delay: NoOpDelay);

        Assert.Equal(2, report.Total); // the two that succeeded
        Assert.Single(report.Failures);
        Assert.Equal("rate limited one", report.Failures[0].Question.Question);
        Assert.Contains("rate limit", report.Failures[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_EveryQuestionFails_ReturnsAllAsFailuresNotAnException()
    {
        var questions = MakeQuestions("a", "b");
        var embeddingService = new FakeEmbeddingService("test-model", _ =>
            throw new EmbeddingProviderException(EmbeddingProviderErrorKind.InvalidCredentials, "bad key"));
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);

        var report = await EvalRunner.RunAsync(
            questions, useCase, embeddingService, new FakeChunkStore(), NoTitles, topK: 5,
            delayBetweenQuestions: TimeSpan.Zero, delay: NoOpDelay);

        Assert.Equal(0, report.Total);
        Assert.Equal(2, report.Failures.Count);
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_PropagatesRatherThanBeingRecordedAsAFailure()
    {
        var questions = MakeQuestions("a");
        var embeddingService = new FakeEmbeddingService("test-model", _ => throw new OperationCanceledException());
        var useCase = new AskQuestionUseCase(embeddingService, new FakeChunkStore(), new FakeChatService(), Options);

        await Assert.ThrowsAsync<OperationCanceledException>(() => EvalRunner.RunAsync(
            questions, useCase, embeddingService, new FakeChunkStore(), NoTitles, topK: 5,
            delayBetweenQuestions: TimeSpan.Zero, delay: NoOpDelay));
    }

    private static Task NoOpDelay(TimeSpan span, CancellationToken cancellationToken) => Task.CompletedTask;

    private static IReadOnlyList<EvalQuestion> MakeQuestions(params string[] questionTexts) =>
        questionTexts.Select(text => new EvalQuestion(text, ["some fact"], "Some Document")).ToList();
}
