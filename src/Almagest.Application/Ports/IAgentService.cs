namespace Almagest.Application.Ports;

public interface IAgentService
{
    Task<AgentTurnResult> RunAsync(string? sessionId, string message, CancellationToken cancellationToken = default);

    Task<AgentTurnResult> ResumeAsync(string sessionId, string approvalRequestId, bool approved, CancellationToken cancellationToken = default);
}

public sealed record AgentTurnResult(string SessionId, string? Answer, IReadOnlyList<PendingApproval> PendingApprovals);

public sealed record PendingApproval(string RequestId, string ToolName, string ArgumentsJson);
