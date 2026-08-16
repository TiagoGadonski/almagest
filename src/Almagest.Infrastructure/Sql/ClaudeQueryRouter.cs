using System.Text.Json;
using Almagest.Application.Ports;
using Microsoft.Extensions.AI;

namespace Almagest.Infrastructure.Sql;

// Same IChatClient-direct pattern as ClaudeSqlGenerator/ClaudeMetadataExtractor
// -- forced tool calling for a reliable classification, not free-text
// parsing of "rag" or "sql" out of a prose response.
public sealed class ClaudeQueryRouter : IQueryRouter
{
    private const string ToolName = "classify_question_route";

    private const string SystemPrompt = """
        Classify how to answer the user's question about their personal data.

        Choose "sql" when the question asks for counts, aggregates, filters,
        or lookups over structured records -- tasks, projects, contacts,
        calendar events, or document metadata (type, tags, dates). Example:
        "how many open tasks are overdue", "list contacts I haven't emailed
        this month", "what documents are tagged legal".

        Choose "rag" when the question asks about the *content* or meaning
        of documents -- what something says, means, or explains. Example:
        "what does my lease say about pet deposits", "summarize the contract
        with Acme Corp".

        Call the classify_question_route tool exactly once with your choice.
        """;

    private readonly IChatClient _chatClient;

    public ClaudeQueryRouter(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<QueryRoute> RouteAsync(string question, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt), new(ChatRole.User, question) };
        var options = new ChatOptions { Tools = [new ClassifyRouteTool()], ToolMode = ChatToolMode.RequireSpecific(ToolName) };

        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var call = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(content => content.Name == ToolName);

        var route = call?.Arguments is not null && call.Arguments.TryGetValue("route", out var value)
            ? (value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value?.ToString())
            : null;

        // Default to RAG on any ambiguity/failure -- RAG already has its own
        // "not found" fallback (Phase 1), so an uncertain classification
        // degrades to the safer path instead of guessing "sql".
        return string.Equals(route, "sql", StringComparison.OrdinalIgnoreCase) ? QueryRoute.Sql : QueryRoute.Rag;
    }

    private sealed class ClassifyRouteTool : AIFunction
    {
        public override string Name => ToolName;

        public override string Description => "Records which pipeline should answer the question.";

        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "route": { "type": "string", "enum": ["rag", "sql"] }
              },
              "required": ["route"],
              "additionalProperties": false
            }
            """).RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Arguments are read directly from the tool call and never invoked as a real function.");
    }
}
