namespace Almagest.Domain;

// The unit of ingestion. Almagest does not persist this directly (Phase 1 has
// no documents table -- see the README's Known limitations); it exists so
// ingestion has a stable id and provenance to stamp onto every chunk it
// produces.
public sealed class Document
{
    public Guid Id { get; }

    public string Title { get; }

    public DocumentSource Source { get; }

    public DateTimeOffset IngestedAt { get; }

    private Document(Guid id, string title, DocumentSource source, DateTimeOffset ingestedAt)
    {
        Id = id;
        Title = title;
        Source = source;
        IngestedAt = ingestedAt;
    }

    public static Document Create(string title, DocumentSource source)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Document title cannot be empty.", nameof(title));
        }

        return new Document(Guid.NewGuid(), title, source, DateTimeOffset.UtcNow);
    }
}
