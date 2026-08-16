namespace Almagest.Domain;

public sealed class Note
{
    public Guid Id { get; }

    public string Content { get; }

    public DateTimeOffset CreatedAt { get; }

    private Note(Guid id, string content, DateTimeOffset createdAt)
    {
        Id = id;
        Content = content;
        CreatedAt = createdAt;
    }

    public static Note Create(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Note content cannot be empty.", nameof(content));
        }

        return new Note(Guid.NewGuid(), content, DateTimeOffset.UtcNow);
    }
}
