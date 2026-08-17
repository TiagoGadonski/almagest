using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Almagest.Infrastructure.Agent;

// Wraps Microsoft.Agents.AI's ChatClientAgent so Application/Api never
// reference its concrete types -- same boundary discipline as every other
// AI-backed port in this project.
public sealed class AlmagestAgentService : IAgentService
{
    private const int MaxIterations = 5;
    private const int MaxConsecutiveErrors = 3;
    private const int MaxRetryAttempts = 3;

    private const string SystemInstructions = """
        You are a personal assistant with four tools: search_documents (RAG
        over the user's ingested documents), query_personal_data (SQL over
        tasks, projects, contacts, calendar, document metadata),
        create_note, and set_reminder. Use search_documents for questions
        about what a document says or means. Use query_personal_data for
        counts, filters, or lookups over structured records. Only call
        create_note or set_reminder when the user actually asks you to save
        or remember something -- never as a side effect of answering a
        question. Explain briefly why you chose a tool before calling it.
        """;

    private readonly AskQuestionUseCase _askQuestionUseCase;
    private readonly AskDataQuestionUseCase _askDataQuestionUseCase;
    private readonly CreateNoteUseCase _createNoteUseCase;
    private readonly SetReminderUseCase _setReminderUseCase;
    private readonly ILogger<AlmagestAgentService> _logger;
    private readonly ChatClientAgent _agent;

    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ToolApprovalRequestContent> _pendingApprovals = new();

    public AlmagestAgentService(
        IChatClient chatClient,
        AskQuestionUseCase askQuestionUseCase,
        AskDataQuestionUseCase askDataQuestionUseCase,
        CreateNoteUseCase createNoteUseCase,
        SetReminderUseCase setReminderUseCase,
        ILogger<AlmagestAgentService> logger)
    {
        _askQuestionUseCase = askQuestionUseCase;
        _askDataQuestionUseCase = askDataQuestionUseCase;
        _createNoteUseCase = createNoteUseCase;
        _setReminderUseCase = setReminderUseCase;
        _logger = logger;

        var searchDocumentsTool = AIFunctionFactory.Create(SearchDocumentsAsync, name: "search_documents");
        var queryPersonalDataTool = AIFunctionFactory.Create(QueryPersonalDataAsync, name: "query_personal_data");
        var createNoteTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreateNoteAsync, name: "create_note"));
        var setReminderTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(SetReminderAsync, name: "set_reminder"));

        var boundedClient = chatClient.AsBuilder()
            .UseFunctionInvocation(configure: options =>
            {
                options.MaximumIterationsPerRequest = MaxIterations;
                options.MaximumConsecutiveErrorsPerRequest = MaxConsecutiveErrors;
            })
            .Build();

        _agent = new ChatClientAgent(
            boundedClient,
            instructions: SystemInstructions,
            tools: [searchDocumentsTool, queryPersonalDataTool, createNoteTool, setReminderTool]);
    }

    public async Task<AgentTurnResult> RunAsync(string? sessionId, string message, CancellationToken cancellationToken = default)
    {
        var (id, session) = await ResolveSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var response = await _agent.RunAsync(message, session, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToResult(id, response);
    }

    public async Task<AgentTurnResult> ResumeAsync(
        string sessionId, string approvalRequestId, bool approved, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !_pendingApprovals.TryRemove(approvalRequestId, out var request))
        {
            throw new InvalidOperationException($"No pending approval '{approvalRequestId}' for session '{sessionId}'.");
        }

        var approvalMessage = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);
        var response = await _agent.RunAsync(approvalMessage, session, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToResult(sessionId, response);
    }

    [Description("Search the user's ingested documents (RAG). Use for questions about what a document says or means.")]
    private async Task<string> SearchDocumentsAsync(
        [Description("The question to answer from the user's documents.")] string question,
        CancellationToken cancellationToken)
    {
        var result = await RetryAsync(() => _askQuestionUseCase.ExecuteAsync(question, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return result.Found ? result.Answer : "Not found in the user's documents.";
    }

    [Description("Answer questions about tasks, projects, contacts, calendar, or document metadata via safe read-only SQL.")]
    private async Task<string> QueryPersonalDataAsync(
        [Description("The question to answer from the user's structured data.")] string question,
        CancellationToken cancellationToken)
    {
        var result = await RetryAsync(() => _askDataQuestionUseCase.ExecuteAsync(question, cancellationToken)).ConfigureAwait(false);
        return result.Answer;
    }

    [Description("Create a note. Requires approval.")]
    private async Task<string> CreateNoteAsync(
        [Description("The note's content.")] string content,
        CancellationToken cancellationToken)
    {
        var result = await RetryAsync(() => _createNoteUseCase.ExecuteAsync(content, cancellationToken)).ConfigureAwait(false);
        return $"Note created (id: {result.NoteId}).";
    }

    [Description("Set a reminder for a future date/time. Requires approval.")]
    private async Task<string> SetReminderAsync(
        [Description("The reminder message.")] string message,
        [Description("When to be reminded, ISO 8601 date-time.")] string remindAt,
        CancellationToken cancellationToken)
    {
        if (!DateTimeOffset.TryParse(remindAt, out var parsed))
        {
            return "Invalid date/time; use ISO 8601.";
        }

        var result = await RetryAsync(() => _setReminderUseCase.ExecuteAsync(message, parsed, cancellationToken)).ConfigureAwait(false);
        return $"Reminder set (id: {result.ReminderId}).";
    }

    private async Task<(string Id, AgentSession Session)> ResolveSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (sessionId is not null && _sessions.TryGetValue(sessionId, out var existing))
        {
            return (sessionId, existing);
        }

        var session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("n");
        _sessions[id] = session;
        return (id, session);
    }

    private AgentTurnResult ToResult(string sessionId, AgentResponse response)
    {
        var approvalRequests = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .ToList();

        var pending = new List<PendingApproval>(approvalRequests.Count);
        foreach (var request in approvalRequests)
        {
            _pendingApprovals[request.RequestId] = request;

            var call = (FunctionCallContent)request.ToolCall;
            var argumentsJson = JsonSerializer.Serialize(call.Arguments);
            _logger.LogInformation(
                "Tool {ToolName} selected, pending approval. Arguments: {Arguments}", call.Name, argumentsJson);

            pending.Add(new PendingApproval(request.RequestId, call.Name, argumentsJson));
        }

        var answer = pending.Count == 0 ? response.Text : null;
        if (answer is not null)
        {
            _logger.LogInformation("Agent turn completed for session {SessionId} with a direct answer.", sessionId);
        }

        return new AgentTurnResult(sessionId, answer, pending);
    }

    // Bounded exponential backoff around a single tool call -- never consumes
    // MaximumIterationsPerRequest, and a call that eventually succeeds still
    // counts as the one loop iteration it took (phase doc 3.6).
    private async Task<T> RetryAsync<T>(Func<Task<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxRetryAttempts)
            {
                _logger.LogWarning(ex, "Tool call attempt {Attempt} failed, retrying.", attempt);
                var backoff = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff).ConfigureAwait(false);
            }
        }
    }
}
