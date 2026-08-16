using Almagest.Application.Ports;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Almagest.Infrastructure.Embeddings;

// Local, no-network alternative to VoyageEmbeddingService, implementing the
// same IEmbeddingService port (Phase 1) -- exists so tests and CI don't
// need a Voyage API key (Phase 5). Not a production substitute:
// document_chunks.embedding is sized VECTOR(1024) for Voyage's output; this
// model produces 384 dimensions and is used only against the isolated,
// disposable databases Testcontainers creates.
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private const string InputIdsName = "input_ids";
    private const string AttentionMaskName = "attention_mask";
    private const string TokenTypeIdsName = "token_type_ids";
    private const string OutputName = "last_hidden_state";

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;

    public string ModelId { get; }

    public OnnxEmbeddingService(string modelPath, string vocabPath, string modelId = "all-MiniLM-L6-v2")
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = BertTokenizer.Create(vocabPath);
        ModelId = modelId;
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<float[]> embeddings = texts.Select(Embed).ToList();
        return Task.FromResult(embeddings);
    }

    private float[] Embed(string text)
    {
        var ids = _tokenizer.EncodeToIds(text);
        var length = ids.Count;

        var inputIds = new DenseTensor<long>(new[] { 1, length });
        var attentionMask = new DenseTensor<long>(new[] { 1, length });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, length });

        for (var i = 0; i < length; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(AttentionMaskName, attentionMask),
            NamedOnnxValue.CreateFromTensor(TokenTypeIdsName, tokenTypeIds),
        };

        using var results = _session.Run(inputs);
        var hiddenState = results.First(r => r.Name == OutputName).AsTensor<float>();

        // No padding for a single, unbatched sentence -- every position is a
        // real token, so a plain mean over the sequence dimension already
        // matches masked-mean-pooling (attention_mask would be all 1s here).
        return MeanPoolAndNormalize(hiddenState, length);
    }

    private static float[] MeanPoolAndNormalize(Tensor<float> hiddenState, int sequenceLength)
    {
        var hiddenSize = hiddenState.Dimensions[2];
        var pooled = new float[hiddenSize];

        for (var position = 0; position < sequenceLength; position++)
        {
            for (var dim = 0; dim < hiddenSize; dim++)
            {
                pooled[dim] += hiddenState[0, position, dim];
            }
        }

        for (var dim = 0; dim < hiddenSize; dim++)
        {
            pooled[dim] /= sequenceLength;
        }

        var norm = MathF.Sqrt(pooled.Sum(value => value * value));
        if (norm > 0)
        {
            for (var dim = 0; dim < hiddenSize; dim++)
            {
                pooled[dim] /= norm;
            }
        }

        return pooled;
    }

    public void Dispose() => _session.Dispose();
}
