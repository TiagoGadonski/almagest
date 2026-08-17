namespace Almagest.Eval;

public sealed record EvalQuestionResult(
    EvalQuestion Question,
    string GeneratedAnswer,
    IReadOnlyList<string> RetrievedDocumentTitles,
    bool RecallHit,
    bool Accurate);

// A question that could not be scored at all (e.g. the provider rate-limited
// it) -- distinct from a scored-but-wrong result. Excluded from
// RecallAt5/Accuracy's denominator, since it was never actually evaluated.
public sealed record EvalQuestionFailure(EvalQuestion Question, string Reason);

public sealed record EvalReport(IReadOnlyList<EvalQuestionResult> Results, IReadOnlyList<EvalQuestionFailure> Failures)
{
    public int Total => Results.Count;
    public double RecallAt5 => Total == 0 ? 0 : Results.Count(r => r.RecallHit) / (double)Total;
    public double Accuracy => Total == 0 ? 0 : Results.Count(r => r.Accurate) / (double)Total;
}

// Both checks are mechanical string matching, on purpose -- see
// docs/phases/05-production.md §3.5. An LLM-judge upgrade is out of scope
// and would need its own individually-approved prompt, same as every other
// prompt in this project.
public static class EvalScorer
{
    public static bool IsRecallHit(IReadOnlyList<string> retrievedDocumentTitles, string expectedDocument) =>
        retrievedDocumentTitles.Any(title => title.Contains(expectedDocument, StringComparison.OrdinalIgnoreCase));

    public static bool IsAccurate(string generatedAnswer, IReadOnlyList<string> expectedFacts) =>
        expectedFacts.Count > 0
        && expectedFacts.All(fact => generatedAnswer.Contains(fact, StringComparison.OrdinalIgnoreCase));

    // Which expected facts specifically weren't found -- IsAccurate alone
    // can't tell you why a question failed, only that it did.
    public static IReadOnlyList<string> MissingFacts(string generatedAnswer, IReadOnlyList<string> expectedFacts) =>
        expectedFacts.Where(fact => !generatedAnswer.Contains(fact, StringComparison.OrdinalIgnoreCase)).ToList();

    public static EvalQuestionResult Score(
        EvalQuestion question, string generatedAnswer, IReadOnlyList<string> retrievedDocumentTitles)
    {
        var recallHit = IsRecallHit(retrievedDocumentTitles, question.ExpectedDocument);
        var accurate = IsAccurate(generatedAnswer, question.ExpectedFacts);
        return new EvalQuestionResult(question, generatedAnswer, retrievedDocumentTitles, recallHit, accurate);
    }
}
