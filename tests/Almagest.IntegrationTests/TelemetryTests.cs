using System.Diagnostics;
using Almagest.Application.Ports;
using Almagest.Domain;
using Almagest.Infrastructure.Persistence;
using Almagest.Infrastructure.Telemetry;

namespace Almagest.IntegrationTests;

// Verifies real spans are actually emitted for a database call -- using
// ActivityListener (the BCL API the OpenTelemetry SDK itself subscribes
// through) rather than standing up a full exporter pipeline just to prove
// instrumentation fires with the right name and tags.
[Collection(PostgresCollection.Name)]
public class TelemetryTests
{
    private readonly PostgresFixture _fixture;

    public TelemetryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ChunkStoreSearch_EmitsASpanWithRowCountTag()
    {
        var recorded = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AlmagestTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => recorded.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var store = new PgVectorChunkStore(_fixture.DataSource);
        var document = Document.Create($"Telemetry Test {Guid.NewGuid()}", DocumentSource.Markdown);
        var chunk = DocumentChunk.Create(document.Id, "some searchable text", 0, null);
        // Tagged with a document type unique to this test so it can never be
        // picked up by another test's untagged, unfiltered topK search
        // against the Postgres instance this fixture shares across the
        // whole collection (same isolation approach the document-type
        // filter test already relies on).
        var metadata = DocumentMetadata.Create(document.Id, documentType: "telemetry-test", null, null);
        await store.SaveAsync(document, [new EmbeddedChunk(chunk, BuildVector(), "test-model")], metadata);

        await store.SearchAsync(BuildVector(), topK: 5, new ChunkSearchFilter(DocumentType: "telemetry-test"));

        var searchSpan = recorded.SingleOrDefault(a => a.OperationName == "db.chunk_store.search");
        Assert.NotNull(searchSpan);
        Assert.Equal(5, searchSpan!.GetTagItem("db.top_k"));
        Assert.Equal(1, searchSpan.GetTagItem("db.row_count"));

        var saveSpan = recorded.SingleOrDefault(a => a.OperationName == "db.chunk_store.save");
        Assert.NotNull(saveSpan);
        Assert.Equal(1, saveSpan!.GetTagItem("db.chunk_count"));
    }

    private static float[] BuildVector()
    {
        var vector = new float[1024];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = i % 7 == 0 ? 1f : 0.1f;
        }

        return vector;
    }
}
