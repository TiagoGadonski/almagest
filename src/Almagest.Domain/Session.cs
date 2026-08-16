namespace Almagest.Domain;

// Unlike Document/DocumentChunk, a session is mutated over its lifetime --
// its summary advances and its activity timestamp updates -- so it exposes
// behavior rather than just being reconstructed fresh each time.
public sealed class Session
{
    public Guid Id { get; }

    public string? Summary { get; private set; }

    // -1 means "nothing summarized yet"; every message with a greater
    // position is still part of the active, unsummarized window.
    public int SummarizedThroughPosition { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastActiveAt { get; private set; }

    private Session(Guid id, string? summary, int summarizedThroughPosition, DateTimeOffset createdAt, DateTimeOffset lastActiveAt)
    {
        Id = id;
        Summary = summary;
        SummarizedThroughPosition = summarizedThroughPosition;
        CreatedAt = createdAt;
        LastActiveAt = lastActiveAt;
    }

    public static Session Create()
    {
        var now = DateTimeOffset.UtcNow;
        return new Session(Guid.NewGuid(), summary: null, summarizedThroughPosition: -1, now, now);
    }

    internal static Session Reconstitute(
        Guid id, string? summary, int summarizedThroughPosition, DateTimeOffset createdAt, DateTimeOffset lastActiveAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Persisted session must have an id.", nameof(id));
        }

        return new Session(id, summary, summarizedThroughPosition, createdAt, lastActiveAt);
    }

    public void ApplySummary(string summary, int throughPosition)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Summary cannot be empty.", nameof(summary));
        }

        if (throughPosition < SummarizedThroughPosition)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughPosition), throughPosition, "Cannot move the summarized cutoff backwards.");
        }

        Summary = summary;
        SummarizedThroughPosition = throughPosition;
    }

    public void Touch() => LastActiveAt = DateTimeOffset.UtcNow;
}
