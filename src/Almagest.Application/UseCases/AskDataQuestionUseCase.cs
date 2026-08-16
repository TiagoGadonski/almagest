using System.Text;
using Almagest.Application.Ports;

namespace Almagest.Application.UseCases;

public sealed record DataAskResult(string Answer, bool Succeeded, string? Sql);

public sealed class AskDataQuestionUseCase
{
    private const string FormattingSystemPrompt = """
        You turn a SQL query result into a clear, natural-language answer to
        the user's original question. Use only the rows provided -- never
        add information not present in them. If the result set is empty,
        say plainly that nothing matched, don't speculate why.
        """;

    private readonly ISchemaProvider _schemaProvider;
    private readonly ISqlGenerator _sqlGenerator;
    private readonly ISqlValidator _sqlValidator;
    private readonly ISqlExecutor _sqlExecutor;
    private readonly IChatService _chatService;

    public AskDataQuestionUseCase(
        ISchemaProvider schemaProvider,
        ISqlGenerator sqlGenerator,
        ISqlValidator sqlValidator,
        ISqlExecutor sqlExecutor,
        IChatService chatService)
    {
        _schemaProvider = schemaProvider;
        _sqlGenerator = sqlGenerator;
        _sqlValidator = sqlValidator;
        _sqlExecutor = sqlExecutor;
        _chatService = chatService;
    }

    public async Task<DataAskResult> ExecuteAsync(string question, CancellationToken cancellationToken = default)
    {
        var schema = await _schemaProvider.GetSchemaAsync(cancellationToken).ConfigureAwait(false);

        var generation = await _sqlGenerator.GenerateAsync(question, schema, cancellationToken).ConfigureAwait(false);
        if (!generation.Succeeded || generation.Sql is null)
        {
            return new DataAskResult("I couldn't turn that into a query over your data.", false, null);
        }

        var validation = _sqlValidator.Validate(generation.Sql);
        if (!validation.IsValid || validation.FinalizedSql is null)
        {
            return new DataAskResult($"I couldn't safely run that query ({validation.RejectionReason}).", false, generation.Sql);
        }

        var results = await _sqlExecutor.ExecuteAsync(validation.FinalizedSql, cancellationToken).ConfigureAwait(false);

        var userPrompt = BuildFormattingPrompt(question, results);
        var answer = await _chatService.CompleteAsync(FormattingSystemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);

        return new DataAskResult(answer, true, validation.FinalizedSql);
    }

    private static string BuildFormattingPrompt(string question, QueryResultSet results)
    {
        var builder = new StringBuilder();
        builder.Append("Question: ").AppendLine(question);
        builder.AppendLine();
        builder.AppendLine("Columns: " + string.Join(", ", results.ColumnNames));
        builder.AppendLine($"Rows ({results.Rows.Count}):");

        foreach (var row in results.Rows)
        {
            builder.AppendLine(string.Join(" | ", row.Select(value => value?.ToString() ?? "NULL")));
        }

        return builder.ToString();
    }
}
