namespace Almagest.Application.Ports;

public interface ISchemaProvider
{
    Task<SchemaDescription> GetSchemaAsync(CancellationToken cancellationToken = default);
}

public sealed record SchemaDescription(IReadOnlyList<TableDescription> Tables);

public sealed record TableDescription(string Name, IReadOnlyList<ColumnDescription> Columns);

public sealed record ColumnDescription(string Name, string DataType, bool IsNullable);
