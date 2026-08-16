using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.Application.UseCases;

public sealed record SetReminderResult(Guid ReminderId);

public sealed class SetReminderUseCase
{
    private readonly IReminderStore _reminderStore;

    public SetReminderUseCase(IReminderStore reminderStore)
    {
        _reminderStore = reminderStore;
    }

    public async Task<SetReminderResult> ExecuteAsync(string message, DateTimeOffset remindAt, CancellationToken cancellationToken = default)
    {
        var reminder = Reminder.Create(message, remindAt);
        await _reminderStore.SaveAsync(reminder, cancellationToken).ConfigureAwait(false);
        return new SetReminderResult(reminder.Id);
    }
}
