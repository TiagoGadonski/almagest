using Almagest.Domain;

namespace Almagest.Application.Ports;

public interface IDocumentParser
{
    DocumentSource Source { get; }

    Task<ParsedDocument> ParseAsync(Stream content, CancellationToken cancellationToken = default);
}

public sealed record ParsedDocument(string Text, IReadOnlyList<HeadingMarker> Headings);

public sealed record HeadingMarker(int Offset, string Title);
