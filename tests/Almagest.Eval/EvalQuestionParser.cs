using System.Text.RegularExpressions;

namespace Almagest.Eval;

// Parses the markdown table in tests/eval/questions.md. Deliberately not a
// general markdown parser -- just enough to read a "| Question | Expected
// Facts | Expected Document |" table, skipping any prose before/after it.
public static partial class EvalQuestionParser
{
    public static IReadOnlyList<EvalQuestion> ParseFile(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<EvalQuestion> Parse(string markdown)
    {
        var tableRows = markdown
            .Split('\n')
            .Select(line => line.TrimEnd('\r').Trim())
            .Where(line => line.StartsWith('|') && line.EndsWith('|'))
            .ToList();

        var dataRows = tableRows
            .Where(row => !SeparatorRow().IsMatch(row))
            .Skip(1) // header row
            .ToList();

        var questions = new List<EvalQuestion>();
        foreach (var row in dataRows)
        {
            var cells = row.Trim('|').Split('|').Select(cell => cell.Trim()).ToList();
            if (cells.Count < 3)
            {
                continue;
            }

            var expectedFacts = cells[1]
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            questions.Add(new EvalQuestion(cells[0], expectedFacts, cells[2]));
        }

        return questions;
    }

    [GeneratedRegex(@"^\|[\s:|-]+\|$")]
    private static partial Regex SeparatorRow();
}
