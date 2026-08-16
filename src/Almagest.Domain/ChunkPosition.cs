namespace Almagest.Domain;

// The chunk's order within its document -- 0-based, contiguous. Nothing here
// knows about character offsets or tokens; that detail belongs to whatever
// produced the chunk, not to its identity.
public sealed class ChunkPosition
{
    public int Index { get; }

    public ChunkPosition(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Chunk position cannot be negative.");
        }

        Index = index;
    }

    public override bool Equals(object? obj) => obj is ChunkPosition other && other.Index == Index;

    public override int GetHashCode() => Index.GetHashCode();

    public override string ToString() => Index.ToString();
}
