namespace Almagest.Domain;

// The extracted, filterable facts about a document. Deliberately separate
// from Document itself: a Document always exists once ingested (it has a
// title, from the request), but its metadata may be absent -- extraction is
// an enrichment that can fail and degrade without blocking ingestion.
public sealed class DocumentMetadata
{
    public Guid DocumentId { get; }

    public string? DocumentType { get; }

    public DateOnly? DateRangeStart { get; }

    public DateOnly? DateRangeEnd { get; }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<string> CitedEntities { get; }

    // The model's inferred title, distinct from Document.Title (the
    // request-supplied one). Informational only -- never overrides it.
    public string? ExtractedTitle { get; }

    private DocumentMetadata(
        Guid documentId,
        string? documentType,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> citedEntities,
        string? extractedTitle)
    {
        DocumentId = documentId;
        DocumentType = documentType;
        DateRangeStart = dateRangeStart;
        DateRangeEnd = dateRangeEnd;
        Tags = tags;
        CitedEntities = citedEntities;
        ExtractedTitle = extractedTitle;
    }

    public static DocumentMetadata Create(
        Guid documentId,
        string? documentType,
        DateOnly? dateRangeStart,
        DateOnly? dateRangeEnd,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? citedEntities = null,
        string? extractedTitle = null)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Metadata must belong to a document.", nameof(documentId));
        }

        if (dateRangeStart is not null && dateRangeEnd is not null && dateRangeStart > dateRangeEnd)
        {
            throw new ArgumentException("Date range start cannot be after its end.", nameof(dateRangeStart));
        }

        return new DocumentMetadata(documentId, documentType, dateRangeStart, dateRangeEnd, tags ?? [], citedEntities ?? [], extractedTitle);
    }
}
