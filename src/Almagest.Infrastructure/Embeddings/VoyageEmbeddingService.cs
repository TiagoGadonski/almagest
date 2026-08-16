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
    private const int CharsPerTokenEstimate = 4; // same rough estimate already used in AskQuestionUseCase/ChatUseCase

    // Conservative default: the lowest per-request token ceiling across every
    // current Voyage embedding model (120K, for voyage-4-large/voyage-3-large/
    // voyage-code-3/voyage-finance-2/voyage-law-2 -- confirmed against
    // Voyage's published API reference, not guessed). voyage-4/voyage-3.5/
    // voyage-2 allow 320K and the -lite variants allow 1M; a caller running
    // one of those models can raise maxTokensPerBatch via configuration.
    // 1,000 texts per request is the uniform batch-size cap across all
    // current models.
    public const int DefaultMaxTokensPerBatch = 120_000;
    public const int DefaultMaxTextsPerBatch = 1_000;

    private readonly HttpClient _httpClient;
    private readonly EmbeddingGeneratorMetadata _metadata;
    private readonly int _maxTokensPerBatch;
    private readonly int _maxTextsPerBatch;

    public VoyageEmbeddingService(
        HttpClient httpClient,
        string modelId,
        int maxTokensPerBatch = DefaultMaxTokensPerBatch,
        int maxTextsPerBatch = DefaultMaxTextsPerBatch)
    {
        _httpClient = httpClient;
        ModelId = modelId;
        _maxTokensPerBatch = maxTokensPerBatch;
        _maxTextsPerBatch = maxTextsPerBatch;
        _metadata = new EmbeddingGeneratorMetadata("VoyageEmbeddingService", httpClient.BaseAddress, modelId);
    }

    public string ModelId { get; }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default)
    {
        var generated = await EmbedInternalAsync(texts, ToInputType(purpose), cancellationToken).ConfigureAwait(false);
        return generated.Select(embedding => embedding.Vector.ToArray()).ToList();
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values as IReadOnlyList<string> ?? values.ToList();

        // Microsoft.Extensions.AI's IEmbeddingGenerator has no concept of
        // embedding purpose -- nothing in this project's own code calls this
        // method today (it's registered in DI for generic MEAI-based
        // tooling), so it defaults to "document", matching this class's
        // behavior before EmbeddingPurpose existed.
        return await EmbedInternalAsync(inputs, ToInputType(EmbeddingPurpose.Document), cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeneratedEmbeddings<Embedding<float>>> EmbedInternalAsync(
        IReadOnlyList<string> inputs, string inputType, CancellationToken cancellationToken)
    {
        using var activity = AlmagestTelemetry.ActivitySource.StartActivity("voyage.embed");
        activity?.SetTag("llm.model", ModelId);
        activity?.SetTag("llm.input_count", inputs.Count);
        activity?.SetTag("voyage.input_type", inputType);

        if (inputs.Count == 0)
        {
            return new GeneratedEmbeddings<Embedding<float>>();
        }

        var vectors = new float[inputs.Count][];
        var totalTokens = 0;

        foreach (var batch in PartitionIntoBatches(inputs))
        {
            var request = new VoyageEmbeddingRequest(batch.Texts, ModelId, inputType, OutputDimension, "float", true);
            var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);

            var batchVectors = response.Vectors;

            // A silent count mismatch would pair each chunk with the wrong
            // vector and poison retrieval without anything failing, so it
            // fails loudly.
            if (batchVectors.Count != batch.Texts.Count)
            {
                throw new InvalidOperationException(
                    $"Voyage AI returned {batchVectors.Count} embeddings for {batch.Texts.Count} inputs.");
            }

            for (var i = 0; i < batchVectors.Count; i++)
            {
                vectors[batch.StartIndex + i] = batchVectors[i];
            }

            totalTokens += response.TotalTokens;
        }

        activity?.SetTag("llm.input_tokens", totalTokens);
        activity?.SetTag(
            "llm.estimated_cost_usd",
            AlmagestTelemetry.EstimateCostUsd(totalTokens, AlmagestTelemetry.Pricing.VoyageCostPerMillionTokensUsd));

        var embeddings = vectors.Select(vector => new Embedding<float>(vector)).ToList();

        return new GeneratedEmbeddings<Embedding<float>>(embeddings)
        {
            Usage = new UsageDetails { InputTokenCount = totalTokens },
        };
    }

    // Splits inputs so each HTTP request stays under both of Voyage's
    // per-request ceilings (text count and estimated tokens). A single text
    // that alone exceeds the token ceiling still gets sent in its own
    // batch -- Voyage rejects it with its own error rather than this class
    // silently truncating content the caller asked to embed.
    private IEnumerable<(IReadOnlyList<string> Texts, int StartIndex)> PartitionIntoBatches(IReadOnlyList<string> inputs)
    {
        var batch = new List<string>();
        var batchStartIndex = 0;
        var batchTokens = 0;

        for (var i = 0; i < inputs.Count; i++)
        {
            var estimatedTokens = Math.Max(1, inputs[i].Length / CharsPerTokenEstimate);

            var wouldOverflow = batch.Count > 0
                && (batch.Count >= _maxTextsPerBatch || batchTokens + estimatedTokens > _maxTokensPerBatch);

            if (wouldOverflow)
            {
                yield return (batch, batchStartIndex);
                batch = [];
                batchStartIndex = i;
                batchTokens = 0;
            }

            batch.Add(inputs[i]);
            batchTokens += estimatedTokens;
        }

        if (batch.Count > 0)
        {
            yield return (batch, batchStartIndex);
        }
    }

    private static string ToInputType(EmbeddingPurpose purpose) => purpose switch
    {
        EmbeddingPurpose.Document => "document",
        EmbeddingPurpose.Query => "query",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown embedding purpose."),
    };

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

            // Credentials are either valid or they aren't -- retrying with the
            // same bad key cannot succeed, so this fails immediately rather
            // than burning MaxAttempts. Mapped to a typed exception (not
            // generic HttpRequestException) so the API layer can tell a
            // server misconfiguration apart from every other failure.
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new EmbeddingProviderException(
                    EmbeddingProviderErrorKind.InvalidCredentials,
                    "Voyage AI rejected the configured API key.");
            }

            var isTransient = httpResponse.StatusCode == HttpStatusCode.TooManyRequests || (int)httpResponse.StatusCode >= 500;
            if (!isTransient || attempt >= MaxAttempts)
            {
                if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new EmbeddingProviderException(
                        EmbeddingProviderErrorKind.RateLimited,
                        $"Voyage AI rate limit exceeded after {attempt} attempt(s).",
                        httpResponse.Headers.RetryAfter?.Delta);
                }

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

    // Voyage returns an OpenAI-compatible envelope: the vectors live under
    // "data", one object per input, and token usage is nested under "usage".
    private sealed record VoyageEmbeddingResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<VoyageEmbeddingData>? Data,
        [property: JsonPropertyName("usage")] VoyageUsage? Usage)
    {
        // Ordering by the reported index is deliberate: response order is not
        // contractually guaranteed, and a reordered response would silently
        // attach the wrong vector to each chunk.
        public IReadOnlyList<float[]> Vectors =>
            (Data ?? [])
                .OrderBy(item => item.Index)
                .Select(item => item.Embedding)
                .ToList();

        public int TotalTokens => Usage?.TotalTokens ?? 0;
    }

    private sealed record VoyageEmbeddingData(
        [property: JsonPropertyName("embedding")] float[] Embedding,
        [property: JsonPropertyName("index")] int Index);

    private sealed record VoyageUsage(
        [property: JsonPropertyName("total_tokens")] int TotalTokens);
}
