using Almagest.Application.Ports;
using Almagest.Domain;
using Npgsql;

namespace Almagest.Infrastructure.Persistence;

public sealed class PostgresReminderStore : IReminderStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresReminderStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO reminders (id, message, remind_at, created_at)
            VALUES (@id, @message, @remind_at, @created_at)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", reminder.Id);
        command.Parameters.AddWithValue("message", reminder.Message);
        command.Parameters.AddWithValue("remind_at", reminder.RemindAt);
        command.Parameters.AddWithValue("created_at", reminder.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
