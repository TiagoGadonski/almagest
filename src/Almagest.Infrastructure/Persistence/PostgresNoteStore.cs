using Almagest.Application.Ports;
using Almagest.Domain;
using Npgsql;

namespace Almagest.Infrastructure.Persistence;

public sealed class PostgresNoteStore : INoteStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresNoteStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveAsync(Note note, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO notes (id, content, created_at)
            VALUES (@id, @content, @created_at)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", note.Id);
        command.Parameters.AddWithValue("content", note.Content);
        command.Parameters.AddWithValue("created_at", note.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
