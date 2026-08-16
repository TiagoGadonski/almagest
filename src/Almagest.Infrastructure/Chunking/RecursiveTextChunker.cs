using System.Text;
using Almagest.Application.Ports;

namespace Almagest.Infrastructure.Chunking;

/// <summary>
/// Recursive text chunker.
///
/// Paragraphs are the top-level unit. A paragraph that fits the budget stays
/// whole; one that does not is broken down on progressively weaker separators
/// (line, sentence, word) until every piece fits, and its pieces are marked as
/// fragments of that paragraph.
///
/// Pieces are then merged back up to the budget. Whole paragraphs merge freely
/// with their neighbours — without that, a document made of many short
/// paragraphs yields one chunk per paragraph and each embedding carries almost
/// no context. Fragments only merge with fragments of the same paragraph, so
/// unrelated sections never end up sharing a chunk.
///
/// Token counts are approximated by whitespace-delimited words. A model
/// tokenizer would be more accurate and is recorded as a known limitation.
/// </summary>
public sealed class RecursiveTextChunker : ITextChunker
{
    private const string ParagraphSeparator = "\n\n";

    // Paragraphs are handled separately in SplitIntoUnits, so the cascade
    // below only applies inside an oversized paragraph.
    private static readonly string[] Separators = ["\n", ". ", " "];

    private readonly record struct Piece(string Text, int Offset, int UnitId, bool IsFragment);

    public IReadOnlyList<TextChunk> Chunk(ParsedDocument document, ChunkingOptions options)
    {
        if (string.IsNullOrWhiteSpace(document.Text))
            return [];

        // The overlap is prefixed onto each chunk, so it has to be reserved up
        // front — otherwise budget + overlap exceeds TargetTokens.
        var overlapWords = (int)(options.TargetTokens * options.OverlapRatio);
        var budget = Math.Max(1, options.TargetTokens - overlapWords);

        var pieces = SplitIntoUnits(document.Text, budget);
        var groups = Merge(pieces, budget);

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
    /// Splits the document into paragraphs, breaking down any paragraph that
    /// exceeds the budget. Offsets are tracked against the original text
    /// because section attribution depends on them.
    /// </summary>
    private static List<Piece> SplitIntoUnits(string text, int maxWords)
    {
        var pieces = new List<Piece>();
        var cursor = 0;
        var unitId = 0;

        foreach (var paragraph in text.Split(ParagraphSeparator))
        {
            var offset = cursor;
            cursor += paragraph.Length + ParagraphSeparator.Length;

            if (string.IsNullOrWhiteSpace(paragraph))
                continue;

            if (CountWords(paragraph) <= maxWords)
            {
                pieces.Add(new Piece(paragraph, offset, unitId, IsFragment: false));
            }
            else
            {
                foreach (var (fragment, fragmentOffset) in Split(paragraph, offset, maxWords, 0))
                    pieces.Add(new Piece(fragment, fragmentOffset, unitId, IsFragment: true));
            }

            unitId++;
        }

        return pieces;
    }

    /// <summary>
    /// Breaks an oversized paragraph down until every piece fits. Each level of
    /// recursion drops to a weaker separator, which is what guarantees
    /// termination: the separator list is finite.
    /// </summary>
    private static List<(string Text, int Offset)> Split(
        string text,
        int offset,
        int maxWords,
        int separatorIndex)
    {
        if (CountWords(text) <= maxWords || separatorIndex >= Separators.Length)
            return [(text, offset)];

        var separator = Separators[separatorIndex];
        var result = new List<(string, int)>();
        var cursor = 0;

        foreach (var part in text.Split(separator))
        {
            var partOffset = offset + cursor;
            cursor += part.Length + separator.Length;

            if (string.IsNullOrWhiteSpace(part))
                continue;

            if (CountWords(part) <= maxWords)
                result.Add((part, partOffset));
            else
                result.AddRange(Split(part, partOffset, maxWords, separatorIndex + 1));
        }

        return result;
    }

    /// <summary>
    /// Merges consecutive pieces up to the budget. A boundary is forced when
    /// moving between paragraphs and either side is a fragment: fragments carry
    /// only part of their paragraph's meaning, so mixing them with a different
    /// paragraph would blur two topics into one embedding.
    /// </summary>
    private static List<Piece> Merge(List<Piece> pieces, int maxWords)
    {
        var groups = new List<Piece>();

        if (pieces.Count == 0)
            return groups;

        var buffer = new StringBuilder();
        var bufferWords = 0;
        var bufferOffset = pieces[0].Offset;
        var bufferUnitId = pieces[0].UnitId;
        var bufferHasFragment = false;

        foreach (var piece in pieces)
        {
            var words = CountWords(piece.Text);
            var startsNewParagraph = piece.UnitId != bufferUnitId;
            var boundary = startsNewParagraph && (piece.IsFragment || bufferHasFragment);

            if (bufferWords > 0 && (boundary || bufferWords + words > maxWords))
            {
                groups.Add(new Piece(buffer.ToString(), bufferOffset, bufferUnitId, bufferHasFragment));

                buffer.Clear();
                bufferWords = 0;
                bufferOffset = piece.Offset;
                bufferHasFragment = false;
            }
            else if (buffer.Length > 0)
            {
                buffer.Append(startsNewParagraph ? ParagraphSeparator : " ");
            }

            buffer.Append(piece.Text);
            bufferWords += words;
            bufferUnitId = piece.UnitId;
            bufferHasFragment |= piece.IsFragment;
        }

        if (buffer.Length > 0)
            groups.Add(new Piece(buffer.ToString(), bufferOffset, bufferUnitId, bufferHasFragment));

        return groups;
    }

    /// <summary>
    /// The section a chunk belongs to is the last heading starting at or before
    /// it. Headings are assumed ordered by offset, as produced by the parsers.
    /// Linear scan: heading counts are small enough that a binary search would
    /// not pay for itself.
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