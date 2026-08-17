using Almagest.Eval;

namespace Almagest.UnitTests.Eval;

public class EvalReportPrinterTests
{
    private static readonly EvalQuestion Question = new(
        Question: "What is the monthly rent?",
        ExpectedFacts: ["R$ 2.200", "renews in March"],
        ExpectedDocument: "Lease Agreement");

    [Fact]
    public void Print_InaccurateResult_ShowsMissingFactsAndTruncatedAnswer()
    {
        var longAnswer = "The rent is R$ 2.200 per month. " + new string('x', 400);
        var result = EvalScorer.Score(Question, longAnswer, ["Lease Agreement"]);
        var report = new EvalReport([result], []);
        var writer = new StringWriter();

        EvalReportPrinter.Print(report, topK: 5, writer);

        var output = writer.ToString();
        Assert.Contains("missing facts: renews in March", output);
        Assert.Contains("answer:", output);
        Assert.Contains("...", output); // truncated, since longAnswer > 300 chars
        Assert.DoesNotContain(new string('x', 400), output); // the full untruncated tail must not appear
    }

    [Fact]
    public void Print_AccurateResult_DoesNotShowMissingFactsOrAnswer()
    {
        var result = EvalScorer.Score(Question, "The rent is R$ 2.200 and it renews in March.", ["Lease Agreement"]);
        var report = new EvalReport([result], []);
        var writer = new StringWriter();

        EvalReportPrinter.Print(report, topK: 5, writer);

        var output = writer.ToString();
        Assert.DoesNotContain("missing facts:", output);
    }

    [Fact]
    public void Print_AnswerWithNewlines_IsFlattenedToASingleLine()
    {
        var result = EvalScorer.Score(Question, "Line one.\nLine two.\r\nLine three.", ["Lease Agreement"]);
        var report = new EvalReport([result], []);
        var writer = new StringWriter();

        EvalReportPrinter.Print(report, topK: 5, writer);

        var answerLine = writer.ToString().Split('\n').Single(line => line.Contains("answer:"));
        Assert.Contains("Line one. Line two. Line three.", answerLine);
    }
}
