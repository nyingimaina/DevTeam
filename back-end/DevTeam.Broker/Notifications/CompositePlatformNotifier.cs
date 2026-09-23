using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Notifications;

/// <summary>
/// Fans a single notification out to every registered platform adapter — lets more than one
/// sink (the OS toast AND the SemaNami Telegram channel) be active at once without UserNotifier
/// needing to know how many there are, or changing at all. Registered as the sole IPlatformNotifier
/// in DI; the individual adapters (WindowsToastNotifier/LoggingNotifier, SemaNamiPlatformNotifier)
/// are resolved by their own concrete types and handed to this composite explicitly (a factory
/// registration, not IEnumerable&lt;IPlatformNotifier&gt;, since that would try to resolve this
/// composite as one of its own inputs).
/// </summary>
public sealed class CompositePlatformNotifier : IPlatformNotifier
{
    private readonly IReadOnlyList<IPlatformNotifier> _notifiers;
    private readonly ILogger<CompositePlatformNotifier> _logger;

    public CompositePlatformNotifier(IReadOnlyList<IPlatformNotifier> notifiers, ILogger<CompositePlatformNotifier> logger)
    {
        _notifiers = notifiers;
        _logger = logger;
    }

    public async Task NotifyAsync(NotificationRequest request, CancellationToken ct)
    {
        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.NotifyAsync(request, ct);
            }
            catch (Exception ex)
            {
                // One sink failing (e.g. Telegram unreachable) must never stop the others from
                // raising their own notification.
                _logger.LogDebug(ex, "A platform notifier ({Notifier}) failed to raise a notification.", notifier.GetType().Name);
            }
        }
    }
}
