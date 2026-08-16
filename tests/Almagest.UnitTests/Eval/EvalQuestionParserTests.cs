using Almagest.Eval;

namespace Almagest.UnitTests.Eval;

public class EvalQuestionParserTests
{
    [Fact]
    public void Parse_TableWithSurroundingProse_ExtractsOnlyDataRows()
    {
        const string markdown = """
            # Evaluation questions

            Some explanatory prose before the table, including a stray
            `|` character that must not be mistaken for a table row.

            | Question | Expected Facts | Expected Document |
            |---|---|---|
            | What is the rent? | R$ 2.200; renews in March | Lease Agreement |
            | What is the deductible? | R$ 1.500 | Insurance Policy |

            Trailing prose after the table.
            """;

        var questions = EvalQuestionParser.Parse(markdown);

        Assert.Equal(2, questions.Count);
        Assert.Equal("What is the rent?", questions[0].Question);
        Assert.Equal(["R$ 2.200", "renews in March"], questions[0].ExpectedFacts);
        Assert.Equal("Lease Agreement", questions[0].ExpectedDocument);
        Assert.Equal("What is the deductible?", questions[1].Question);
        Assert.Equal(["R$ 1.500"], questions[1].ExpectedFacts);
        Assert.Equal("Insurance Policy", questions[1].ExpectedDocument);
    }

    [Fact]
    public void Parse_NoTable_ReturnsEmpty()
    {
        var questions = EvalQuestionParser.Parse("Just a paragraph of text, no table at all.");

        Assert.Empty(questions);
    }

    [Fact]
    public void Parse_SingleExpectedFact_DoesNotSplitOnAnythingButSemicolon()
    {
        const string markdown = """
            | Question | Expected Facts | Expected Document |
            |---|---|---|
            | What warranty applies? | 2-year warranty, extendable | Receipt |
            """;

        var questions = EvalQuestionParser.Parse(markdown);

        Assert.Equal(["2-year warranty, extendable"], questions[0].ExpectedFacts);
    }
}
