namespace Almagest.Application.Ports;

public interface ITextChunker
{
    IReadOnlyList<TextChunk> Chunk(ParsedDocument document, ChunkingOptions options);
}

public sealed record TextChunk(string Text, int Position, string? SectionTitle);

public sealed record ChunkingOptions(int TargetTokens, double OverlapRatio);
