using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeConversationStore : IConversationStore
{
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly List<Message> _messages = [];

    public IReadOnlyList<Message> AppendedMessages => _messages;

    public Task<Session?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.GetValueOrDefault(sessionId));

    public Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Message>> GetMessagesAsync(
        Guid sessionId, int fromPositionExclusive, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Message> result = _messages
            .Where(m => m.SessionId == sessionId && m.Position > fromPositionExclusive)
            .OrderBy(m => m.Position)
            .ToList();

        return Task.FromResult(result);
    }

    public Task AppendMessageAsync(Message message, CancellationToken cancellationToken = default)
    {
        _messages.Add(message);
        return Task.CompletedTask;
    }
}
