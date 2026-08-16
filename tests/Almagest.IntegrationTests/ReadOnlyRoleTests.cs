using Npgsql;

namespace Almagest.IntegrationTests;

// Automates the manual `psql` verification done by hand in Phase 3: the
// almagest_readonly role can read the allowlisted tables and is refused,
// by Postgres itself, on anything else -- independent of the application's
// own allowlist check (PgAstSqlValidator).
[Collection(PostgresCollection.Name)]
public class ReadOnlyRoleTests
{
    private readonly PostgresFixture _fixture;

    public ReadOnlyRoleTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AlmagestReadonlyRole_CanSelectContacts_ButNotSessions()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var setRole = new NpgsqlCommand("SET LOCAL ROLE almagest_readonly", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using (var selectContacts = new NpgsqlCommand("SELECT id FROM contacts LIMIT 1", connection, transaction))
        await using (var reader = await selectContacts.ExecuteReaderAsync())
        {
            // Reaching here without an exception is the assertion: the role
            // is genuinely allowed to read this table.
        }

        await using var selectSessions = new NpgsqlCommand("SELECT id FROM sessions LIMIT 1", connection, transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => selectSessions.ExecuteReaderAsync());
        Assert.Equal("42501", exception.SqlState); // insufficient_privilege

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AlmagestReadonlyRole_CannotInsertIntoAnAllowlistedTable()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var setRole = new NpgsqlCommand("SET LOCAL ROLE almagest_readonly", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand("INSERT INTO contacts (name) VALUES ('should not work')", connection, transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", exception.SqlState);

        await transaction.RollbackAsync();
    }
}
