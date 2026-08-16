using System.Text;
using Almagest.Application.Ports;

namespace Almagest.Infrastructure.Chunking;

/// <summary>
/// Recursive character-based chunker. Splits on progressively weaker
/// separators (paragraph, line, sentence, word) until every piece fits the
/// budget, then merges consecutive pieces back up to the budget without
/// crossing paragraph boundaries. Token counts are approximated by
/// whitespace-delimited words; a model tokenizer would be more accurate and
/// is recorded as a known limitation.
/// </summary>
public sealed class RecursiveTextChunker : ITextChunker
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", " "];

    private readonly record struct Piece(string Text, int Offset);

    public IReadOnlyList<TextChunk> Chunk(ParsedDocument document, ChunkingOptions options)
    {
        if (string.IsNullOrWhiteSpace(document.Text))
            return [];

        // The overlap is prefixed onto each chunk, so it has to be reserved
        // up front — otherwise budget + overlap exceeds TargetTokens.
        var overlapWords = (int)(options.TargetTokens * options.OverlapRatio);
        var budget = Math.Max(1, options.TargetTokens - overlapWords);

        var pieces = Split(document.Text, offset: 0, budget, separatorIndex: 0);
        var groups = Merge(document.Text, pieces, budget);

        var chunks = new List<TextChunk>(groups.Count);

        for (var i = 0; i < groups.Count; i++)
        {
            var text = groups[i].Text;

            if (i > 0 && overlapWords > 0)
            {
                var carry = LastWords(groups[i - 1].Text, overlapWords);
                if (carry.Length > 0)
                    text = $"{carry} {text}";
            }

            chunks.Add(new TextChunk(
                text,
                Position: i,
                SectionTitle: SectionTitleAt(document.Headings, groups[i].Offset)));
        }

        return chunks;
    }

    /// <summary>
    /// Breaks the text down until every piece fits the budget. Each level of
    /// recursion drops to a weaker separator, which is what guarantees
    /// termination: the separator list is finite.
    /// </summary>
    private static List<Piece> Split(string text, int offset, int maxWords, int separatorIndex)
    {
        if (CountWords(text) <= maxWords || separatorIndex >= Separators.Length)
            return [new Piece(text, offset)];

        var separator = Separators[separatorIndex];
        var result = new List<Piece>();
        var cursor = 0;

        foreach (var part in text.Split(separator))
        {
            var partOffset = offset + cursor;
            cursor += part.Length + separator.Length;

            if (string.IsNullOrWhiteSpace(part))
                continue;

            if (CountWords(part) <= maxWords)
                result.Add(new Piece(part, partOffset));
            else
                result.AddRange(Split(part, partOffset, maxWords, separatorIndex + 1));
        }

        return result;
    }

    /// <summary>
    /// Merges consecutive pieces up to the budget. Without this, an oversized
    /// paragraph with no sentence punctuation collapses into one chunk per
    /// word — each embedding then carries almost no meaning. Paragraph
    /// boundaries are never merged across.
    /// </summary>
    private static List<Piece> Merge(string source, List<Piece> pieces, int maxWords)
    {
        var groups = new List<Piece>();

        if (pieces.Count == 0)
            return groups;

        var buffer = new StringBuilder();
        var bufferWords = 0;
        var bufferOffset = pieces[0].Offset;
        var previousEnd = -1;

        foreach (var piece in pieces)
        {
            var words = CountWords(piece.Text);
            var gap = previousEnd >= 0 ? piece.Offset - previousEnd : 0;

            var crossesParagraph = gap > 0
                && source.AsSpan(previousEnd, gap).Contains("\n\n", StringComparison.Ordinal);

            if (bufferWords > 0 && (crossesParagraph || bufferWords + words > maxWords))
            {
                groups.Add(new Piece(buffer.ToString(), bufferOffset));
                buffer.Clear();
                bufferWords = 0;
                bufferOffset = piece.Offset;
            }

            if (buffer.Length > 0)
                buffer.Append(' ');

            buffer.Append(piece.Text);
            bufferWords += words;
            previousEnd = piece.Offset + piece.Text.Length;
        }

        if (buffer.Length > 0)
            groups.Add(new Piece(buffer.ToString(), bufferOffset));

        return groups;
    }

    /// <summary>
    /// The section a chunk belongs to is the last heading that starts at or
    /// before it. Headings are assumed ordered by offset, as produced by the
    /// parsers. Linear scan: heading counts are small enough that a binary
    /// search would not pay for itself.
    /// </summary>
    private static string? SectionTitleAt(IReadOnlyList<HeadingMarker> headings, int offset)
    {
        string? title = null;

        foreach (var heading in headings)
        {
            if (heading.Offset > offset)
                break;

            title = heading.Title;
        }

        return title;
    }

    private static string LastWords(string text, int count)
    {
        if (count <= 0)
            return string.Empty;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length <= count
            ? text
            : string.Join(' ', words[^count..]);
    }

    private static int CountWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}