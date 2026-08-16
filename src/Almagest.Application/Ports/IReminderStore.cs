using Almagest.Domain;

namespace Almagest.Application.Ports;

public interface IReminderStore
{
    Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default);
}
