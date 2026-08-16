using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeDocumentParser(DocumentSource source, ParsedDocument result) : IDocumentParser
{
    public DocumentSource Source => source;

    public Task<ParsedDocument> ParseAsync(Stream content, CancellationToken cancellationToken = default) =>
        Task.FromResult(result);
}
