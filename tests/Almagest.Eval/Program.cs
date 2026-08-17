using System.Net.Http.Headers;
using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.Eval;
using Almagest.Infrastructure.Chat;
using Almagest.Infrastructure.Embeddings;
using Almagest.Infrastructure.Persistence;
using Anthropic;
using Microsoft.Extensions.AI;
using Npgsql;

// Same composition root shape as Almagest.Api's Program.cs, minus API/HTTP
// hosting -- this is a console tool, run by hand (`dotnet run`), not a
// service. Secrets come from the environment, never a config file.

var connectionString = Environment.GetEnvironmentVariable("ALMAGEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("ALMAGEST_CONNECTION_STRING is not set.");

var voyageApiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
    ?? throw new InvalidOperationException("VOYAGE_API_KEY is not set.");
var voyageModel = Environment.GetEnvironmentVariable("VOYAGE_MODEL") ?? "voyage-4";

_ = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set. Anthropic.SDK reads it itself, validated here for a clear failure.");
var anthropicModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-haiku-4-5";

var questionsPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval", "questions.md"));

var questions = EvalQuestionParser.ParseFile(questionsPath);

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
await using var dataSource = dataSourceBuilder.Build();

IChunkStore chunkStore = new PgVectorChunkStore(dataSource);

using var voyageHttpClient = new HttpClient { BaseAddress = new Uri("https://api.voyageai.com/") };
voyageHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", voyageApiKey);
IEmbeddingService embeddingService = new VoyageEmbeddingService(voyageHttpClient, voyageModel);

var anthropic = new AnthropicClient();
IChatClient chatClient = anthropic
    .AsIChatClient(anthropicModel)
    .AsBuilder()
    .ConfigureOptions(options => options.MaxOutputTokens ??= 1024)
    .Build();
IChatService chatService = new ClaudeChatService(chatClient);

// SimilarityFloor: 0.45, matching src/Almagest.Api/Program.cs -- this was
// still the original 0.70 guess here, never updated after the real
// calibration (see that file's comment and README "How the similarity
// floor was calibrated"). With 0.70, AskQuestionUseCase's own floor filter
// rejected every real match before generation, independent of recall@5
// (which reads raw IChunkStore.SearchAsync results and never applies this
// floor) -- the actual cause of a 100% recall@5 / 7% accuracy run.
var askQuestionUseCase = new AskQuestionUseCase(
    embeddingService, chunkStore, chatService, new RetrievalOptions(TopK: 5, SimilarityFloor: 0.45, MaxContextTokens: 4000));

DocumentTitleLookup lookupDocumentTitles = async (documentIds, cancellationToken) =>
{
    if (documentIds.Count == 0)
    {
        return new Dictionary<Guid, string>();
    }

    await using var command = dataSource.CreateCommand("SELECT id, title FROM documents WHERE id = ANY(@ids)");
    command.Parameters.AddWithValue("ids", documentIds.ToArray());

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var titles = new Dictionary<Guid, string>();
    while (await reader.ReadAsync(cancellationToken))
    {
        titles[reader.GetGuid(0)] = reader.GetString(1);
    }

    return titles;
};

const int topK = 5;

// Default 25s: Voyage's free tier is 3 requests/minute, so anything faster
// than ~20s between questions (each of which calls both Voyage and Claude)
// eventually 429s. See tests/eval/questions.md for the rationale.
var delayMs = int.TryParse(Environment.GetEnvironmentVariable("ALMAGEST_EVAL_DELAY_MS"), out var parsedDelayMs)
    ? parsedDelayMs
    : 25_000;

var report = await EvalRunner.RunAsync(
    questions,
    askQuestionUseCase,
    embeddingService,
    chunkStore,
    lookupDocumentTitles,
    topK,
    TimeSpan.FromMilliseconds(delayMs),
    logProgress: message => Console.Error.WriteLine(message));

EvalReportPrinter.Print(report, topK);
