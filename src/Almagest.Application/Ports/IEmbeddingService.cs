namespace Almagest.Application.Ports;

public interface IEmbeddingService
{
    string ModelId { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
