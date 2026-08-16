using System.Text.Json;
using Almagest.Application.Ports;
using Almagest.Domain;
using Json.Schema;
using Microsoft.Extensions.AI;

namespace Almagest.Infrastructure.Metadata;

// Depends on IChatClient directly, not the narrow IChatService Application
// port -- forced tool calling needs ChatOptions.Tools/ToolMode and
// FunctionCallContent, which IChatService deliberately doesn't expose.
public sealed class ClaudeMetadataExtractor : IMetadataExtractor
{
    private const int MaxDocumentChars = 12_000; // ~3k tokens; title/type/tags/dates are usually inferable from the start
    private const int MaxAttempts = 2; // one shot + one repair retry

    private const string SystemPrompt = """
        You extract structured metadata from documents. Call the record_document_metadata
        tool exactly once with your best assessment. Leave a field empty (empty string,
        empty array, or omit optional date fields) rather than guessing when the document
        doesn't make something clear. Dates must be ISO 8601 (YYYY-MM-DD).
        """;

    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "documentType": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" } },
            "citedEntities": { "type": "array", "items": { "type": "string" } },
            "dateRangeStart": { "type": "string", "format": "date" },
            "dateRangeEnd": { "type": "string", "format": "date" }
          },
          "required": ["title", "documentType", "tags", "citedEntities"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonSchema Schema = JsonSchema.FromText(SchemaJson);

    private readonly IChatClient _chatClient;

    public ClaudeMetadataExtractor(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<MetadataExtractionResult> ExtractAsync(
        Guid documentId, string documentText, CancellationToken cancellationToken = default)
    {
        var excerpt = documentText.Length > MaxDocumentChars ? documentText[..MaxDocumentChars] : documentText;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, excerpt),
        };

        var options = new ChatOptions
        {
            Tools = [new MetadataExtractionTool()],
            ToolMode = ChatToolMode.RequireSpecific(MetadataExtractionTool.ToolName),
        };

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ChatResponse response;
            try
            {
                response = await _chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Metadata is an enrichment, not a precondition for a
                // document's chunks to exist and be searchable (see
                // docs/phases/02-memory.md §3.4) -- a transient provider
                // failure (timeout, 5xx, rate limit) degrades the same way a
                // model refusal or repeated schema-validation failure
                // already does below, instead of propagating out of
                // IngestDocumentUseCase and losing chunks that were already
                // successfully parsed, chunked, and embedded.
                return new MetadataExtractionResult(false, null);
            }

            var call = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .FirstOrDefault(content => content.Name == MetadataExtractionTool.ToolName);

            if (call is null)
            {
                // Model ignored/refused the forced tool -- degrade, don't guess.
                return new MetadataExtractionResult(false, null);
            }

            var element = JsonDocument.Parse(JsonSerializer.Serialize(call.Arguments)).RootElement;
            var evaluation = Schema.Evaluate(element);

            if (evaluation.IsValid)
            {
                return new MetadataExtractionResult(true, ToDocumentMetadata(documentId, element));
            }

            if (attempt < MaxAttempts)
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, [call]));
                messages.Add(new ChatMessage(ChatRole.User,
                    $"The previous call to {MetadataExtractionTool.ToolName} failed schema validation: " +
                    $"{DescribeErrors(evaluation)}. Call it again with corrected arguments."));
            }
        }

        // Repair retry also failed -- metadata is an enrichment, not a
        // precondition for the document's chunks to exist. Degrade, don't throw.
        return new MetadataExtractionResult(false, null);
    }

    private static string DescribeErrors(EvaluationResults evaluation)
    {
        var messages = (evaluation.Details ?? [])
            .Where(detail => !detail.IsValid && detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"));

        var joined = string.Join("; ", messages);
        return string.IsNullOrEmpty(joined) ? "schema validation failed" : joined;
    }

    private static DocumentMetadata ToDocumentMetadata(Guid documentId, JsonElement element)
    {
        var tags = ReadStringArray(element, "tags");
        var citedEntities = ReadStringArray(element, "citedEntities");
        var documentType = AsNonEmpty(ReadString(element, "documentType"));
        var extractedTitle = AsNonEmpty(ReadString(element, "title"));
        var dateStart = ParseDate(ReadString(element, "dateRangeStart"));
        var dateEnd = ParseDate(ReadString(element, "dateRangeEnd"));

        return DocumentMetadata.Create(documentId, documentType, dateStart, dateEnd, tags, citedEntities, extractedTitle);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static string? AsNonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateOnly? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DateOnly.TryParse(value, out var date) ? date : null;

    private sealed class MetadataExtractionTool : AIFunction
    {
        public const string ToolName = "record_document_metadata";

        public override string Name => ToolName;

        public override string Description =>
            "Records structured metadata extracted from a document: title, type, tags, date range, and cited entities.";

        public override JsonElement JsonSchema { get; } = JsonDocument.Parse(SchemaJson).RootElement;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Arguments are read directly from the model's tool call and never invoked as a real function.");
    }
}
