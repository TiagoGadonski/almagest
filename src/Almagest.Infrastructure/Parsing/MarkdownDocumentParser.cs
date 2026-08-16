using System.Text;
using System.Text.RegularExpressions;
using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.Infrastructure.Parsing;

// Reads the file as-is and extracts ATX heading offsets (# .. ######) as a
// structural hint for the chunker. Markup is not stripped from the text that
// gets chunked/embedded -- see README "Known limitations".
public sealed partial class MarkdownDocumentParser : IDocumentParser
{
    public DocumentSource Source => DocumentSource.Markdown;

    public async Task<ParsedDocument> ParseAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var headings = HeadingPattern()
            .Matches(text)
            .Select(match => new HeadingMarker(match.Index, match.Groups["title"].Value.Trim()))
            .ToList();

        return new ParsedDocument(text, headings);
    }

    [GeneratedRegex(@"^#{1,6}[ \t]+(?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();
}
