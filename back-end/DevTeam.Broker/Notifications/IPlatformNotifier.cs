namespace DevTeam.Broker.Notifications;

/// <summary>How much the notification should insist on being noticed.</summary>
public enum NotificationUrgency
{
    /// <summary>Something finished — worth knowing, not worth an alarm.</summary>
    Info,

    /// <summary>Something needs a person: the run failed, or it is waiting on approval.</summary>
    Attention,
}

public sealed record NotificationRequest(string Title, string Message, NotificationUrgency Urgency = NotificationUrgency.Info);

/// <summary>
/// Raises an operating-system notification. One adapter per platform, chosen at runtime — the
/// app is a headless service with no UI of its own, so this is the only way to reach someone who
/// has walked away from the screen.
/// </summary>
public interface IPlatformNotifier
{
    Task NotifyAsync(NotificationRequest request, CancellationToken ct);
}
