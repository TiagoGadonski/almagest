using Npgsql;

namespace Almagest.Infrastructure.Persistence;

// Fly Postgres (via `fly postgres attach`) injects DATABASE_URL in URI form
// -- postgres://user:pass@host:port/db?sslmode=... -- but Npgsql has never
// accepted that format directly: a feature request for it
// (npgsql/npgsql#2090) has been open since 2018 with no resolution as of
// this writing. Translated here so `fly postgres attach` -> the app
// connecting is a straight line, no manual reformatting step in between.
public static class PostgresConnectionStringTranslator
{
    private const int DefaultPort = 5432;

    public static string FromDatabaseUrl(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);

        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port == -1 ? DefaultPort : uri.Port,
            Username = username,
            Password = password,
            Database = uri.AbsolutePath.TrimStart('/'),
        };

        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("sslmode", out var sslModeValue) && TryMapSslMode(sslModeValue, out var sslMode))
        {
            builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }

    private static bool TryMapSslMode(string value, out SslMode sslMode)
    {
        switch (value.ToLowerInvariant())
        {
            case "disable": sslMode = SslMode.Disable; return true;
            case "allow": sslMode = SslMode.Allow; return true;
            case "prefer": sslMode = SslMode.Prefer; return true;
            case "require": sslMode = SslMode.Require; return true;
            case "verify-ca": sslMode = SslMode.VerifyCA; return true;
            case "verify-full": sslMode = SslMode.VerifyFull; return true;
            // Unrecognized value: leave Npgsql's own default in place rather
            // than guess at a mapping, or throw and take the app down over
            // a query parameter it doesn't strictly need to understand.
            default: sslMode = default; return false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return result;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }
}
