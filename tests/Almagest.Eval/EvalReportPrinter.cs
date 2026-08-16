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
        }

        writer.WriteLine();
        writer.WriteLine($"recall@{topK}: {report.RecallAt5:P0} ({report.Results.Count(r => r.RecallHit)}/{report.Total})");
        writer.WriteLine($"accuracy:  {report.Accuracy:P0} ({report.Results.Count(r => r.Accurate)}/{report.Total})");
    }
}
