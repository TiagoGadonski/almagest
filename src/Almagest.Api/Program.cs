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
using Microsoft.OpenApi.Models;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- API documentation (Swagger/OpenAPI) ------------------------------------
// Enabled in every environment, including Production -- deliberately not
// gated behind IsDevelopment(). This is a public portfolio demo with no
// other landing page; GET / 404ing for anyone who clicks the README's live
// link is worse than showing the API surface. See README "Live demo".
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Almagest",
        Version = "v1",
        Description = "Personal knowledge assistant: RAG over your documents (with citations), " +
            "text-to-SQL over personal data (five-layer defense in depth), and a tool-calling agent. " +
            "Source and full documentation: the repo linked from this API's README.",
    });
});

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

// ALMAGEST_CONNECTION_STRING (Npgsql keyword=value form) is used as given.
// DATABASE_URL (URI form, postgres://user:pass@host:port/db?...) is
// translated automatically -- this is exactly the variable `fly postgres
// attach` sets, so a Fly deployment needs no manual reformatting step
// between attaching the database and the app being able to connect to it.
var connectionString = Environment.GetEnvironmentVariable("ALMAGEST_CONNECTION_STRING") is { Length: > 0 } explicitConnectionString
    ? explicitConnectionString
    : Environment.GetEnvironmentVariable("DATABASE_URL") is { Length: > 0 } databaseUrl
        ? PostgresConnectionStringTranslator.FromDatabaseUrl(databaseUrl)
        : throw new InvalidOperationException("Neither ALMAGEST_CONNECTION_STRING nor DATABASE_URL is set.");

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

// SimilarityFloor: 0.45, not the original 0.70. The 0.70 default was never
// validated against real voyage-4 output -- once the AskQuestionUseCase/
// ChatUseCase query-embedding bug was fixed (queries were sent with
// input_type="document" instead of "query", degrading alignment), real
// relevant matches against the ingested corpus scored 0.59-0.67 and
// irrelevant ones scored up to ~0.40. 0.45 sits below the observed
// relevant cluster (with margin for real, less precisely-phrased user
// questions) and above most of the irrelevant cluster. Based on a small
// manual sample (n=2 relevant/n=3 irrelevant, 17 chunks) -- re-tune against
// tests/eval/questions.md's recall@5 once that harness has real coverage,
// not by further manual spot-checks here.
builder.Services.AddSingleton(new RetrievalOptions(TopK: 5, SimilarityFloor: 0.45, MaxContextTokens: 4000));
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

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Almagest v1");
    options.RoutePrefix = string.Empty; // serve at "/" instead of the default "/swagger"
    options.DocumentTitle = "Almagest API";
});

// Checks real database connectivity, not just "the process is up" -- Fly's
// own health check (fly.toml [[http_service.checks]]) polls this to decide
// whether to route traffic to a machine, so a database outage should
// report unhealthy here rather than a misleadingly green "ok".
app.MapGet("/health", async (NpgsqlDataSource dataSource, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
        return Results.Ok("ok");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Logged server-side, not echoed to the caller -- this endpoint is
        // public (it's what Fly's own health checker polls over the same
        // internet-facing port as everything else), and an Npgsql exception
        // message can include the host or a hint about which credential
        // failed, neither of which belongs in an unauthenticated response.
        logger.LogError(ex, "Health check: database connectivity failed.");
        return Results.Problem(
            title: "Database connectivity check failed",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithSummary("Health check")
.WithDescription("Verifies real database connectivity, not just process liveness.");

// Both a credential problem (our own server's Voyage key is bad -- a
// misconfiguration, not the caller's fault) and a rate limit (the caller's
// request is fine, the upstream provider is asking to slow down) are
// distinguishable failures that used to fall through as a generic 500.
// 502: this API is acting as a gateway to Voyage, and Voyage gave it a
// response it can't use for a reason that's on the server, not the caller.
// 429: propagate the same "too much load" signal Voyage gave us.
static IResult MapEmbeddingProviderError(EmbeddingProviderException ex) => ex.Kind switch
{
    EmbeddingProviderErrorKind.InvalidCredentials => Results.Problem(
        title: "Embedding provider authentication failed",
        detail: "The embedding provider rejected the configured API key. This is a server-side configuration issue, not a problem with the request.",
        statusCode: StatusCodes.Status502BadGateway),
    EmbeddingProviderErrorKind.RateLimited => Results.Problem(
        title: "Embedding provider rate limit exceeded",
        detail: ex.RetryAfter is { } retryAfter
            ? $"{ex.Message} Retry after approximately {retryAfter.TotalSeconds:F0}s."
            : ex.Message,
        statusCode: StatusCodes.Status429TooManyRequests),
    _ => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway),
};

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

    IngestResult result;
    try
    {
        result = await useCase.ExecuteAsync(new IngestRequest(title, source.Value, stream), cancellationToken);
    }
    catch (EmbeddingProviderException ex)
    {
        return MapEmbeddingProviderError(ex);
    }

    return Results.Ok(new
    {
        documentId = result.DocumentId,
        chunkCount = result.ChunkCount,
        metadataExtracted = result.MetadataExtracted,
    });
})
.WithSummary("Ingest a document")
.WithDescription("Parses, chunks, embeds, and stores a PDF or Markdown file. " +
    "Calls real Voyage/Anthropic APIs (real cost) and writes to the production database -- " +
    "\"Try it out\" here is not a sandbox.");

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

    AskResult ragResult;
    try
    {
        ragResult = await ragUseCase.ExecuteAsync(request.Question, cancellationToken: cancellationToken);
    }
    catch (EmbeddingProviderException ex)
    {
        return MapEmbeddingProviderError(ex);
    }

    return Results.Ok(new { route = "rag", answer = ragResult.Answer, found = ragResult.Found, citations = ragResult.Citations });
})
.WithSummary("Ask a question")
.WithDescription("Routes to RAG (your documents) or text-to-SQL (personal data) automatically " +
    "and returns a grounded answer with citations and a similarity score. Read-only, but calls " +
    "real Voyage/Anthropic APIs.");

app.MapPost("/chat", async (ChatApiRequest request, ChatUseCase useCase, HttpResponse response, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest("Missing 'message'.");
    }

    ChatStreamResult result;
    try
    {
        result = await useCase.StreamAsync(request.SessionId, request.Message, cancellationToken: cancellationToken);
    }
    catch (EmbeddingProviderException ex)
    {
        return MapEmbeddingProviderError(ex);
    }

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
})
.WithSummary("Multi-turn chat (streaming)")
.WithDescription("Server-Sent Events stream of a grounded answer, with conversation memory " +
    "across turns. Calls real Voyage/Anthropic APIs and persists the conversation. Swagger UI's " +
    "\"Try it out\" does not render SSE streams well -- use curl with -N instead.");

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
})
.WithSummary("Agent turn")
.WithDescription("Runs one turn of the tool-calling agent. May pause and return a pending " +
    "approval if it selects a side-effecting tool (create note, set reminder) -- see " +
    "POST /agent/approve. Read-only tool calls (RAG, text-to-SQL) execute immediately and write " +
    "nothing.");

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
})
.WithSummary("Approve or reject an agent action")
.WithDescription("Resumes a paused agent turn after a human decision on a side-effecting tool " +
    "call. Approving executes it for real -- creates an actual note or reminder in the " +
    "production database. \"Try it out\" here is not a sandbox.");

app.Run();

internal sealed record AskApiRequest(string Question);

internal sealed record ChatApiRequest(Guid? SessionId, string Message);

internal sealed record AgentApiRequest(string? SessionId, string Message);

internal sealed record AgentApprovalApiRequest(string SessionId, string ApprovalRequestId, bool Approved);
