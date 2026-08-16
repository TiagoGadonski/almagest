using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeSqlValidator(SqlValidationResult? result = null) : ISqlValidator
{
    public string? LastSql { get; private set; }

    public SqlValidationResult Validate(string sql)
    {
        LastSql = sql;
        return result ?? new SqlValidationResult(true, sql, null);
    }
}
