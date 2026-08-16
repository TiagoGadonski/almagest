using System.Net;
using System.Text.Json;
using Almagest.Application.Ports;
using Almagest.Infrastructure.Embeddings;

namespace Almagest.UnitTests.Embeddings;

public class VoyageEmbeddingServiceTests
{
    [Fact]
    public async Task EmbedAsync_DocumentPurpose_SendsDocumentInputType()
    {
        var handler = new StubHttpMessageHandler(SuccessResponse);
        var service = CreateService(handler);

        await service.EmbedAsync(["hello"], EmbeddingPurpose.Document);

        Assert.Single(handler.RequestBodies);
        Assert.Equal("document", ReadInputType(handler.RequestBodies[0]));
    }

    [Fact]
    public async Task EmbedAsync_QueryPurpose_SendsQueryInputType()
    {
        var handler = new StubHttpMessageHandler(SuccessResponse);
        var service = CreateService(handler);

        await service.EmbedAsync(["what is an emergency fund"], EmbeddingPurpose.Query);

        Assert.Single(handler.RequestBodies);
        Assert.Equal("query", ReadInputType(handler.RequestBodies[0]));
    }

    [Fact]
    public async Task GenerateAsync_MicrosoftExtensionsAIInterface_DefaultsToDocumentInputType()
    {
        // No EmbeddingPurpose exists on Microsoft.Extensions.AI's
        // IEmbeddingGenerator -- this locks in the documented default so a
        // future change to that fallback doesn't go unnoticed.
        var handler = new StubHttpMessageHandler(SuccessResponse);
        var service = CreateService(handler);

        await service.GenerateAsync(["hello"]);

        Assert.Equal("document", ReadInputType(handler.RequestBodies[0]));
    }

    [Fact]
    public async Task EmbedAsync_MoreTextsThanBatchLimit_PartitionsAcrossMultipleRequests_PreservingOrder()
    {
        var handler = new StubHttpMessageHandler(SuccessResponse);
        var service = CreateService(handler, maxTextsPerBatch: 2);

        var texts = new[] { "a", "bb", "ccc", "dddd", "eeeee" };
        var embeddings = await service.EmbedAsync(texts, EmbeddingPurpose.Document);

        Assert.Equal(3, handler.RequestBodies.Count); // [a,bb] [ccc,dddd] [eeeee]
        for (var i = 0; i < texts.Length; i++)
        {
            // The stub echoes each text's length back as embeddings[i][0] --
            // a mismatch here would mean batching's StartIndex-based
            // reassembly scrambled results across request boundaries.
            Assert.Equal(texts[i].Length, (int)embeddings[i][0]);
        }
    }

    [Fact]
    public async Task EmbedAsync_ProviderReturns401_ThrowsInvalidCredentialsWithoutRetrying()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var service = CreateService(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => service.EmbedAsync(["hello"], EmbeddingPurpose.Document));

        Assert.Equal(EmbeddingProviderErrorKind.InvalidCredentials, ex.Kind);
        Assert.Equal(1, handler.CallCount); // no point retrying a bad key
    }

    [Fact]
    public async Task EmbedAsync_ProviderReturns429Repeatedly_ThrowsRateLimitedAfterExhaustingRetries()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var service = CreateService(handler);

        var ex = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => service.EmbedAsync(["hello"], EmbeddingPurpose.Document));

        Assert.Equal(EmbeddingProviderErrorKind.RateLimited, ex.Kind);
        Assert.Equal(5, handler.CallCount); // MaxAttempts
    }

    private static VoyageEmbeddingService CreateService(
        HttpMessageHandler handler, int maxTextsPerBatch = VoyageEmbeddingService.DefaultMaxTextsPerBatch)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.voyageai.com/") };
        return new VoyageEmbeddingService(
            httpClient, "voyage-4", VoyageEmbeddingService.DefaultMaxTokensPerBatch, maxTextsPerBatch);
    }

    private static string ReadInputType(string requestBody) =>
        JsonDocument.Parse(requestBody).RootElement.GetProperty("input_type").GetString()!;

    // Echoes each input text's length back as that embedding's single
    // component -- enough to assert ordering without a real model.
    private static HttpResponseMessage SuccessResponse(HttpRequestMessage _, string requestBody)
    {
        var inputs = JsonDocument.Parse(requestBody).RootElement.GetProperty("input")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        var data = inputs.Select((text, index) => new { embedding = new[] { (float)text.Length }, index }).ToList();
        var payload = JsonSerializer.Serialize(new { data, usage = new { total_tokens = inputs.Sum(t => t.Length) } });

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            RequestBodies.Add(body);
            return respond(request, body);
        }
    }
}
