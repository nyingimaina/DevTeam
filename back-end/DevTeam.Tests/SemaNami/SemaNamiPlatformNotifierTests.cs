using DevTeam.Broker.Notifications;
using DevTeam.Broker.SemaNami;
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

public class SemaNamiPlatformNotifierTests
{
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

    private static (SemaNamiPlatformNotifier Notifier, FakeMessageSender MessageSender, SemaNamiChannelState ChannelState) Create()
    {
        var messageSender = new FakeMessageSender();
        var store = new FakeConversationStore();
        var sender = new ConversationSender(store, messageSender, "12345");
        var channelState = new SemaNamiChannelState();
        return (new SemaNamiPlatformNotifier(sender, channelState), messageSender, channelState);
    }

    [Fact]
    public async Task NotifyAsync_SendsTitleAndMessage()
    {
        var (notifier, messageSender, _) = Create();

        await notifier.NotifyAsync(new NotificationRequest("QA needs attention", "Feature 'addition' stopped."), CancellationToken.None);

        var sent = Assert.Single(messageSender.Sent);
        Assert.Contains("QA needs attention", sent.Text);
        Assert.Contains("Feature 'addition' stopped.", sent.Text);
    }

    [Fact]
    public async Task NotifyAsync_WithAFeatureId_BindsTheChannelToIt()
    {
        var (notifier, _, channelState) = Create();
        var featureId = Guid.NewGuid();

        await notifier.NotifyAsync(new NotificationRequest("Title", "Message", FeatureId: featureId), CancellationToken.None);

        Assert.Equal(featureId, channelState.BoundFeatureId);
    }

    [Fact]
    public async Task NotifyAsync_WithNoFeatureId_LeavesAnyExistingBindingAlone()
    {
        var (notifier, _, channelState) = Create();
        var existing = Guid.NewGuid();
        channelState.Bind(existing);

        await notifier.NotifyAsync(new NotificationRequest("Title", "Message"), CancellationToken.None);

        Assert.Equal(existing, channelState.BoundFeatureId);
    }

    [Fact]
    public async Task SecondNotification_RepliesInTheSameThread()
    {
        var (notifier, messageSender, _) = Create();

        await notifier.NotifyAsync(new NotificationRequest("First", "message"), CancellationToken.None);
        await notifier.NotifyAsync(new NotificationRequest("Second", "message"), CancellationToken.None);

        Assert.Null(messageSender.Sent[0].ReplyToMessageId);
        Assert.Equal(1, messageSender.Sent[1].ReplyToMessageId);
    }
}
