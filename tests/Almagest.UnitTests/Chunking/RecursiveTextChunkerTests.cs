using Almagest.Application.Ports;
using Almagest.Infrastructure.Chunking;

namespace Almagest.UnitTests.Chunking;

// Spec for RecursiveTextChunker, per docs/phases/01-rag.md section 3.3
// (paragraph -> sentence -> word splitting, with overlap). The chunker is a
// stub (NotImplementedException) by design -- these tests are intentionally
// red until the real implementation lands; that's the point of writing them
// now.
public class RecursiveTextChunkerTests
{
    private static readonly ChunkingOptions DefaultOptions = new(TargetTokens: 800, OverlapRatio: 0.12);

    [Fact]
    public void Chunk_EmptyInput_ReturnsNoChunks()
    {
        var chunker = new RecursiveTextChunker();
        var document = new ParsedDocument(string.Empty, []);

        var chunks = chunker.Chunk(document, DefaultOptions);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_TwoParagraphsThatTogetherExceedTheTarget_SplitsAtTheParagraphBoundary()
    {
        var chunker = new RecursiveTextChunker();
        var firstParagraph = string.Join(' ', Enumerable.Repeat("alpha", 50));
        var secondParagraph = string.Join(' ', Enumerable.Repeat("beta", 900));
        var document = new ParsedDocument($"{firstParagraph}\n\n{secondParagraph}", []);

        var chunks = chunker.Chunk(document, DefaultOptions);

        Assert.True(chunks.Count >= 2);
        Assert.DoesNotContain("beta", chunks[0].Text);
        Assert.DoesNotContain("alpha", chunks[^1].Text);
    }

    [Fact]
    public void Chunk_ConsecutiveChunks_OverlapAtTheBoundary()
    {
        var chunker = new RecursiveTextChunker();
        var words = Enumerable.Range(0, 3000).Select(i => $"word{i}");
        var document = new ParsedDocument(string.Join(' ', words), []);

        var chunks = chunker.Chunk(document, DefaultOptions);

        Assert.True(chunks.Count >= 2);

        var firstChunkWords = chunks[0].Text.Split(' ');
        var secondChunkWords = chunks[1].Text.Split(' ');

        Assert.True(
            firstChunkWords.Intersect(secondChunkWords).Any(),
            "Consecutive chunks should share overlapping content at the boundary.");
    }

    [Fact]
    public void Chunk_ParagraphLargerThanTarget_IsSplitFurtherRatherThanReturnedWhole()
    {
        var chunker = new RecursiveTextChunker();
        var oversizedParagraph = string.Join(' ', Enumerable.Repeat("word", 5000));
        var document = new ParsedDocument(oversizedParagraph, []);

        var chunks = chunker.Chunk(document, DefaultOptions);

        Assert.True(chunks.Count > 1, "A paragraph far larger than the target size must be split further.");
        Assert.All(chunks, chunk => Assert.True(
            chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= DefaultOptions.TargetTokens,
            "No chunk should exceed the configured target token budget."));
    }

    [Fact]
    public void Chunk_Positions_AreSequentialStartingAtZero()
    {
        var chunker = new RecursiveTextChunker();
        var words = Enumerable.Range(0, 3000).Select(i => $"word{i}");
        var document = new ParsedDocument(string.Join(' ', words), []);

        var chunks = chunker.Chunk(document, DefaultOptions);

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Position));
    }

    [Fact]
public void Chunk_OversizedParagraph_ProducesChunksNearTheTargetSize()
{
    var chunker = new RecursiveTextChunker();
    var document = new ParsedDocument(string.Join(' ', Enumerable.Repeat("word", 5000)), []);

    var chunks = chunker.Chunk(document, DefaultOptions);

    Assert.All(chunks.SkipLast(1), chunk => Assert.True(
        chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > DefaultOptions.TargetTokens / 2,
        "Pieces must be merged back up to the budget, not emitted one word at a time."));
}

[Fact]
public void Chunk_WithHeadings_AssignsTheEnclosingSectionTitle()
{
    var chunker = new RecursiveTextChunker();
    var options = new ChunkingOptions(TargetTokens: 50, OverlapRatio: 0.0);

    var intro = string.Join(' ', Enumerable.Repeat("intro", 40));
    var body = string.Join(' ', Enumerable.Repeat("body", 40));
    var text = $"{intro}\n\n{body}";

    var document = new ParsedDocument(text, [
        new HeadingMarker(0, "Introduction"),
        new HeadingMarker(intro.Length + 2, "Body")
    ]);

    var chunks = chunker.Chunk(document, options);

    Assert.True(chunks.Count >= 2);
    Assert.Equal("Introduction", chunks[0].SectionTitle);
    Assert.Equal("Body", chunks[^1].SectionTitle);
}
}
