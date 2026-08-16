namespace Almagest.Domain;

public sealed class CalendarEvent
{
    public Guid Id { get; }

    public string Title { get; }

    public DateTimeOffset StartsAt { get; }

    public DateTimeOffset? EndsAt { get; }

    public string? Location { get; }

    public Guid? RelatedContactId { get; }

    public DateTimeOffset CreatedAt { get; }

    private CalendarEvent(
        Guid id,
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt,
        string? location,
        Guid? relatedContactId,
        DateTimeOffset createdAt)
    {
        Id = id;
        Title = title;
        StartsAt = startsAt;
        EndsAt = endsAt;
        Location = location;
        RelatedContactId = relatedContactId;
        CreatedAt = createdAt;
    }

    public static CalendarEvent Create(
        string title, DateTimeOffset startsAt, DateTimeOffset? endsAt = null, string? location = null, Guid? relatedContactId = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Calendar event title cannot be empty.", nameof(title));
        }

        if (endsAt is not null && endsAt < startsAt)
        {
            throw new ArgumentException("Calendar event cannot end before it starts.", nameof(endsAt));
        }

        return new CalendarEvent(Guid.NewGuid(), title, startsAt, endsAt, location, relatedContactId, DateTimeOffset.UtcNow);
    }

    internal static CalendarEvent Reconstitute(
        Guid id,
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt,
        string? location,
        Guid? relatedContactId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Persisted calendar event must have an id.", nameof(id));
        }

        return new CalendarEvent(id, title, startsAt, endsAt, location, relatedContactId, createdAt);
    }
}
