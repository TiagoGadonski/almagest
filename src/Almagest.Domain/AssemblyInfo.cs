using System.Runtime.CompilerServices;

// PgVectorChunkStore needs DocumentChunk.Reconstitute to rehydrate rows read
// back from the database with their real, already-assigned id.
[assembly: InternalsVisibleTo("Almagest.Infrastructure")]
