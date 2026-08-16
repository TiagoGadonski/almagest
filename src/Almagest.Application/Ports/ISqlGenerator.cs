namespace Almagest.Application.Ports;

public interface ISqlGenerator
{
    Task<SqlGenerationResult> GenerateAsync(string question, SchemaDescription schema, CancellationToken cancellationToken = default);
}

public sealed record SqlGenerationResult(bool Succeeded, string? Sql);
