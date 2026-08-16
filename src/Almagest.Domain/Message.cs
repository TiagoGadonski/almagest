namespace Almagest.Domain;

public sealed class Message
{
    public Guid Id { get; }

    public Guid SessionId { get; }

    public MessageRole Role { get; }

    public string Content { get; }

    public int Position { get; }

    public DateTimeOffset CreatedAt { get; }

    private Message(Guid id, Guid sessionId, MessageRole role, string content, int position, DateTimeOffset createdAt)
    {
        Id = id;
        SessionId = sessionId;
        Role = role;
        Content = content;
        Position = position;
        CreatedAt = createdAt;
    }

    public static Message Create(Guid sessionId, MessageRole role, string content, int position)
    {
        Validate(sessionId, content, position);

        return new Message(Guid.NewGuid(), sessionId, role, content, position, DateTimeOffset.UtcNow);
    }

    // Rehydrates a message with its already-assigned id and timestamp, for a
    // conversation store mapping rows back from persistence. Internal for
    // the same reason as DocumentChunk.Reconstitute -- minting identity is a
    // storage-layer concern.
    internal static Message Reconstitute(
        Guid id, Guid sessionId, MessageRole role, string content, int position, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Persisted message must have an id.", nameof(id));
        }

        Validate(sessionId, content, position);

        return new Message(id, sessionId, role, content, position, createdAt);
    }

    private static void Validate(Guid sessionId, string content, int position)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Message must belong to a session.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(content));
        }

        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "Message position cannot be negative.");
        }
    }
}
