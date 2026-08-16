using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Almagest.Application.Ports;
using Almagest.Infrastructure.Telemetry;
using Microsoft.Extensions.AI;

namespace Almagest.Infrastructure.Embeddings;

// Hand-written HttpClient adapter -- Voyage AI ships no official .NET SDK.
// Implements Microsoft.Extensions.AI's IEmbeddingGenerator (the abstraction
// the phase doc calls for) and the Application-owned IEmbeddingService port
// side by side, so no Microsoft.Extensions.AI concrete type needs to cross
// into use case code.
//
// Expects an HttpClient already configured by the DI registration with
// BaseAddress = https://api.voyageai.com/ and an Authorization: Bearer
// header carrying VOYAGE_API_KEY -- reading that secret is a composition
// root concern, not this class's.
public sealed class VoyageEmbeddingService : IEmbeddingGenerator<string, Embedding<float>>, IEmbeddingService
{
    private const string EmbeddingsPath = "v1/embeddings";
    private const int OutputDimension = 1024;
    private const int MaxAttempts = 5;

    private readonly HttpClient _httpClient;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public VoyageEmbeddingService(HttpClient httpClient, string modelId)
    {
        _httpClient = httpClient;
        ModelId = modelId;
        _metadata = new EmbeddingGeneratorMetadata("VoyageEmbeddingService", httpClient.BaseAddress, modelId);
    }

    public string ModelId { get; }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var generated = await GenerateAsync(texts, options: null, cancellationToken).ConfigureAwait(false);
        return generated.Select(embedding => embedding.Vector.ToArray()).ToList();
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = AlmagestTelemetry.ActivitySource.StartActivity("voyage.embed");
        activity?.SetTag("llm.model", ModelId);

        var inputs = values as IReadOnlyList<string> ?? values.ToList();
        activity?.SetTag("llm.input_count", inputs.Count);

        if (inputs.Count == 0)
        {
            return new GeneratedEmbeddings<Embedding<float>>();
        }

        var request = new VoyageEmbeddingRequest(inputs, ModelId, "document", OutputDimension, "float", true);
        var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);

        activity?.SetTag("llm.input_tokens", response.TotalTokens);
        activity?.SetTag(
            "llm.estimated_cost_usd",
            AlmagestTelemetry.EstimateCostUsd(response.TotalTokens, AlmagestTelemetry.Pricing.VoyageCostPerMillionTokensUsd));

        var embeddings = response.Embeddings.Select(vector => new Embedding<float>(vector)).ToList();

        return new GeneratedEmbeddings<Embedding<float>>(embeddings)
        {
            Usage = new UsageDetails { InputTokenCount = response.TotalTokens },
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null
            ? null
            : serviceType == typeof(EmbeddingGeneratorMetadata)
                ? _metadata
                : serviceType?.IsInstanceOfType(this) is true
                    ? this
                    : null;

    public void Dispose()
    {
    }

    // Exponential backoff with jitter; honors Retry-After on 429. Distinguishes
    // transient failures (429, 5xx) from terminal ones (other 4xx) instead of
    // retrying everything blindly. Hand-rolled rather than pulling in
    // Polly/Microsoft.Extensions.Http.Resilience -- not justified for one HTTP
    // call shape, and this provider's adapter is hand-written already.
    private async Task<VoyageEmbeddingResponse> SendWithRetryAsync(VoyageEmbeddingRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EmbeddingsPath)
            {
                Content = JsonContent.Create(request),
            };

            using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (httpResponse.IsSuccessStatusCode)
            {
                return await httpResponse.Content.ReadFromJsonAsync<VoyageEmbeddingResponse>(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Voyage AI returned an empty embeddings response.");
            }

            var isTransient = httpResponse.StatusCode == HttpStatusCode.TooManyRequests || (int)httpResponse.StatusCode >= 500;
            if (!isTransient || attempt >= MaxAttempts)
            {
                var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Voyage AI embeddings request failed with {(int)httpResponse.StatusCode} {httpResponse.StatusCode}: {body}");
            }

            await Task.Delay(GetRetryDelay(httpResponse, attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        var backoff = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
        return backoff + jitter;
    }

    private sealed record VoyageEmbeddingRequest(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input_type")] string InputType,
        [property: JsonPropertyName("output_dimension")] int OutputDimension,
        [property: JsonPropertyName("output_dtype")] string OutputDtype,
        [property: JsonPropertyName("truncation")] bool Truncation);

    private sealed record VoyageEmbeddingResponse(
        [property: JsonPropertyName("embeddings")] IReadOnlyList<float[]> Embeddings,
        [property: JsonPropertyName("total_tokens")] int TotalTokens);
}
