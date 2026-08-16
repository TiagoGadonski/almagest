using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeSqlGenerator(SqlGenerationResult? result = null) : ISqlGenerator
{
    public string? LastQuestion { get; private set; }

    public Task<SqlGenerationResult> GenerateAsync(string question, SchemaDescription schema, CancellationToken cancellationToken = default)
    {
        LastQuestion = question;
        return Task.FromResult(result ?? new SqlGenerationResult(false, null));
    }
}
