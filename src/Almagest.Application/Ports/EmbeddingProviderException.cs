namespace Almagest.Application.Ports;

public enum EmbeddingProviderErrorKind
{
    InvalidCredentials,
    RateLimited,
}

// Provider-agnostic, same reasoning as EmbeddingPurpose: callers at the API
// boundary need to distinguish "our credentials are wrong" (a server
// misconfiguration) from "we're being rate limited" (a transient, retry-
// later condition) from every other embedding failure, without knowing
// which concrete provider is behind IEmbeddingService.
public sealed class EmbeddingProviderException : Exception
{
    public EmbeddingProviderErrorKind Kind { get; }

    public TimeSpan? RetryAfter { get; }

    public EmbeddingProviderException(EmbeddingProviderErrorKind kind, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }
}
