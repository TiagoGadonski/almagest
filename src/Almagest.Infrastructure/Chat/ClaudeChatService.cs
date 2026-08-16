using System.Runtime.CompilerServices;
using Almagest.Application.Ports;
using Almagest.Infrastructure.Telemetry;
using Microsoft.Extensions.AI;

namespace Almagest.Infrastructure.Chat;

// Wraps a shared IChatClient (built once in the composition root via the
// chain proven in Almagest.Lab/Program.cs: Anthropic SDK -> IChatClient)
// rather than constructing its own -- the same client is also injected into
// ClaudeMetadataExtractor/ClaudeSqlGenerator/ClaudeQueryRouter, so there's
// one Anthropic connection, not one per consumer. Uses IChatClient directly
// for both methods (Phase 5: previously CompleteAsync went through Semantic
// Kernel's IChatCompletionService -- switched so real Usage/token data is
// available for tracing, and to match every other Claude-calling class in
// this project, which already used IChatClient directly. This let Semantic
// Kernel be dropped from Almagest.Infrastructure's dependencies entirely;
// only Almagest.Lab, unrelated to the running app, still references it).
public sealed class ClaudeChatService : IChatService
{
    private readonly IChatClient _chatClient;

    public ClaudeChatService(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        using var activity = AlmagestTelemetry.ActivitySource.StartActivity("claude.complete");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);

        Tag(activity, response.Usage);

        return response.Text;
    }

    // Uses IChatClient's streaming overload directly -- same reasoning as
    // CompleteAsync above, and zero risk to the non-streaming path since
    // they're independent methods.
    public async IAsyncEnumerable<string> StreamCompleteAsync(
        string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = AlmagestTelemetry.ActivitySource.StartActivity("claude.stream_complete");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        UsageDetails? usage = null;

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            usage ??= update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details;

            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }

        Tag(activity, usage);
    }

    private static void Tag(System.Diagnostics.Activity? activity, UsageDetails? usage)
    {
        if (activity is null || usage is null)
        {
            return;
        }

        var inputTokens = usage.InputTokenCount ?? 0;
        var outputTokens = usage.OutputTokenCount ?? 0;

        activity.SetTag("llm.input_tokens", inputTokens);
        activity.SetTag("llm.output_tokens", outputTokens);
        activity.SetTag(
            "llm.estimated_cost_usd",
            AlmagestTelemetry.EstimateCostUsd(inputTokens, AlmagestTelemetry.Pricing.ClaudeInputCostPerMillionTokensUsd)
            + AlmagestTelemetry.EstimateCostUsd(outputTokens, AlmagestTelemetry.Pricing.ClaudeOutputCostPerMillionTokensUsd));
    }
}
