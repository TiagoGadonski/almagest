using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeMetadataExtractor(MetadataExtractionResult? result = null) : IMetadataExtractor
{
    public Task<MetadataExtractionResult> ExtractAsync(Guid documentId, string documentText, CancellationToken cancellationToken = default) =>
        Task.FromResult(result ?? new MetadataExtractionResult(false, null));
}
