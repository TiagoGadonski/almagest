using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.Application.UseCases;

public sealed record ConversationOptions(int MaxActiveMessages, int RetainRecentMessages);

public sealed class ConversationSummarizer
{
    private const string SummarizationSystemPrompt = """
        You maintain a running summary of an ongoing conversation. Given the
        existing summary (if any) and a batch of older messages, produce an
        updated summary that preserves every fact, decision, preference, and
        open question a later turn might need -- names, numbers, commitments,
        anything specific. Prose, not a transcript. Be concise, but never at
        the cost of dropping a concrete detail.
        """;

    private readonly IChatService _chatService;

    public ConversationSummarizer(IChatService chatService)
    {
        _chatService = chatService;
    }

    public bool ShouldSummarize(int activeMessageCount, ConversationOptions options) =>
        activeMessageCount > options.MaxActiveMessages;

    public async Task<string> SummarizeAsync(
        string? existingSummary, IReadOnlyList<Message> messagesToFold, CancellationToken cancellationToken = default)
    {
        var transcript = string.Join("\n", messagesToFold.Select(m => $"{m.Role}: {m.Content}"));
        var userPrompt = existingSummary is { } summary
            ? $"Existing summary:\n{summary}\n\nOlder messages to fold in:\n{transcript}"
            : $"Messages to summarize:\n{transcript}";

        return await _chatService.CompleteAsync(SummarizationSystemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
    }
}
