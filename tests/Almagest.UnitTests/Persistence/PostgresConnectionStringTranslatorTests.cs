using Almagest.Infrastructure.Persistence;
using Npgsql;

namespace Almagest.UnitTests.Persistence;

public class PostgresConnectionStringTranslatorTests
{
    [Fact]
    public void FromDatabaseUrl_FullUrl_ExtractsEveryComponent()
    {
        var result = PostgresConnectionStringTranslator.FromDatabaseUrl(
            "postgres://almagest:s3cret@almagest-db.flycast:5432/almagest?sslmode=disable");

        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal("almagest-db.flycast", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal("almagest", builder.Username);
        Assert.Equal("s3cret", builder.Password);
        Assert.Equal("almagest", builder.Database);
        Assert.Equal(SslMode.Disable, builder.SslMode);
    }

    [Fact]
    public void FromDatabaseUrl_NoPortSpecified_DefaultsTo5432()
    {
        var result = PostgresConnectionStringTranslator.FromDatabaseUrl(
            "postgres://almagest:s3cret@almagest-db.flycast/almagest");

        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(5432, builder.Port);
    }

    [Fact]
    public void FromDatabaseUrl_NoSslModeInQuery_LeavesNpgsqlDefault()
    {
        var result = PostgresConnectionStringTranslator.FromDatabaseUrl(
            "postgres://almagest:s3cret@almagest-db.flycast:5432/almagest");

        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(new NpgsqlConnectionStringBuilder().SslMode, builder.SslMode);
    }

    [Fact]
    public void FromDatabaseUrl_UnrecognizedSslMode_IsIgnoredRatherThanThrowing()
    {
        var result = PostgresConnectionStringTranslator.FromDatabaseUrl(
            "postgres://almagest:s3cret@almagest-db.flycast:5432/almagest?sslmode=made-up-value");

        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(new NpgsqlConnectionStringBuilder().SslMode, builder.SslMode);
    }

    [Fact]
    public void FromDatabaseUrl_UrlEncodedPassword_IsUnescaped()
    {
        // A password containing '@' or ':' must be percent-encoded in the URI
        // (RFC 3986) -- %40 for '@' here -- or it would be misread as the
        // host/port separator instead of part of the password.
        var result = PostgresConnectionStringTranslator.FromDatabaseUrl(
            "postgres://almagest:p%40ss@almagest-db.flycast:5432/almagest");

        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal("p@ss", builder.Password);
    }

    [Fact]
    public void FromDatabaseUrl_MultipleQueryParameters_StillFindsSslMode()
    {
        var result = PostgresConnectionStringTranslator.FromDatabaseUrl(
            "postgres://almagest:s3cret@almagest-db.flycast:5432/almagest?connect_timeout=10&sslmode=require&application_name=almagest");

        var builder = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(SslMode.Require, builder.SslMode);
    }
}
