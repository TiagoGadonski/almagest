using Almagest.Application.Ports;
using Npgsql;

namespace Almagest.Infrastructure.Sql;

// Introspects information_schema, but filtered to the allowlist before the
// result ever reaches a prompt -- the model never learns a non-allowlisted
// table exists (phase doc 3.3).
public sealed class PostgresSchemaProvider : ISchemaProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SqlAllowlist _allowlist;

    public PostgresSchemaProvider(NpgsqlDataSource dataSource, SqlAllowlist allowlist)
    {
        _dataSource = dataSource;
        _allowlist = allowlist;
    }

    public async Task<SchemaDescription> GetSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT table_name, column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = ANY(@table_names)
            ORDER BY table_name, ordinal_position
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("table_names", _allowlist.TableColumns.Keys.ToArray());

        var tables = new Dictionary<string, List<ColumnDescription>>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);

            // information_schema lists every column regardless of grants;
            // filter again here so the allowlist is enforced even if this
            // query's WHERE clause were ever loosened.
            if (!_allowlist.IsColumnAllowed(tableName, columnName))
            {
                continue;
            }

            if (!tables.TryGetValue(tableName, out var columns))
            {
                columns = [];
                tables[tableName] = columns;
            }

            columns.Add(new ColumnDescription(columnName, reader.GetString(2), reader.GetString(3) == "YES"));
        }

        var tableDescriptions = tables.Select(kvp => new TableDescription(kvp.Key, kvp.Value)).ToList();
        return new SchemaDescription(tableDescriptions);
    }
}
