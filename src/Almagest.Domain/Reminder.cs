namespace Almagest.Domain;

public sealed class Reminder
{
    public Guid Id { get; }

    public string Message { get; }

    public DateTimeOffset RemindAt { get; }

    public DateTimeOffset CreatedAt { get; }

    private Reminder(Guid id, string message, DateTimeOffset remindAt, DateTimeOffset createdAt)
    {
        Id = id;
        Message = message;
        RemindAt = remindAt;
        CreatedAt = createdAt;
    }

    public static Reminder Create(string message, DateTimeOffset remindAt)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Reminder message cannot be empty.", nameof(message));
        }

        return new Reminder(Guid.NewGuid(), message, remindAt, DateTimeOffset.UtcNow);
    }
}
