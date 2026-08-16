using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

// Hello-world for the mandated LLM access chain. No RAG here — this only proves
// we can reach Claude through:
//   Anthropic (official SDK) -> IChatClient -> AsChatCompletionService() -> Semantic Kernel

// Secrets come from the environment, never from appsettings. The official SDK
// reads ANTHROPIC_API_KEY itself, but we validate here so the failure is a clear
// message instead of an auth error on the first call.
_ = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException(
        "ANTHROPIC_API_KEY is not set. Set it in the environment before running the Lab.");

var modelId = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-haiku-4-5";

// The Anthropic API *requires* max_tokens; omitting it returns an opaque 400.
// We pin it as a global default in ConfigureOptions so every call downstream
// inherits it, and neither path below has to remember to set it.
AnthropicClient anthropic = new();

using IChatClient chatClient = anthropic
    .AsIChatClient(modelId)
    .AsBuilder()
    .ConfigureOptions(options => options.MaxOutputTokens ??= 1024)
    .Build();

// One conversion, reused by both paths.
var chatService = chatClient.AsChatCompletionService();

Console.WriteLine($"Model: {modelId}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Path 1 — IChatCompletionService directly, driving a ChatHistory.
// ---------------------------------------------------------------------------
Console.WriteLine("[1] IChatCompletionService + ChatHistory");

var history = new ChatHistory();
history.AddSystemMessage("You are terse. Answer in a single sentence.");
history.AddUserMessage("What is the Almagest, the astronomical treatise by Ptolemy?");

var direct = await chatService.GetChatMessageContentAsync(history);
Console.WriteLine(direct.Content);
Console.WriteLine();

// ---------------------------------------------------------------------------
// Path 2 — Semantic Kernel prompt template with named variables.
// ---------------------------------------------------------------------------
Console.WriteLine("[2] Semantic Kernel prompt template");

var kernelBuilder = Kernel.CreateBuilder();
kernelBuilder.Services.AddSingleton(chatService);
var kernel = kernelBuilder.Build();

const string prompt = "Answer in the style of {{$style}}, in one sentence: {{$question}}";
var templated = await kernel.InvokePromptAsync(prompt, new KernelArguments
{
    ["style"] = "a medieval chronicler",
    ["question"] = "Who was Claudius Ptolemy?"
});
Console.WriteLine(templated);