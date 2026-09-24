using DevTeam.Broker.Notifications;
using SemaNami.Core.Conversations;

namespace DevTeam.Broker.SemaNami;

/// <summary>
/// Sends a DevTeam notification over the live SemaNami Telegram channel. Uses ConversationSender
/// (not the plain Notifier facade) specifically because it records each sent message, which is
/// what lets a Telegram reply be correlated back to this thread later — a bare Notifier.NotifyAsync
/// has no reply-tracking at all. Plugged in as one more IPlatformNotifier sink (see
/// CompositePlatformNotifier) alongside the OS toast, so it automatically inherits
/// UserNotifier's existing "is this worth notifying"/de-dup rules for free.
/// </summary>
public sealed class SemaNamiPlatformNotifier : IPlatformNotifier
{
    public const string Sender = "devteam";

    private readonly ConversationSender _sender;
    private readonly SemaNamiChannelState _channelState;
    private readonly SemaNamiSettings _settings;

    public SemaNamiPlatformNotifier(ConversationSender sender, SemaNamiChannelState channelState, SemaNamiSettings settings)
    {
        _sender = sender;
        _channelState = channelState;
        _settings = settings;
    }

    public async Task NotifyAsync(NotificationRequest request, CancellationToken ct)
    {
        // The Settings UI's "Send stage updates to Telegram" toggle is documented as gating
        // whether Telegram gets messages at all, not just whether replies are routed back in.
        if (!_settings.Enabled)
            return;

        if (request.FeatureId is { } featureId)
            _channelState.Bind(featureId);

        var text = $"{request.Title}\n{request.Message}";
        await _sender.SendAsync(Sender, SemaNamiChannelState.ConversationId, text, ct);
    }
}
