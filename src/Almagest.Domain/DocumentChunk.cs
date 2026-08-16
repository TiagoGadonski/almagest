namespace Almagest.Domain;

// A chunk knows it has text, an origin and a position -- it does not know
// what a vector is. Embeddings are paired with a chunk only at the
// Application/Infrastructure boundary, never here.
public sealed class DocumentChunk
{
    public Guid Id { get; }

    public Guid DocumentId { get; }

    public string Text { get; }

    public ChunkPosition Position { get; }

    public string? SectionTitle { get; }

    private DocumentChunk(Guid id, Guid documentId, string text, ChunkPosition position, string? sectionTitle)
    {
        Id = id;
        DocumentId = documentId;
        Text = text;
        Position = position;
        SectionTitle = sectionTitle;
    }

    public static DocumentChunk Create(Guid documentId, string text, int positionIndex, string? sectionTitle = null)
    {
        Validate(documentId, text);

        return new DocumentChunk(Guid.NewGuid(), documentId, text, new ChunkPosition(positionIndex), sectionTitle);
    }

    // Rehydrates a chunk with its already-assigned id, for callers reading a
    // previously persisted row back (e.g. a chunk store mapping a query
    // result). Internal because minting an id is a storage-layer concern,
    // not something Application or Api code should ever do.
    internal static DocumentChunk Reconstitute(Guid id, Guid documentId, string text, int positionIndex, string? sectionTitle)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Persisted chunk must have an id.", nameof(id));
        }

        Validate(documentId, text);

        return new DocumentChunk(id, documentId, text, new ChunkPosition(positionIndex), sectionTitle);
    }

    private static void Validate(Guid documentId, string text)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Chunk must belong to a document.", nameof(documentId));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Chunk text cannot be empty.", nameof(text));
        }
    }
}
