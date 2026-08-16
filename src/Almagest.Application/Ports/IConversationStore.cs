using Almagest.Domain;

namespace Almagest.Application.Ports;

public interface IConversationStore
{
    Task<Session?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Message>> GetMessagesAsync(Guid sessionId, int fromPositionExclusive, CancellationToken cancellationToken = default);

    Task AppendMessageAsync(Message message, CancellationToken cancellationToken = default);
}
