using System.Text;
using System.Text.Json;
using Almagest.Application.Ports;
using Microsoft.Extensions.AI;

namespace Almagest.Infrastructure.Sql;

// Depends on IChatClient directly, same reasoning as ClaudeMetadataExtractor
// -- forced tool calling needs ChatOptions.Tools/ToolMode/FunctionCallContent,
// which the narrow IChatService Application port doesn't expose.
//
// No JsonSchema.Net validation here (unlike ClaudeMetadataExtractor): the
// output schema is a single "sql" string, and the validation that actually
// matters -- is this a safe SELECT? -- is PgAstSqlValidator's job, called
// separately by the use case. "Structured output" here is a reliability
// improvement, not a security control (phase doc 3.2).
public sealed class ClaudeSqlGenerator : ISqlGenerator
{
    private const string ToolName = "generate_sql_query";

    private const string SystemPromptTemplate = """
        You translate a natural-language question about the user's personal
        data into a single read-only PostgreSQL SELECT query. Call the
        {0} tool exactly once with that query.

        Rules:
        - SELECT only. Never write INSERT, UPDATE, DELETE, or any DDL.
        - Use only the tables and columns listed in the schema below -- do
          not reference anything else, even if it seems like it should exist.
        - No comments in the query.
        - Prefer a single statement with an explicit LIMIT.
        - If the question cannot be answered from the schema below, generate
          `SELECT NULL WHERE FALSE` rather than guessing at a table or
          column that isn't listed.

        Schema:
        {1}
        """;

    private readonly IChatClient _chatClient;

    public ClaudeSqlGenerator(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<SqlGenerationResult> GenerateAsync(
        string question, SchemaDescription schema, CancellationToken cancellationToken = default)
    {
        var systemPrompt = string.Format(SystemPromptTemplate, ToolName, DescribeSchema(schema));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, question),
        };

        var options = new ChatOptions
        {
            Tools = [new GenerateSqlTool()],
            ToolMode = ChatToolMode.RequireSpecific(ToolName),
        };

        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var call = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(content => content.Name == ToolName);

        if (call?.Arguments is null || !call.Arguments.TryGetValue("sql", out var sqlValue))
        {
            return new SqlGenerationResult(false, null);
        }

        var sql = sqlValue is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : sqlValue?.ToString();

        return string.IsNullOrWhiteSpace(sql) ? new SqlGenerationResult(false, null) : new SqlGenerationResult(true, sql);
    }

    private static string DescribeSchema(SchemaDescription schema)
    {
        var builder = new StringBuilder();

        foreach (var table in schema.Tables)
        {
            builder.Append(table.Name).Append(": ");
            builder.AppendLine(string.Join(", ", table.Columns.Select(column => $"{column.Name} ({column.DataType})")));
        }

        return builder.ToString();
    }

    private sealed class GenerateSqlTool : AIFunction
    {
        public override string Name => ToolName;

        public override string Description => "Records the generated read-only SQL SELECT query.";

        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "sql": { "type": "string" }
              },
              "required": ["sql"],
              "additionalProperties": false
            }
            """).RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Arguments are read directly from the tool call and never invoked as a real function.");
    }
}
