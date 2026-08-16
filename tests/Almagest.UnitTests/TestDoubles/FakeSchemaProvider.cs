using Almagest.Application.Ports;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeSchemaProvider(SchemaDescription? schema = null) : ISchemaProvider
{
    public Task<SchemaDescription> GetSchemaAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(schema ?? new SchemaDescription([]));
}
