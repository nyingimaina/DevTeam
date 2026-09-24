using DevTeam.Broker.Domain;
using DevTeam.Broker.Notifications;
using DevTeam.Broker.SemaNami;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SemaNami.Core;
using SemaNami.Core.Conversations;

namespace DevTeam.Tests.SemaNami;

public class SemaNamiChannelStateTests
{
    [Fact]
    public void NoBindingYet_IsNull()
    {
        var state = new SemaNamiChannelState();

        Assert.Null(state.BoundFeatureId);
    }

    [Fact]
    public void Bind_RecordsTheMostRecentlyBoundFeature()
    {
        var state = new SemaNamiChannelState();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        state.Bind(first);
        Assert.Equal(first, state.BoundFeatureId);

        state.Bind(second);
        Assert.Equal(second, state.BoundFeatureId);
    }
}

public class SemaNamiPlatformNotifierTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SemaNamiPlatformNotifierTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateSettingsFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> CreateSettingsFactory() => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    /// <summary>A settings instance with .Enabled already at the given value, ready to hand to the notifier.</summary>
    private async Task<SemaNamiSettings> CreateSettingsAsync(bool enabled)
    {
        var settings = new SemaNamiSettings(CreateSettingsFactory());
        await settings.SetEnabledAsync(enabled, CancellationToken.None);
        return settings;
    }

    private sealed class FakeMessageSender : ITelegramMessageSender
    {
        public List<(string ChatId, string Text, int? ReplyToMessageId)> Sent { get; } = [];
        private int _nextMessageId = 1;

        public Task SendMessageAsync(string chatId, string text, CancellationToken cancellationToken = default)
        {
            Sent.Add((chatId, text, null));
            return Task.CompletedTask;
        }

        public Task<int> SendReplyAsync(string chatId, string text, int? replyToMessageId, CancellationToken cancellationToken = default)
        {
            Sent.Add((chatId, text, replyToMessageId));
            return Task.FromResult(_nextMessageId++);
        }
    }

    private sealed class FakeConversationStore : IConversationStore
    {
        private readonly Dictionary<(string, string), Conversation> _conversations = [];

        public Conversation? GetConversation(string sender, string conversationId)
            => _conversations.TryGetValue((sender, conversationId), out var c) ? c : null;

        public void RecordSentMessage(string sender, string conversationId, int telegramMessageId, string text)
        {
            var now = DateTime.UtcNow;
            _conversations[(sender, conversationId)] = new Conversation(sender, conversationId, "open", telegramMessageId, now, now);
        }

        public (string Sender, string ConversationId)? TryFindConversationByMessageId(int telegramMessageId) => null;

        public IReadOnlyList<Conversation> GetOpenConversations() => _conversations.Values.ToList();

        public StoredMessage RecordReceivedMessage(string sender, string conversationId, int telegramMessageId, string text, bool ambiguousMatch)
            => new(1, "in", telegramMessageId, text, ambiguousMatch, DateTime.UtcNow);

        public void CloseConversation(string sender, string conversationId) { }

        public IReadOnlyList<StoredMessage> GetHistory(string sender, string conversationId, long? afterSeq) => [];

        public int? GetLastUpdateId() => null;

        public void SetLastUpdateId(int updateId) { }
    }

    private async Task<(SemaNamiPlatformNotifier Notifier, FakeMessageSender MessageSender, SemaNamiChannelState ChannelState)> CreateAsync(bool enabled = true)
    {
        var messageSender = new FakeMessageSender();
        var store = new FakeConversationStore();
        var sender = new ConversationSender(store, messageSender, "12345");
        var channelState = new SemaNamiChannelState();
        var settings = await CreateSettingsAsync(enabled);
        return (new SemaNamiPlatformNotifier(sender, channelState, settings), messageSender, channelState);
    }

    [Fact]
    public async Task NotifyAsync_SendsTitleAndMessage()
    {
        var (notifier, messageSender, _) = await CreateAsync();

        await notifier.NotifyAsync(new NotificationRequest("QA needs attention", "Feature 'addition' stopped."), CancellationToken.None);

        var sent = Assert.Single(messageSender.Sent);
        Assert.Contains("QA needs attention", sent.Text);
        Assert.Contains("Feature 'addition' stopped.", sent.Text);
    }

    [Fact]
    public async Task NotifyAsync_WithAFeatureId_BindsTheChannelToIt()
    {
        var (notifier, _, channelState) = await CreateAsync();
        var featureId = Guid.NewGuid();

        await notifier.NotifyAsync(new NotificationRequest("Title", "Message", FeatureId: featureId), CancellationToken.None);

        Assert.Equal(featureId, channelState.BoundFeatureId);
    }

    [Fact]
    public async Task NotifyAsync_WithNoFeatureId_LeavesAnyExistingBindingAlone()
    {
        var (notifier, _, channelState) = await CreateAsync();
        var existing = Guid.NewGuid();
        channelState.Bind(existing);

        await notifier.NotifyAsync(new NotificationRequest("Title", "Message"), CancellationToken.None);

        Assert.Equal(existing, channelState.BoundFeatureId);
    }

    [Fact]
    public async Task NotifyAsync_WhenTheChannelIsDisabled_DoesNotSend()
    {
        // Regression: the toggle in Settings ("Send stage updates to Telegram") is documented as
        // gating outbound notifications, but the notifier used to ignore SemaNamiSettings.Enabled
        // entirely and always send whenever TELEGRAM_BOT_TOKEN/CHAT_ID were configured — so turning
        // the toggle off in the UI had no effect on whether Telegram actually got messages.
        var (notifier, messageSender, _) = await CreateAsync(enabled: false);

        await notifier.NotifyAsync(new NotificationRequest("Developer is waiting for you", "Approve to continue."), CancellationToken.None);

        Assert.Empty(messageSender.Sent);
    }

    [Fact]
    public async Task NotifyAsync_WhenTheChannelIsDisabled_DoesNotBindTheChannelEither()
    {
        var (notifier, _, channelState) = await CreateAsync(enabled: false);
        var featureId = Guid.NewGuid();

        await notifier.NotifyAsync(new NotificationRequest("Title", "Message", FeatureId: featureId), CancellationToken.None);

        Assert.Null(channelState.BoundFeatureId);
    }

    [Fact]
    public async Task SecondNotification_RepliesInTheSameThread()
    {
        var (notifier, messageSender, _) = await CreateAsync();

        await notifier.NotifyAsync(new NotificationRequest("First", "message"), CancellationToken.None);
        await notifier.NotifyAsync(new NotificationRequest("Second", "message"), CancellationToken.None);

        Assert.Null(messageSender.Sent[0].ReplyToMessageId);
        Assert.Equal(1, messageSender.Sent[1].ReplyToMessageId);
    }
}
