using System.Text.Json;
using Almagest.Application.Ports;
using Almagest.Domain;
using Almagest.Infrastructure.Telemetry;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Almagest.Infrastructure.Persistence;

public sealed class PgVectorChunkStore : IChunkStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PgVectorChunkStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveAsync(
        Document document,
        IReadOnlyList<EmbeddedChunk> chunks,
        DocumentMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        using var activity = AlmagestTelemetry.ActivitySource.StartActivity("db.chunk_store.save");
        activity?.SetTag("db.chunk_count", chunks.Count);

        const string documentSql = """
            INSERT INTO documents
                (id, title, document_type, document_date_start, document_date_end, extracted_metadata, created_at)
            VALUES
                (@id, @title, @document_type, @date_start, @date_end, @extracted_metadata, @created_at)
            """;

        const string chunkSql = """
            INSERT INTO document_chunks
                (id, document_id, chunk_text, chunk_position, section_title, embedding, embedding_model_id)
            VALUES
                (@id, @document_id, @chunk_text, @chunk_position, @section_title, @embedding, @embedding_model_id)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var documentCommand = new NpgsqlCommand(documentSql, connection, transaction))
        {
            documentCommand.Parameters.AddWithValue("id", document.Id);
            documentCommand.Parameters.AddWithValue("title", document.Title);
            documentCommand.Parameters.AddWithValue("document_type", (object?)metadata?.DocumentType ?? DBNull.Value);
            documentCommand.Parameters.AddWithValue("date_start", (object?)metadata?.DateRangeStart ?? DBNull.Value);
            documentCommand.Parameters.AddWithValue("date_end", (object?)metadata?.DateRangeEnd ?? DBNull.Value);
            documentCommand.Parameters.Add("extracted_metadata", NpgsqlDbType.Jsonb).Value =
                (object?)SerializeExtractedMetadata(metadata) ?? DBNull.Value;
            documentCommand.Parameters.AddWithValue("created_at", document.IngestedAt);

            await documentCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in chunks)
        {
            await using var command = new NpgsqlCommand(chunkSql, connection, transaction);
            command.Parameters.AddWithValue("id", item.Chunk.Id);
            command.Parameters.AddWithValue("document_id", item.Chunk.DocumentId);
            command.Parameters.AddWithValue("chunk_text", item.Chunk.Text);
            command.Parameters.AddWithValue("chunk_position", item.Chunk.Position.Index);
            command.Parameters.AddWithValue("section_title", (object?)item.Chunk.SectionTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("embedding", new Vector(item.Embedding));
            command.Parameters.AddWithValue("embedding_model_id", item.EmbeddingModelId);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        ChunkSearchFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = AlmagestTelemetry.ActivitySource.StartActivity("db.chunk_store.search");
        activity?.SetTag("db.top_k", topK);

        var whereClauses = new List<string>();
        var parameters = new List<NpgsqlParameter>
        {
            new("query_embedding", new Vector(queryEmbedding)),
            new("top_k", topK),
        };

        if (filter?.DocumentType is { } documentType)
        {
            whereClauses.Add("d.document_type = @document_type");
            parameters.Add(new NpgsqlParameter("document_type", documentType));
        }

        if (filter?.DateRangeStart is { } rangeStart)
        {
            // Overlap check: the document's date range must reach at least this far forward.
            whereClauses.Add("d.document_date_end >= @date_start");
            parameters.Add(new NpgsqlParameter("date_start", rangeStart));
        }

        if (filter?.DateRangeEnd is { } rangeEnd)
        {
            // Overlap check: the document's date range must not start after this.
            whereClauses.Add("d.document_date_start <= @date_end");
            parameters.Add(new NpgsqlParameter("date_end", rangeEnd));
        }

        if (filter?.Tags is { Count: > 0 } tags)
        {
            // ?& checks every given string is present in the jsonb array -- AND semantics.
            whereClauses.Add("d.extracted_metadata -> 'tags' ?& @tags");
            parameters.Add(new NpgsqlParameter("tags", tags.ToArray()));
        }

        var whereSql = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;

        var sql = $"""
            SELECT c.id, c.document_id, c.chunk_text, c.chunk_position, c.section_title, c.embedding_model_id,
                   1 - (c.embedding <=> @query_embedding) AS similarity
            FROM document_chunks c
            JOIN documents d ON d.id = c.document_id
            {whereSql}
            ORDER BY c.embedding <=> @query_embedding
            LIMIT @top_k
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        var results = new List<ScoredChunk>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            var documentId = reader.GetGuid(1);
            var text = reader.GetString(2);
            var position = reader.GetInt32(3);
            var sectionTitle = reader.IsDBNull(4) ? null : reader.GetString(4);
            var embeddingModelId = reader.GetString(5);
            var similarity = reader.GetDouble(6);

            var chunk = DocumentChunk.Reconstitute(id, documentId, text, position, sectionTitle);
            results.Add(new ScoredChunk(chunk, similarity, embeddingModelId));
        }

        activity?.SetTag("db.row_count", results.Count);

        return results;
    }

    private static string? SerializeExtractedMetadata(DocumentMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var payload = new { tags = metadata.Tags, citedEntities = metadata.CitedEntities, extractedTitle = metadata.ExtractedTitle };
        return JsonSerializer.Serialize(payload);
    }
}
