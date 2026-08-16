using Almagest.Infrastructure.Metadata;
using Almagest.UnitTests.TestDoubles;
using Microsoft.Extensions.AI;

namespace Almagest.UnitTests.Metadata;

public class ClaudeMetadataExtractorTests
{
    private const string ToolName = "record_document_metadata";

    [Fact]
    public async Task ExtractAsync_ValidToolCall_ReturnsSucceededMetadata()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["title"] = "Q3 Report",
            ["documentType"] = "report",
            ["tags"] = new[] { "finance", "quarterly" },
            ["citedEntities"] = new[] { "Acme Corp" },
        };
        var call = new FunctionCallContent("call-1", ToolName, arguments);
        var chatClient = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        var extractor = new ClaudeMetadataExtractor(chatClient);

        var result = await extractor.ExtractAsync(Guid.NewGuid(), "some document text");

        Assert.True(result.Succeeded);
        Assert.Equal("report", result.Metadata!.DocumentType);
        Assert.Equal("Q3 Report", result.Metadata.ExtractedTitle);
        Assert.Contains("finance", result.Metadata.Tags);
        Assert.Contains("Acme Corp", result.Metadata.CitedEntities);
        Assert.Single(chatClient.Requests); // no retry needed
    }

    [Fact]
    public async Task ExtractAsync_InvalidThenValidOnRetry_Repairs()
    {
        var invalidArguments = new Dictionary<string, object?>
        {
            ["title"] = "X",
            // "documentType" missing -- required by the schema.
            ["tags"] = Array.Empty<string>(),
            ["citedEntities"] = Array.Empty<string>(),
        };
        var validArguments = new Dictionary<string, object?>
        {
            ["title"] = "X",
            ["documentType"] = "note",
            ["tags"] = Array.Empty<string>(),
            ["citedEntities"] = Array.Empty<string>(),
        };

        var firstCall = new FunctionCallContent("call-1", ToolName, invalidArguments);
        var secondCall = new FunctionCallContent("call-2", ToolName, validArguments);
        var chatClient = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [firstCall])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [secondCall])));
        var extractor = new ClaudeMetadataExtractor(chatClient);

        var result = await extractor.ExtractAsync(Guid.NewGuid(), "some document text");

        Assert.True(result.Succeeded);
        Assert.Equal("note", result.Metadata!.DocumentType);
        Assert.Equal(2, chatClient.Requests.Count);
    }

    [Fact]
    public async Task ExtractAsync_InvalidTwice_DegradesWithoutThrowing()
    {
        var invalidArguments = new Dictionary<string, object?>
        {
            ["title"] = "X",
            ["tags"] = Array.Empty<string>(),
            ["citedEntities"] = Array.Empty<string>(),
        };

        var call = new FunctionCallContent("call-1", ToolName, invalidArguments);
        var chatClient = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        var extractor = new ClaudeMetadataExtractor(chatClient);

        var result = await extractor.ExtractAsync(Guid.NewGuid(), "some document text");

        Assert.False(result.Succeeded);
        Assert.Null(result.Metadata);
        Assert.Equal(2, chatClient.Requests.Count);
    }

    [Fact]
    public async Task ExtractAsync_ModelDoesNotCallTheTool_DegradesWithoutThrowing()
    {
        var chatClient = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I refuse.")));
        var extractor = new ClaudeMetadataExtractor(chatClient);

        var result = await extractor.ExtractAsync(Guid.NewGuid(), "some document text");

        Assert.False(result.Succeeded);
        Assert.Null(result.Metadata);
    }
}
