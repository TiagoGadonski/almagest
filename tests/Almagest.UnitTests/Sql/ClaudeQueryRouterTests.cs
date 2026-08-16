using Almagest.Application.Ports;
using Almagest.Infrastructure.Sql;
using Almagest.UnitTests.TestDoubles;
using Microsoft.Extensions.AI;

namespace Almagest.UnitTests.Sql;

public class ClaudeQueryRouterTests
{
    private const string ToolName = "classify_question_route";

    [Fact]
    public async Task RouteAsync_ModelChoosesSql_ReturnsSql()
    {
        var call = new FunctionCallContent("call-1", ToolName, new Dictionary<string, object?> { ["route"] = "sql" });
        var chatClient = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        var router = new ClaudeQueryRouter(chatClient);

        var route = await router.RouteAsync("how many tasks are open?");

        Assert.Equal(QueryRoute.Sql, route);
    }

    [Fact]
    public async Task RouteAsync_ModelChoosesRag_ReturnsRag()
    {
        var call = new FunctionCallContent("call-1", ToolName, new Dictionary<string, object?> { ["route"] = "rag" });
        var chatClient = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        var router = new ClaudeQueryRouter(chatClient);

        var route = await router.RouteAsync("what does my lease say about pets?");

        Assert.Equal(QueryRoute.Rag, route);
    }

    [Fact]
    public async Task RouteAsync_ModelDoesNotCallTool_DefaultsToRag()
    {
        var chatClient = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I don't know.")));
        var router = new ClaudeQueryRouter(chatClient);

        var route = await router.RouteAsync("ambiguous question");

        Assert.Equal(QueryRoute.Rag, route);
    }
}
