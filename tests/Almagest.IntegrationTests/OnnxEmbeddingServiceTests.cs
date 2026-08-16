using Almagest.Infrastructure.Embeddings;

namespace Almagest.IntegrationTests;

// Depends on the model files scripts/download-onnx-model.sh fetches --
// skipped, not failed, when they aren't present (e.g. a machine that hasn't
// run the setup script yet), rather than blocking the rest of the suite.
public class OnnxEmbeddingServiceTests
{
    private static readonly string ModelPath = Path.Combine(FindRepoRoot(), "models", "all-MiniLM-L6-v2", "model.onnx");
    private static readonly string VocabPath = Path.Combine(FindRepoRoot(), "models", "all-MiniLM-L6-v2", "vocab.txt");

    [SkippableFact]
    public async Task EmbedAsync_SimilarSentences_ScoreHigherThanUnrelatedOnes()
    {
        Skip.IfNot(File.Exists(ModelPath) && File.Exists(VocabPath), "ONNX model not downloaded -- run scripts/download-onnx-model.sh.");

        using var service = new OnnxEmbeddingService(ModelPath, VocabPath);

        var embeddings = await service.EmbedAsync(
        [
            "The cat sat on the mat.",
            "A feline rested on the rug.",
            "The stock market fell sharply today.",
        ]);

        var catToFeline = CosineSimilarity(embeddings[0], embeddings[1]);
        var catToStocks = CosineSimilarity(embeddings[0], embeddings[2]);

        Assert.True(
            catToFeline > catToStocks,
            $"Expected semantically similar sentences to score higher (cat/feline: {catToFeline:F3}, cat/stocks: {catToStocks:F3}).");
    }

    [SkippableFact]
    public async Task EmbedAsync_ReturnsThreeHundredEightyFourDimensions()
    {
        Skip.IfNot(File.Exists(ModelPath) && File.Exists(VocabPath), "ONNX model not downloaded -- run scripts/download-onnx-model.sh.");

        using var service = new OnnxEmbeddingService(ModelPath, VocabPath);

        var embeddings = await service.EmbedAsync(["a short sentence"]);

        Assert.Equal(384, embeddings[0].Length);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        // Both vectors are already L2-normalized by OnnxEmbeddingService, so
        // the dot product alone is the cosine similarity.
        return dot;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Almagest.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not locate repo root (Almagest.sln not found).");
    }
}
