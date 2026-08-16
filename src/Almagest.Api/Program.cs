using System.Net.Http.Headers;
using System.Text.Json;
using Almagest.Application.Ports;
using Almagest.Application.UseCases;
using Almagest.Domain;
using Almagest.Infrastructure.Agent;
using Almagest.Infrastructure.Chat;
using Almagest.Infrastructure.Chunking;
using Almagest.Infrastructure.Embeddings;
using Almagest.Infrastructure.Metadata;
using Almagest.Infrastructure.Parsing;
using Almagest.Infrastructure.Persistence;
using Almagest.Infrastructure.Sql;
using Almagest.Infrastructure.Telemetry;
using Anthropic;
using Microsoft.Extensions.AI;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Telemetry --------------------------------------------------------------
// Console exporter always on (cheap, useful for local verification without a
// collector). OTLP exporter only if a collector endpoint is actually
// configured -- otherwise it just retries against nothing on every export.

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Almagest.Api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(AlmagestTelemetry.SourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddConsoleExporter();

        if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 })
        {
            tracing.AddOtlpExporter();
        }
    });

// --- Composition root: environment-sourced configuration -------------------
// Secrets never live in appsettings.json.

var connectionString = Environment.GetEnvironmentVariable("ALMAGEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("ALMAGEST_CONNECTION_STRING is not set.");

var voyageApiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
    ?? throw new InvalidOperationException("VOYAGE_API_KEY is not set.");
var voyageModel = Environment.GetEnvironmentVariable("VOYAGE_MODEL") ?? "voyage-4";

_ = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set. Anthropic.SDK reads it itself, validated here for a clear failure.");
var anthropicModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-haiku-4-5";

// --- Data access -------------------------------------------------------

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
builder.Services.AddSingleton(dataSourceBuilder.Build());
builder.Services.AddSingleton<IChunkStore, PgVectorChunkStore>();
builder.Services.AddSingleton<IConversationStore, PostgresConversationStore>();

// --- Embeddings (Voyage AI) ---------------------------------------------

builder.Services.AddHttpClient("voyage", client =>
{
    client.BaseAddress = new Uri("https://api.voyageai.com/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", voyageApiKey);
});
builder.Services.AddSingleton(sp =>
    new VoyageEmbeddingService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("voyage"), voyageModel));
builder.Services.AddSingleton<IEmbeddingService>(sp => sp.GetRequiredService<VoyageEmbeddingService>());
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<VoyageEmbeddingService>());

// --- Chat (Claude, via the chain validated in Almagest.Lab) -------------
// One shared IChatClient -- ClaudeChatService and ClaudeMetadataExtractor
// both consume it rather than each opening their own Anthropic connection.

var anthropic = new AnthropicClient();
IChatClient chatClient = anthropic
    .AsIChatClient(anthropicModel)
    .AsBuilder()
    .ConfigureOptions(options => options.MaxOutputTokens ??= 1024)
    .Build();
builder.Services.AddSingleton(chatClient);

builder.Services.AddSingleton<IChatService>(sp => new ClaudeChatService(sp.GetRequiredService<IChatClient>()));
builder.Services.AddSingleton<IMetadataExtractor>(sp => new ClaudeMetadataExtractor(sp.GetRequiredService<IChatClient>()));
builder.Services.AddSingleton<ISqlGenerator>(sp => new ClaudeSqlGenerator(sp.GetRequiredService<IChatClient>()));
builder.Services.AddSingleton<IQueryRouter>(sp => new ClaudeQueryRouter(sp.GetRequiredService<IChatClient>()));

// --- Parsing and chunking ------------------------------------------------

builder.Services.AddSingleton<IDocumentParser, PdfDocumentParser>();
builder.Services.AddSingleton<IDocumentParser, MarkdownDocumentParser>();
builder.Services.AddSingleton<ITextChunker, RecursiveTextChunker>();

// Phase 1/2 defaults from docs/phases/ -- configuration, not constants
// buried in code; tune these against a fixed question set, not by editing them here.
builder.Services.AddSingleton(new ChunkingOptions(TargetTokens: 800, OverlapRatio: 0.12));
builder.Services.AddSingleton(new RetrievalOptions(TopK: 5, SimilarityFloor: 0.70, MaxContextTokens: 4000));
builder.Services.AddSingleton(new ConversationOptions(MaxActiveMessages: 20, RetainRecentMessages: 6));

// --- Text-to-SQL (Phase 3) -----------------------------------------------
// SqlAllowlist is the single source of truth shared by schema introspection
// and AST validation; it must stay in sync with the GRANTs in
// db/migrations/0003_text_to_sql.sql (the database-enforced copy).

var sqlAllowlist = SqlAllowlist.Default;
builder.Services.AddSingleton(sqlAllowlist);
builder.Services.AddSingleton<ISchemaProvider, PostgresSchemaProvider>();
builder.Services.AddSingleton<ISqlValidator>(_ => new PgAstSqlValidator(sqlAllowlist, maxRows: 200));
builder.Services.AddSingleton<ISqlExecutor>(sp =>
    new PostgresReadOnlySqlExecutor(sp.GetRequiredService<NpgsqlDataSource>(), TimeSpan.FromSeconds(5)));

// --- Agent (Phase 4) -------------------------------------------------------

builder.Services.AddSingleton<INoteStore, PostgresNoteStore>();
builder.Services.AddSingleton<IReminderStore, PostgresReminderStore>();

// --- Use cases -------------------------------------------------------------

builder.Services.AddSingleton<ConversationSummarizer>();
builder.Services.AddScoped<IngestDocumentUseCase>();
builder.Services.AddScoped<ChatUseCase>();

// Singleton, not Scoped: AlmagestAgentService (below) is itself a singleton
// -- it holds live sessions/pending-approvals in memory, so a new instance
// per request would forget every approval the moment it was issued -- and a
// singleton can't depend on a scoped service. Neither use case holds any
// per-request state, so this lifetime change is safe.
builder.Services.AddSingleton<AskQuestionUseCase>();
builder.Services.AddSingleton<AskDataQuestionUseCase>();
builder.Services.AddSingleton<CreateNoteUseCase>();
builder.Services.AddSingleton<SetReminderUseCase>();

builder.Services.AddSingleton<IAgentService, AlmagestAgentService>();

var app = builder.Build();

app.MapGet("/health", () => "ok");

app.MapPost("/documents", async (HttpRequest request, IngestDocumentUseCase useCase, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Expected multipart/form-data with a 'file' field.");
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files["file"];
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest("Missing 'file'.");
    }

    DocumentSource? source = Path.GetExtension(file.FileName).ToLowerInvariant() switch
    {
        ".pdf" => DocumentSource.Pdf,
        ".md" or ".markdown" => DocumentSource.Markdown,
        _ => null,
    };

    if (source is null)
    {
        return Results.BadRequest("Unsupported file type. Only .pdf and .md/.markdown are supported.");
    }

    var title = form["title"].FirstOrDefault() is { Length: > 0 } providedTitle ? providedTitle : file.FileName;

    await using var stream = file.OpenReadStream();
    var result = await useCase.ExecuteAsync(new IngestRequest(title, source.Value, stream), cancellationToken);

    return Results.Ok(new
    {
        documentId = result.DocumentId,
        chunkCount = result.ChunkCount,
        metadataExtracted = result.MetadataExtracted,
    });
});

app.MapPost("/ask", async (
    AskApiRequest request,
    IQueryRouter router,
    AskQuestionUseCase ragUseCase,
    AskDataQuestionUseCase dataUseCase,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest("Missing 'question'.");
    }

    var route = await router.RouteAsync(request.Question, cancellationToken);

    if (route == QueryRoute.Sql)
    {
        var dataResult = await dataUseCase.ExecuteAsync(request.Question, cancellationToken);
        return Results.Ok(new
        {
            route = "sql",
            answer = dataResult.Answer,
            succeeded = dataResult.Succeeded,
            sql = dataResult.Sql,
        });
    }

    var ragResult = await ragUseCase.ExecuteAsync(request.Question, cancellationToken);
    return Results.Ok(new { route = "rag", answer = ragResult.Answer, found = ragResult.Found, citations = ragResult.Citations });
});

app.MapPost("/chat", async (ChatApiRequest request, ChatUseCase useCase, HttpResponse response, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest("Missing 'message'.");
    }

    var result = await useCase.StreamAsync(request.SessionId, request.Message, cancellationToken: cancellationToken);

    response.StatusCode = 200;
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    response.Headers["X-Session-Id"] = result.SessionId.ToString();

    // Each fragment is JSON-string-encoded before going out as the SSE
    // "data:" payload -- collapses any embedded newlines/quotes so a single
    // fragment can never be mistaken for multiple SSE lines or a new event.
    await foreach (var fragment in result.AnswerFragments.WithCancellation(cancellationToken))
    {
        await response.WriteAsync($"data: {JsonSerializer.Serialize(fragment)}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    await response.WriteAsync("event: done\ndata: {}\n\n", cancellationToken);

    return Results.Empty;
});

app.MapPost("/agent", async (AgentApiRequest request, IAgentService agentService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest("Missing 'message'.");
    }

    var result = await agentService.RunAsync(request.SessionId, request.Message, cancellationToken);

    return Results.Ok(new
    {
        sessionId = result.SessionId,
        answer = result.Answer,
        pendingApprovals = result.PendingApprovals,
    });
});

app.MapPost("/agent/approve", async (AgentApprovalApiRequest request, IAgentService agentService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.ApprovalRequestId))
    {
        return Results.BadRequest("Missing 'sessionId' or 'approvalRequestId'.");
    }

    var result = await agentService.ResumeAsync(request.SessionId, request.ApprovalRequestId, request.Approved, cancellationToken);

    return Results.Ok(new
    {
        sessionId = result.SessionId,
        answer = result.Answer,
        pendingApprovals = result.PendingApprovals,
    });
});

app.Run();

internal sealed record AskApiRequest(string Question);

internal sealed record ChatApiRequest(Guid? SessionId, string Message);

internal sealed record AgentApiRequest(string? SessionId, string Message);

internal sealed record AgentApprovalApiRequest(string SessionId, string ApprovalRequestId, bool Approved);
