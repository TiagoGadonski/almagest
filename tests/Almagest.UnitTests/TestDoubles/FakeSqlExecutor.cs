using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeSqlExecutor(QueryResultSet? result = null) : ISqlExecutor
{
    public string? LastSql { get; private set; }

    public Task<QueryResultSet> ExecuteAsync(string validatedSql, CancellationToken cancellationToken = default)
    {
        LastSql = validatedSql;
        return Task.FromResult(result ?? new QueryResultSet([], []));
    }
}
