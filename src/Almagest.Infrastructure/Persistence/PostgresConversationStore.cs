using Almagest.Application.Ports;
using Almagest.Domain;
using Npgsql;

namespace Almagest.Infrastructure.Persistence;

public sealed class PostgresConversationStore : IConversationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresConversationStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Session?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, summary, summarized_through_position, created_at, last_active_at
            FROM sessions
            WHERE id = @id
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var summary = reader.IsDBNull(1) ? null : reader.GetString(1);

        return Session.Reconstitute(
            reader.GetGuid(0),
            summary,
            reader.GetInt32(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO sessions (id, summary, summarized_through_position, created_at, last_active_at)
            VALUES (@id, @summary, @summarized_through_position, @created_at, @last_active_at)
            ON CONFLICT (id) DO UPDATE SET
                summary = EXCLUDED.summary,
                summarized_through_position = EXCLUDED.summarized_through_position,
                last_active_at = EXCLUDED.last_active_at
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", session.Id);
        command.Parameters.AddWithValue("summary", (object?)session.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("summarized_through_position", session.SummarizedThroughPosition);
        command.Parameters.AddWithValue("created_at", session.CreatedAt);
        command.Parameters.AddWithValue("last_active_at", session.LastActiveAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(
        Guid sessionId, int fromPositionExclusive, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, session_id, role, content, message_position, created_at
            FROM messages
            WHERE session_id = @session_id AND message_position > @from_position
            ORDER BY message_position
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("from_position", fromPositionExclusive);

        var results = new List<Message>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var role = ParseRole(reader.GetString(2));
            results.Add(Message.Reconstitute(
                reader.GetGuid(0),
                reader.GetGuid(1),
                role,
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return results;
    }

    public async Task AppendMessageAsync(Message message, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO messages (id, session_id, role, content, message_position, created_at)
            VALUES (@id, @session_id, @role, @content, @message_position, @created_at)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", message.Id);
        command.Parameters.AddWithValue("session_id", message.SessionId);
        command.Parameters.AddWithValue("role", FormatRole(message.Role));
        command.Parameters.AddWithValue("content", message.Content);
        command.Parameters.AddWithValue("message_position", message.Position);
        command.Parameters.AddWithValue("created_at", message.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatRole(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role."),
    };

    private static MessageRole ParseRole(string role) => role switch
    {
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        "system" => MessageRole.System,
        _ => throw new InvalidOperationException($"Unknown message role '{role}' read from storage."),
    };
}
