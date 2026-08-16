namespace Almagest.Application.Ports;

public interface ISqlExecutor
{
    Task<QueryResultSet> ExecuteAsync(string validatedSql, CancellationToken cancellationToken = default);
}

public sealed record QueryResultSet(IReadOnlyList<string> ColumnNames, IReadOnlyList<IReadOnlyList<object?>> Rows);
