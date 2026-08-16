using Almagest.Application.Ports;
using Almagest.Infrastructure.Telemetry;
using Npgsql;

namespace Almagest.Infrastructure.Sql;

// Layers 4-5 of the phase doc's security design (3.4): a database role that
// structurally cannot do anything but SELECT the allowlisted tables, and a
// bounded blast radius (timeout, always-ROLLBACK) if a query slips past
// every earlier check. Both restrictions are set with SET LOCAL, inside the
// transaction, so neither can outlive it or leak into a later query reusing
// the same pooled connection (phase doc 3.7).
public sealed class PostgresReadOnlySqlExecutor : ISqlExecutor
{
    private const string ReadOnlyRole = "almagest_readonly";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan _statementTimeout;

    public PostgresReadOnlySqlExecutor(NpgsqlDataSource dataSource, TimeSpan statementTimeout)
    {
        _dataSource = dataSource;
        _statementTimeout = statementTimeout;
    }

    public async Task<QueryResultSet> ExecuteAsync(string validatedSql, CancellationToken cancellationToken = default)
    {
        using var activity = AlmagestTelemetry.ActivitySource.StartActivity("db.sql_executor.execute");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE {ReadOnlyRole}", connection, transaction))
            {
                await setRole.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var timeoutMs = (int)_statementTimeout.TotalMilliseconds;
            await using (var setTimeout = new NpgsqlCommand($"SET LOCAL statement_timeout = {timeoutMs}", connection, transaction))
            {
                await setTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var queryCommand = new NpgsqlCommand(validatedSql, connection, transaction);
            await using var reader = await queryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<object?>>();

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            activity?.SetTag("db.row_count", rows.Count);

            return new QueryResultSet(columnNames, rows);
        }
        finally
        {
            // Always rollback, even though every allowed statement is
            // read-only -- one more independent guarantee that nothing
            // persists even if a data-modifying statement somehow reached
            // execution (phase doc 3.4, Layer 5).
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
