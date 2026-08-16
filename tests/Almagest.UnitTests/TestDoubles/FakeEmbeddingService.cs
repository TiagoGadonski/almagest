using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeEmbeddingService(string modelId, Func<IReadOnlyList<string>, IReadOnlyList<float[]>>? embed = null)
    : IEmbeddingService
{
    public string ModelId { get; } = modelId;

    public IReadOnlyList<string>? LastRequest { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        LastRequest = texts;

        var vectors = embed?.Invoke(texts)
            ?? texts.Select(text => new float[] { text.Length }).ToList();

        return Task.FromResult(vectors);
    }
}
