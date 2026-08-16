using Almagest.Domain;
using Almagest.Infrastructure.Persistence;

namespace Almagest.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class PostgresConversationStoreTests
{
    private readonly PostgresFixture _fixture;

    public PostgresConversationStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveSession_AppendMessages_ThenReadBack_RoundTripsCorrectly()
    {
        var store = new PostgresConversationStore(_fixture.DataSource);
        var session = Session.Create();
        await store.SaveSessionAsync(session);

        var first = Message.Create(session.Id, MessageRole.User, "hello", 0);
        var second = Message.Create(session.Id, MessageRole.Assistant, "hi there", 1);
        await store.AppendMessageAsync(first);
        await store.AppendMessageAsync(second);

        var reloaded = await store.FindSessionAsync(session.Id);
        var messages = await store.GetMessagesAsync(session.Id, fromPositionExclusive: -1);

        Assert.NotNull(reloaded);
        Assert.Equal(session.Id, reloaded!.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("hello", messages[0].Content);
        Assert.Equal(MessageRole.Assistant, messages[1].Role);
    }

    [Fact]
    public async Task GetMessagesAsync_ExcludesMessagesAtOrBeforeTheCutoff()
    {
        var store = new PostgresConversationStore(_fixture.DataSource);
        var session = Session.Create();
        await store.SaveSessionAsync(session);

        for (var i = 0; i < 5; i++)
        {
            await store.AppendMessageAsync(Message.Create(session.Id, MessageRole.User, $"message {i}", i));
        }

        var afterCutoff = await store.GetMessagesAsync(session.Id, fromPositionExclusive: 2);

        Assert.Equal(2, afterCutoff.Count);
        Assert.Equal(3, afterCutoff[0].Position);
        Assert.Equal(4, afterCutoff[1].Position);
    }

    [Fact]
    public async Task SaveSessionAsync_Upserts_SummaryAndCutoffPersist()
    {
        var store = new PostgresConversationStore(_fixture.DataSource);
        var session = Session.Create();
        await store.SaveSessionAsync(session);

        session.ApplySummary("a running summary", throughPosition: 3);
        await store.SaveSessionAsync(session);

        var reloaded = await store.FindSessionAsync(session.Id);

        Assert.Equal("a running summary", reloaded!.Summary);
        Assert.Equal(3, reloaded.SummarizedThroughPosition);
    }
}
