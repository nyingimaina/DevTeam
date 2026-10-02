namespace DevTeam.Broker.Notifications;

/// <summary>What happened to a stage, as the notifier needs to know it.</summary>
public sealed record StageOutcome(
    string FeatureKey, string StageName, string Status, bool AllPassed, Guid? FeatureId = null,
    bool NeedsDecision = false, string? Detail = null);

/// <summary>
/// Decides whether a stage outcome is worth a desktop notification, and raises it through the
/// platform adapter. Kept separate from the engine so the wording and the "don't spam me" rules
/// can be tested without running a pipeline.
/// </summary>
public interface IUserNotifier
{
    Task StageFinishedAsync(StageOutcome outcome, CancellationToken ct);
}

public sealed class UserNotifier : IUserNotifier
{
    // The same stage keeps reporting the same terminal state while it is polled; without this a
    // single blocked stage would fire a notification per poll.
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    private readonly IPlatformNotifier _platform;
    private readonly NotificationSettings _settings;
    private readonly ILogger<UserNotifier> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    public UserNotifier(IPlatformNotifier platform, NotificationSettings settings, ILogger<UserNotifier> logger)
    {
        _platform = platform;
        _settings = settings;
        _logger = logger;
    }

    public async Task StageFinishedAsync(StageOutcome outcome, CancellationToken ct)
    {
        if (Describe(outcome) is not { } notification)
            return;

        if (IsRepeat(outcome))
            return;

        try
        {
            await _platform.NotifyAsync(
                _settings.Sound ? notification : notification with { Urgency = NotificationUrgency.Info },
                ct);
        }
        catch (Exception ex)
        {
            // A failed notification must never fail the work it was reporting on.
            _logger.LogDebug(ex, "Could not raise a notification for {Stage}.", outcome.StageName);
        }
    }

    /// <summary>The notification to raise, or null when this outcome isn't one the user asked for.</summary>
    private NotificationRequest? Describe(StageOutcome outcome)
    {
        var stage = FriendlyStage(outcome.StageName);
        var feature = outcome.FeatureKey;

        if (outcome.Status == "BlockedSignoff")
            return _settings.ApprovalNeeded
                ? new NotificationRequest(
                    $"{stage} is waiting for you",
                    $"Feature '{feature}' needs your approval before it can continue.",
                    NotificationUrgency.Attention,
                    outcome.FeatureId)
                : null;

        // Nothing automatic will ever resolve this one, so it is not governed by the generic
        // "needs attention" preference: silencing that must not leave a stalled app silent.
        if (outcome.Status == "BlockedGate" && outcome.NeedsDecision)
            return new NotificationRequest(
                $"{stage} needs your decision",
                string.IsNullOrWhiteSpace(outcome.Detail)
                    ? $"Feature '{feature}' is paused until you decide what to do."
                    : $"Feature '{feature}': {outcome.Detail}",
                NotificationUrgency.Attention,
                outcome.FeatureId);

        if (outcome.Status is "BlockedGate" or "BlockedEntry" or "Escalated")
            return _settings.NeedsAttention
                ? new NotificationRequest(
                    $"{stage} needs attention",
                    $"Feature '{feature}' stopped: a check didn't pass or the agent hit an error.",
                    NotificationUrgency.Attention,
                    outcome.FeatureId)
                : null;

        if (outcome.Status == "Complete")
            return _settings.StageComplete
                ? new NotificationRequest(
                    $"{stage} finished",
                    $"Feature '{feature}' completed the {stage} step.",
                    NotificationUrgency.Info,
                    outcome.FeatureId)
                : null;

        return null;
    }

    private bool IsRepeat(StageOutcome outcome)
    {
        var key = $"{outcome.FeatureKey}|{outcome.StageName}|{outcome.Status}";
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_recent.TryGetValue(key, out var last) && now - last < RepeatWindow)
                return true;

            _recent[key] = now;
            return false;
        }
    }

    internal static string FriendlyStage(string stageName) => stageName switch
    {
        "business-analyst" => "Business Analyst",
        "qa" => "QA",
        "verification" => "Final checks",
        _ => char.ToUpperInvariant(stageName.Length == 0 ? 'S' : stageName[0]) + stageName[1..],
    };
}
