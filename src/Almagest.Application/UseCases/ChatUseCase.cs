using System.Runtime.CompilerServices;
using System.Text;
using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.Application.UseCases;

public sealed record ChatStreamResult(Guid SessionId, IAsyncEnumerable<string> AnswerFragments);

public sealed class ChatUseCase
{
    private const string GroundingSystemPrompt = """
        You are a precise research assistant with memory of this conversation.
        Answer using the conversation so far and the supplied excerpts from the
        user's documents. Never draw on outside/parametric knowledge for facts
        the excerpts should cover. Cite excerpts as [chunk:<id>] per claim. If
        neither the conversation nor the excerpts answer the question, say so.
        """;

    private const int CharsPerTokenEstimate = 4;

    private readonly IConversationStore _conversationStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IChunkStore _chunkStore;
    private readonly IChatService _chatService;
    private readonly ConversationSummarizer _summarizer;
    private readonly RetrievalOptions _retrievalOptions;
    private readonly ConversationOptions _conversationOptions;

    public ChatUseCase(
        IConversationStore conversationStore,
        IEmbeddingService embeddingService,
        IChunkStore chunkStore,
        IChatService chatService,
        ConversationSummarizer summarizer,
        RetrievalOptions retrievalOptions,
        ConversationOptions conversationOptions)
    {
        _conversationStore = conversationStore;
        _embeddingService = embeddingService;
        _chunkStore = chunkStore;
        _chatService = chatService;
        _summarizer = summarizer;
        _retrievalOptions = retrievalOptions;
        _conversationOptions = conversationOptions;
    }

    public async Task<ChatStreamResult> StreamAsync(
        Guid? sessionId, string userMessage, ChunkSearchFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var session = sessionId is { } id
            ? await _conversationStore.FindSessionAsync(id, cancellationToken).ConfigureAwait(false) ?? Session.Create()
            : Session.Create();

        session.Touch();

        // The session row must exist before any message can reference it
        // (messages.session_id is a foreign key) -- persist it first, for a
        // brand new session as much as a resumed one.
        await _conversationStore.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);

        var activeMessages = await _conversationStore
            .GetMessagesAsync(session.Id, session.SummarizedThroughPosition, cancellationToken)
            .ConfigureAwait(false);

        var nextPosition = activeMessages.Count > 0 ? activeMessages[^1].Position + 1 : session.SummarizedThroughPosition + 1;
        var userMsg = Message.Create(session.Id, MessageRole.User, userMessage, nextPosition);
        await _conversationStore.AppendMessageAsync(userMsg, cancellationToken).ConfigureAwait(false);
        activeMessages = [.. activeMessages, userMsg];

        if (_summarizer.ShouldSummarize(activeMessages.Count, _conversationOptions))
        {
            var toFold = activeMessages.Take(activeMessages.Count - _conversationOptions.RetainRecentMessages).ToList();
            var newSummary = await _summarizer.SummarizeAsync(session.Summary, toFold, cancellationToken).ConfigureAwait(false);
            session.ApplySummary(newSummary, toFold[^1].Position);
            activeMessages = activeMessages.Skip(toFold.Count).ToList();
        }

        await _conversationStore.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);

        var queryEmbedding = (await _embeddingService.EmbedAsync([userMessage], cancellationToken).ConfigureAwait(false))[0];
        var candidates = await _chunkStore
            .SearchAsync(queryEmbedding, _retrievalOptions.TopK, filter, cancellationToken)
            .ConfigureAwait(false);
        var relevant = candidates
            .Where(c => c.EmbeddingModelId == _embeddingService.ModelId && c.Similarity >= _retrievalOptions.SimilarityFloor)
            .ToList();

        var userPrompt = BuildUserPrompt(session.Summary, activeMessages, relevant, userMessage);
        var fragments = StreamAndPersistAsync(session, nextPosition + 1, GroundingSystemPrompt, userPrompt, cancellationToken);

        return new ChatStreamResult(session.Id, fragments);
    }

    // Persistence of the assistant's reply happens only after the stream is
    // fully consumed -- if the client disconnects mid-stream, the user's
    // message is already saved, but the partial assistant answer is not.
    private async IAsyncEnumerable<string> StreamAndPersistAsync(
        Session session,
        int assistantPosition,
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();

        await foreach (var fragment in _chatService.StreamCompleteAsync(systemPrompt, userPrompt, cancellationToken)
            .ConfigureAwait(false))
        {
            buffer.Append(fragment);
            yield return fragment;
        }

        if (buffer.Length > 0)
        {
            var assistantMessage = Message.Create(session.Id, MessageRole.Assistant, buffer.ToString(), assistantPosition);
            await _conversationStore.AppendMessageAsync(assistantMessage, cancellationToken).ConfigureAwait(false);
        }
    }

    private string BuildUserPrompt(
        string? summary, IReadOnlyList<Message> recentMessages, IReadOnlyList<ScoredChunk> relevantChunks, string latestQuestion)
    {
        var builder = new StringBuilder();

        if (summary is { Length: > 0 })
        {
            builder.AppendLine("Conversation summary so far:").AppendLine(summary).AppendLine();
        }

        if (recentMessages.Count > 1) // exclude the just-appended latest message -- it's "Question:" below
        {
            builder.AppendLine("Recent messages:");
            foreach (var message in recentMessages.SkipLast(1))
            {
                builder.AppendLine($"{message.Role}: {message.Content}");
            }

            builder.AppendLine();
        }

        if (relevantChunks.Count > 0)
        {
            builder.AppendLine("Document excerpts:");
            var remaining = _retrievalOptions.MaxContextTokens * CharsPerTokenEstimate;
            foreach (var chunk in relevantChunks.OrderByDescending(c => c.Similarity))
            {
                var entry = $"[chunk:{chunk.Chunk.Id}] {chunk.Chunk.Text}\n";
                if (entry.Length > remaining && builder.Length > 0)
                {
                    break;
                }

                builder.Append(entry);
                remaining -= entry.Length;
            }

            builder.AppendLine();
        }

        builder.Append("Question: ").Append(latestQuestion);
        return builder.ToString();
    }
}
