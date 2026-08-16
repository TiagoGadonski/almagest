using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeReminderStore : IReminderStore
{
    public List<Reminder> Saved { get; } = [];

    public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        Saved.Add(reminder);
        return Task.CompletedTask;
    }
}
