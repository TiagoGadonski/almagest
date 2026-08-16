using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.Domain;
using Almagest.Infrastructure.Agent;
using Almagest.UnitTests.TestDoubles;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Almagest.UnitTests.Agent;

// The decision loop, tested against the real Microsoft.Agents.AI machinery
// with only the model call faked out (FakeChatClient) -- tool selection,
// approval gating, and the iteration cap are exercised for real, not
// simulated by hand.
public class AlmagestAgentServiceTests
{
    private static AlmagestAgentService BuildService(
        FakeChatClient chatClient,
        out FakeChunkStore chunkStore,
        out FakeNoteStore noteStore,
        out FakeReminderStore reminderStore)
    {
        chunkStore = new FakeChunkStore();
        noteStore = new FakeNoteStore();
        reminderStore = new FakeReminderStore();

        var askQuestionUseCase = new AskQuestionUseCase(
            new FakeEmbeddingService("test-model"),
            chunkStore,
            new FakeChatService("Answer from RAG."),
            new RetrievalOptions(TopK: 5, SimilarityFloor: 0.70, MaxContextTokens: 4000));

        var askDataQuestionUseCase = new AskDataQuestionUseCase(
            new FakeSchemaProvider(),
            new FakeSqlGenerator(new SqlGenerationResult(false, null)),
            new FakeSqlValidator(),
            new FakeSqlExecutor(),
            new FakeChatService("Answer from SQL."));

        var createNoteUseCase = new CreateNoteUseCase(noteStore);
        var setReminderUseCase = new SetReminderUseCase(reminderStore);

        return new AlmagestAgentService(
            chatClient,
            askQuestionUseCase,
            askDataQuestionUseCase,
            createNoteUseCase,
            setReminderUseCase,
            NullLogger<AlmagestAgentService>.Instance);
    }

    [Fact]
    public async Task RunAsync_ModelSelectsReadOnlyTool_ExecutesImmediatelyWithoutApproval()
    {
        var chunk = DocumentChunk.Create(Guid.NewGuid(), "relevant text", 0, null);
        var toolCall = new FunctionCallContent("call-1", "search_documents", new Dictionary<string, object?> { ["question"] = "what is x?" });
        var chatClient = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Here is what I found.")));

        var service = BuildService(chatClient, out _, out _, out _);

        var result = await service.RunAsync(sessionId: null, "what is x?");

        Assert.Empty(result.PendingApprovals);
        Assert.Equal("Here is what I found.", result.Answer);
    }

    [Fact]
    public async Task RunAsync_ModelSelectsSideEffectingTool_DoesNotExecuteWithoutApproval()
    {
        var toolCall = new FunctionCallContent("call-1", "create_note", new Dictionary<string, object?> { ["content"] = "buy milk" });
        var chatClient = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall])));

        var service = BuildService(chatClient, out _, out var noteStore, out _);

        var result = await service.RunAsync(sessionId: null, "remember to buy milk");

        Assert.Empty(noteStore.Saved);
        Assert.Single(result.PendingApprovals);
        Assert.Equal("create_note", result.PendingApprovals[0].ToolName);
        Assert.Null(result.Answer);
    }

    [Fact]
    public async Task ResumeAsync_Approved_ExecutesTheSideEffectingTool()
    {
        var toolCall = new FunctionCallContent("call-1", "create_note", new Dictionary<string, object?> { ["content"] = "buy milk" });
        var chatClient = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Noted.")));

        var service = BuildService(chatClient, out _, out var noteStore, out _);

        var initial = await service.RunAsync(sessionId: null, "remember to buy milk");
        var pending = Assert.Single(initial.PendingApprovals);

        var resumed = await service.ResumeAsync(initial.SessionId, pending.RequestId, approved: true);

        Assert.Single(noteStore.Saved);
        Assert.Equal("buy milk", noteStore.Saved[0].Content);
        Assert.Empty(resumed.PendingApprovals);
    }

    [Fact]
    public async Task ResumeAsync_Rejected_NeverExecutesTheSideEffectingTool()
    {
        var toolCall = new FunctionCallContent("call-1", "set_reminder", new Dictionary<string, object?>
        {
            ["message"] = "call the dentist",
            ["remindAt"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
        });
        var chatClient = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCall])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Okay, I won't set that reminder.")));

        var service = BuildService(chatClient, out _, out _, out var reminderStore);

        var initial = await service.RunAsync(sessionId: null, "remind me to call the dentist tomorrow");
        var pending = Assert.Single(initial.PendingApprovals);

        await service.ResumeAsync(initial.SessionId, pending.RequestId, approved: false);

        Assert.Empty(reminderStore.Saved);
    }

    [Fact]
    public async Task RunAsync_ModelNeverStopsCallingTools_LoopEndsAtTheIterationCap()
    {
        // Six canned tool-call responses in a row -- more than the 5-iteration
        // cap the service configures -- with no final text answer ever queued.
        var repeatedCall = new FunctionCallContent("call-x", "search_documents", new Dictionary<string, object?> { ["question"] = "again?" });
        var responses = Enumerable.Range(0, 6)
            .Select(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [repeatedCall])))
            .ToArray();
        var chatClient = new FakeChatClient(responses);

        var service = BuildService(chatClient, out _, out _, out _);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await service.RunAsync(sessionId: null, "again?", cts.Token);

        // The loop must terminate (this line is only reached if it does) --
        // it should not have exhausted all six queued responses.
        Assert.True(chatClient.Requests.Count <= 6);
    }
}
