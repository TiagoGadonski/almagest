using Almagest.Eval;

namespace Almagest.UnitTests.Eval;

// Guards the real tests/eval/questions.md against accidental format
// drift -- structurally verifiable even though the harness itself can't
// run end to end here (no ingested corpus, no API credentials).
public class QuestionsFileTests
{
    [Fact]
    public void RealQuestionsFile_ParsesIntoWellFormedQuestions()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval", "questions.md"));

        var questions = EvalQuestionParser.ParseFile(path);

        Assert.NotEmpty(questions);
        Assert.All(questions, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Question));
            Assert.NotEmpty(q.ExpectedFacts);
            Assert.False(string.IsNullOrWhiteSpace(q.ExpectedDocument));
        });
    }
}
