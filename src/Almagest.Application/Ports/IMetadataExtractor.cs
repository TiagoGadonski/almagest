using Almagest.Domain;

namespace Almagest.Application.Ports;

public interface IMetadataExtractor
{
    Task<MetadataExtractionResult> ExtractAsync(Guid documentId, string documentText, CancellationToken cancellationToken = default);
}

public sealed record MetadataExtractionResult(bool Succeeded, DocumentMetadata? Metadata);
