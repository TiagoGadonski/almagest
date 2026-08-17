namespace Almagest.Eval;

public static class EvalReportPrinter
{
    public static void Print(EvalReport report, int topK, TextWriter? writer = null)
    {
        writer ??= Console.Out;

        foreach (var result in report.Results)
        {
            var recallMark = result.RecallHit ? "HIT " : "MISS";
            var accuracyMark = result.Accurate ? "HIT " : "MISS";
            writer.WriteLine($"[recall {recallMark}] [accuracy {accuracyMark}] {result.Question.Question}");

            if (!result.Accurate)
            {
                var missing = EvalScorer.MissingFacts(result.GeneratedAnswer, result.Question.ExpectedFacts);
                writer.WriteLine($"    missing facts: {string.Join("; ", missing)}");
                writer.WriteLine($"    answer: {Truncate(result.GeneratedAnswer, 300)}");
            }
        }

        writer.WriteLine();
        writer.WriteLine($"recall@{topK}: {report.RecallAt5:P0} ({report.Results.Count(r => r.RecallHit)}/{report.Total})");
        writer.WriteLine($"accuracy:  {report.Accuracy:P0} ({report.Results.Count(r => r.Accurate)}/{report.Total})");

        if (report.Failures.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"{report.Failures.Count} question(s) could not be evaluated (excluded from recall/accuracy above):");
            foreach (var failure in report.Failures)
            {
                writer.WriteLine($"  - {failure.Question.Question} -- {failure.Reason}");
            }
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        var singleLine = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "...";
    }
}
