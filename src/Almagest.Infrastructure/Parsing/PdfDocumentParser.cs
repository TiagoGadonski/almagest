using Almagest.Application.Ports;
using Almagest.Domain;
using UglyToad.PdfPig;

namespace Almagest.Infrastructure.Parsing;

// PDFs carry no reliable structural markup here, so headings always come
// back empty -- chunks produced from a PDF get a null SectionTitle.
public sealed class PdfDocumentParser : IDocumentParser
{
    public DocumentSource Source => DocumentSource.Pdf;

    public Task<ParsedDocument> ParseAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(content);

        var text = string.Join("\n\n", document.GetPages().Select(page => page.Text));

        return Task.FromResult(new ParsedDocument(text, Array.Empty<HeadingMarker>()));
    }
}
