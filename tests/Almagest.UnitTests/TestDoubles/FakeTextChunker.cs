using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeTextChunker(IReadOnlyList<TextChunk> result) : ITextChunker
{
    public IReadOnlyList<TextChunk> Chunk(ParsedDocument document, ChunkingOptions options) => result;
}
