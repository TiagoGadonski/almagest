using Npgsql;
using Testcontainers.PostgreSql;

namespace Almagest.IntegrationTests;

// One real, ephemeral Postgres per test run (not per test) -- shared via
// ICollectionFixture so every integration test class pays the container
// startup cost once. The exact image docker-compose.yml already uses, with
// every migration in db/migrations/ applied for real, in order.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("almagest")
        .WithUsername("almagest")
        .WithPassword("almagest_dev")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_container.GetConnectionString());
        dataSourceBuilder.UseVector();
        DataSource = dataSourceBuilder.Build();

        var migrationsDirectory = Path.Combine(FindRepoRoot(), "db", "migrations");
        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.sql").OrderBy(path => path, StringComparer.Ordinal);

        await using var connection = await DataSource.OpenConnectionAsync();
        foreach (var file in migrationFiles)
        {
            var sql = await File.ReadAllTextAsync(file);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        // The vector type didn't exist when this DataSource first connected
        // (0001_init.sql's CREATE EXTENSION just created it) -- Npgsql's
        // type catalog was resolved before that and needs to be refreshed,
        // or every later command using a Pgvector.Vector parameter fails to
        // resolve the type. Production never hits this: there, migrations
        // run via docker-entrypoint-initdb.d before the app's own
        // NpgsqlDataSource ever connects, so the type already exists by the
        // time anything resolves it.
        await connection.ReloadTypesAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Almagest.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not locate repo root (Almagest.sln not found).");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
