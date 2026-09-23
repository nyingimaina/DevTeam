namespace DevTeam.Broker.Notifications;

/// <summary>
/// Linux and macOS adapter stub. Deliberately a no-op that logs: the platform-specific call
/// (notify-send / osascript) is not written yet, but having the adapter in place means the app
/// runs everywhere and adding the real call is a change to this one class.
/// </summary>
public sealed class LoggingNotifier : IPlatformNotifier
{
    private readonly ILogger<LoggingNotifier> _logger;

    public LoggingNotifier(ILogger<LoggingNotifier> logger) => _logger = logger;

    public Task NotifyAsync(NotificationRequest request, CancellationToken ct)
    {
        _logger.LogInformation(
            "[notification — not supported on this platform yet] {Title}: {Message}",
            request.Title, request.Message);
        return Task.CompletedTask;
    }
}
