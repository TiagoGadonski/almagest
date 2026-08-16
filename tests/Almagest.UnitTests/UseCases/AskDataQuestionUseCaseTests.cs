using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.UnitTests.TestDoubles;

namespace Almagest.UnitTests.UseCases;

public class AskDataQuestionUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_HappyPath_FormatsResultIntoAnswer()
    {
        var generator = new FakeSqlGenerator(new SqlGenerationResult(true, "SELECT id FROM contacts"));
        var validator = new FakeSqlValidator(new SqlValidationResult(true, "SELECT id FROM contacts LIMIT 200", null));
        var executor = new FakeSqlExecutor(new QueryResultSet(["id"], [["1"], ["2"]]));
        var chatService = new FakeChatService("There are 2 contacts.");

        var useCase = new AskDataQuestionUseCase(new FakeSchemaProvider(), generator, validator, executor, chatService);

        var result = await useCase.ExecuteAsync("how many contacts do I have?");

        Assert.True(result.Succeeded);
        Assert.Equal("There are 2 contacts.", result.Answer);
        Assert.Equal("SELECT id FROM contacts LIMIT 200", result.Sql);
        Assert.Equal("SELECT id FROM contacts LIMIT 200", executor.LastSql);
        Assert.Contains("how many contacts do I have?", chatService.LastUserPrompt);
    }

    [Fact]
    public async Task ExecuteAsync_GenerationFails_ReturnsFailureWithoutCallingValidatorOrExecutor()
    {
        var generator = new FakeSqlGenerator(new SqlGenerationResult(false, null));
        var validator = new FakeSqlValidator();
        var executor = new FakeSqlExecutor();

        var useCase = new AskDataQuestionUseCase(new FakeSchemaProvider(), generator, validator, executor, new FakeChatService());

        var result = await useCase.ExecuteAsync("nonsense");

        Assert.False(result.Succeeded);
        Assert.Null(result.Sql);
        Assert.Null(validator.LastSql);
        Assert.Null(executor.LastSql);
    }

    [Fact]
    public async Task ExecuteAsync_ValidationFails_ReturnsRejectionReasonWithoutExecuting()
    {
        var generator = new FakeSqlGenerator(new SqlGenerationResult(true, "DROP TABLE contacts"));
        var validator = new FakeSqlValidator(new SqlValidationResult(false, null, "Only SELECT statements are allowed."));
        var executor = new FakeSqlExecutor();

        var useCase = new AskDataQuestionUseCase(new FakeSchemaProvider(), generator, validator, executor, new FakeChatService());

        var result = await useCase.ExecuteAsync("drop everything");

        Assert.False(result.Succeeded);
        Assert.Equal("DROP TABLE contacts", result.Sql);
        Assert.Contains("Only SELECT statements are allowed.", result.Answer);
        Assert.Null(executor.LastSql);
    }
}
