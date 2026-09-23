using DevTeam.Broker.Domain;
using DevTeam.Broker.SemaNami;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using SemaNami.Core.Conversations;

namespace DevTeam.Tests.SemaNami;

public class SemaNamiListenerServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SemaNamiListenerServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    private sealed class FakeUpdatesSource : IUpdatesSource
    {
        public int CallCount;
        public Func<IReadOnlyList<IncomingUpdate>> Produce { get; set; } = () => [];

        public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(int? offset, int timeoutSeconds, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(Produce());
        }
    }

    private sealed class InMemoryConversationStore : IConversationStore
    {
        private int? _lastUpdateId;

        public Conversation? GetConversation(string sender, string conversationId) => null;
        public void RecordSentMessage(string sender, string conversationId, int telegramMessageId, string text) { }
        public (string Sender, string ConversationId)? TryFindConversationByMessageId(int telegramMessageId) => null;
        public IReadOnlyList<Conversation> GetOpenConversations() => [];
        public StoredMessage RecordReceivedMessage(string sender, string conversationId, int telegramMessageId, string text, bool ambiguousMatch)
            => new(1, "in", telegramMessageId, text, ambiguousMatch, DateTime.UtcNow);
        public void CloseConversation(string sender, string conversationId) { }
        public IReadOnlyList<StoredMessage> GetHistory(string sender, string conversationId, long? afterSeq) => [];
        public int? GetLastUpdateId() => _lastUpdateId;
        public void SetLastUpdateId(int updateId) => _lastUpdateId = updateId;
    }

    private sealed class FakeReplyRouter : ISemaNamiReplyRouter
    {
        public List<StoredMessage> Routed { get; } = [];

        public Task RouteAsync(StoredMessage message, CancellationToken ct)
        {
            Routed.Add(message);
            return Task.CompletedTask;
        }
    }

    private async Task<SemaNamiSettings> EnabledSettingsAsync()
    {
        var settings = new SemaNamiSettings(CreateFactory());
        await settings.SetEnabledAsync(true, CancellationToken.None);
        return settings;
    }

    [Fact]
    public async Task PollLoopAsync_WhileEnabled_KeepsPollingUntilCancelled()
    {
        var updates = new FakeUpdatesSource();
        var listener = new ConversationListener(new InMemoryConversationStore(), updates, new InProcessRealtimeNotifier());
        var service = new SemaNamiListenerService(
            await EnabledSettingsAsync(), listener, new InProcessRealtimeNotifier(), new FakeReplyRouter(), NullLogger<SemaNamiListenerService>.Instance);

        using var cts = new CancellationTokenSource();
        updates.Produce = () =>
        {
            if (updates.CallCount >= 3)
                cts.Cancel();
            return [];
        };

        await service.PollLoopAsync(cts.Token);

        Assert.True(updates.CallCount >= 3);
    }

    [Fact]
    public async Task PollLoopAsync_WhileDisabled_NeverPolls()
    {
        var updates = new FakeUpdatesSource();
        var listener = new ConversationListener(new InMemoryConversationStore(), updates, new InProcessRealtimeNotifier());
        var settings = new SemaNamiSettings(CreateFactory()); // never enabled
        var service = new SemaNamiListenerService(
            settings, listener, new InProcessRealtimeNotifier(), new FakeReplyRouter(), NullLogger<SemaNamiListenerService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await service.PollLoopAsync(cts.Token);

        Assert.Equal(0, updates.CallCount);
    }

    [Fact]
    public async Task PollLoopAsync_APollFailing_KeepsGoingInsteadOfExiting()
    {
        var updates = new FakeUpdatesSource();
        var listener = new ConversationListener(new InMemoryConversationStore(), updates, new InProcessRealtimeNotifier());
        var service = new SemaNamiListenerService(
            await EnabledSettingsAsync(), listener, new InProcessRealtimeNotifier(), new FakeReplyRouter(), NullLogger<SemaNamiListenerService>.Instance);

        using var cts = new CancellationTokenSource();
        updates.Produce = () =>
        {
            if (updates.CallCount == 1)
                throw new InvalidOperationException("Telegram unreachable");
            if (updates.CallCount >= 2)
                cts.Cancel();
            return [];
        };

        await service.PollLoopAsync(cts.Token);

        Assert.True(updates.CallCount >= 2);
    }

    [Fact]
    public async Task RouteLoopAsync_ANewMessage_GetsHandedToTheRouter()
    {
        var realtimeNotifier = new InProcessRealtimeNotifier();
        var router = new FakeReplyRouter();
        var updates = new FakeUpdatesSource();
        var listener = new ConversationListener(new InMemoryConversationStore(), updates, realtimeNotifier);
        var service = new SemaNamiListenerService(
            await EnabledSettingsAsync(), listener, realtimeNotifier, router, NullLogger<SemaNamiListenerService>.Instance);

        using var cts = new CancellationTokenSource();
        var routeTask = service.RouteLoopAsync(cts.Token);

        // Give the subscribe call a moment to actually register before publishing.
        await Task.Delay(50);
        realtimeNotifier.PublishNewMessage(SemaNamiPlatformNotifier.Sender, SemaNamiChannelState.ConversationId, new StoredMessage(1, "in", 7, "yes go ahead", false, DateTime.UtcNow));
        await Task.Delay(50);
        cts.Cancel();
        await routeTask;

        var routed = Assert.Single(router.Routed);
        Assert.Equal("yes go ahead", routed.Text);
    }
}
