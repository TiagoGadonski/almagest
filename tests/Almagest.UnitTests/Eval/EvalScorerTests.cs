using Almagest.Eval;

namespace Almagest.UnitTests.Eval;

public class EvalScorerTests
{
    private static readonly EvalQuestion Question = new(
        Question: "What is the monthly rent?",
        ExpectedFacts: ["R$ 2.200", "renews in March"],
        ExpectedDocument: "Lease Agreement");

    [Fact]
    public void Score_ExpectedDocumentAmongRetrievedTitles_IsRecallHit()
    {
        var result = EvalScorer.Score(Question, "The rent is R$ 2.200 and it renews in March.", ["Lease Agreement — 2024"]);

        Assert.True(result.RecallHit);
    }

    [Fact]
    public void Score_ExpectedDocumentNotAmongRetrievedTitles_IsRecallMiss()
    {
        var result = EvalScorer.Score(Question, "irrelevant answer", ["Auto Insurance Policy"]);

        Assert.False(result.RecallHit);
    }

    [Fact]
    public void Score_RecallMatch_IsCaseInsensitiveSubstring()
    {
        var result = EvalScorer.Score(Question, "irrelevant answer", ["my lease agreement (signed copy)"]);

        Assert.True(result.RecallHit);
    }

    [Fact]
    public void Score_AnswerContainsAllExpectedFacts_IsAccurate()
    {
        var result = EvalScorer.Score(Question, "The rent is R$ 2.200 and it renews in March each year.", ["Lease Agreement"]);

        Assert.True(result.Accurate);
    }

    [Fact]
    public void Score_AnswerMissingOneExpectedFact_IsNotAccurate()
    {
        var result = EvalScorer.Score(Question, "The rent is R$ 2.200.", ["Lease Agreement"]);

        Assert.False(result.Accurate);
    }

    [Fact]
    public void Score_NoExpectedFacts_IsNeverAccurate()
    {
        var question = Question with { ExpectedFacts = [] };

        var result = EvalScorer.Score(question, "anything at all", ["Lease Agreement"]);

        Assert.False(result.Accurate);
    }

    [Fact]
    public void Score_AccuracyMatch_IsCaseInsensitive()
    {
        var result = EvalScorer.Score(Question, "the RENT is r$ 2.200 and RENEWS IN MARCH", ["Lease Agreement"]);

        Assert.True(result.Accurate);
    }
}
