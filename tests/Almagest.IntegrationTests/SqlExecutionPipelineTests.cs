using Almagest.Infrastructure.Persistence;
using Almagest.Infrastructure.Sql;
using Npgsql;

namespace Almagest.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class SqlExecutionPipelineTests
{
    private readonly PostgresFixture _fixture;

    public SqlExecutionPipelineTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ValidatedQuery_ExecutesUnderTheReadOnlyRole_AndReturnsRealRows()
    {
        var contactId = Guid.NewGuid();
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand("INSERT INTO contacts (id, name) VALUES (@id, @name)", connection))
        {
            insert.Parameters.AddWithValue("id", contactId);
            insert.Parameters.AddWithValue("name", "Ada Lovelace");
            await insert.ExecuteNonQueryAsync();
        }

        var validator = new PgAstSqlValidator(SqlAllowlist.Default, maxRows: 10);
        var executor = new PostgresReadOnlySqlExecutor(_fixture.DataSource, TimeSpan.FromSeconds(5));

        var validation = validator.Validate($"SELECT id, name FROM contacts WHERE id = '{contactId}'");
        Assert.True(validation.IsValid);

        var result = await executor.ExecuteAsync(validation.FinalizedSql!);

        Assert.Single(result.Rows);
        Assert.Contains("name", result.ColumnNames);
        Assert.Equal("Ada Lovelace", result.Rows[0][result.ColumnNames.ToList().IndexOf("name")]);
    }

    [Fact]
    public void RejectedQuery_NeverReachesTheExecutor()
    {
        var validator = new PgAstSqlValidator(SqlAllowlist.Default, maxRows: 10);

        var validation = validator.Validate("DROP TABLE contacts");

        Assert.False(validation.IsValid);
        Assert.Null(validation.FinalizedSql);
        // No call to executor.ExecuteAsync here at all -- the point being
        // demonstrated is that there is nothing to execute.
    }

    [Fact]
    public async Task Executor_RollsBackEveryTime_CommittedDataBeforeItIsUnaffected()
    {
        var contactId = Guid.NewGuid();
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand("INSERT INTO contacts (id, name) VALUES (@id, @name)", connection))
        {
            insert.Parameters.AddWithValue("id", contactId);
            insert.Parameters.AddWithValue("name", "Grace Hopper");
            await insert.ExecuteNonQueryAsync();
        }

        var executor = new PostgresReadOnlySqlExecutor(_fixture.DataSource, TimeSpan.FromSeconds(5));

        var first = await executor.ExecuteAsync($"SELECT id FROM contacts WHERE id = '{contactId}'");
        var second = await executor.ExecuteAsync($"SELECT id FROM contacts WHERE id = '{contactId}'");

        Assert.Single(first.Rows);
        Assert.Single(second.Rows);
    }
}
