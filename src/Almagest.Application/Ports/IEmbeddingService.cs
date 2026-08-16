namespace Almagest.Application.Ports;

// Some embedding providers (Voyage AI among them) train separate encoders --
// or at least separate projection heads -- for the text being indexed versus
// the text used to search it, and expect the caller to say which is which on
// every request. Mixing them up doesn't fail loudly: both sides still
// produce valid-looking vectors, cosine similarity just degrades, silently,
// because query and document text no longer land in comparable regions of
// the embedding space. This is provider-agnostic on purpose -- callers say
// what they're embedding for, not which provider-specific flag to set.
public enum EmbeddingPurpose
{
    Document,
    Query,
}

public interface IEmbeddingService
{
    string ModelId { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default);
}
