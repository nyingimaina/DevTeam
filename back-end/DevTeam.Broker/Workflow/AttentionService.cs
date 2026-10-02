using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Workflow;

public enum AttentionKind
{
    /// <summary>A stage is finished and waits for the person to approve it.</summary>
    Approval,

    /// <summary>A stage stopped and nothing automatic can fix it - the person must decide.</summary>
    Decision,
}

/// <summary>
/// One thing only the person can do next. Worded for someone who has never seen a pipeline: what
/// is waiting, in plain words, and nothing about gates, rounds or sessions.
/// </summary>
public sealed record AttentionItem(
    string Id,
    Guid ReleaseId,
    Guid FeatureId,
    string FeatureKey,
    string StageName,
    AttentionKind Kind,
    string Title,
    string Message,
    DateTimeOffset Since,
    IReadOnlyList<PendingRuling>? Rulings = null);

/// <summary>
/// The single answer to "is anything waiting on me?". Every human-decision surface used to live
/// inline somewhere in the release screens, so the operator found them by scrolling - and was
/// never told. This reads the same stage state the engine writes, so there is no second source of
/// truth to drift: a stage stops asking exactly when its state changes.
/// </summary>
public sealed class AttentionService
{
    private static readonly ReleaseStatus[] ClosedReleases = [ReleaseStatus.Released, ReleaseStatus.Cancelled];

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;

    public AttentionService(IDbContextFactory<DevTeamDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<AttentionItem>> ListAsync(string? workspacePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var features = await db.ReleaseFeatures
            .AsNoTracking()
            .Include(f => f.Release)
            .Include(f => f.FlowPosition)
            .Include(f => f.StageRuns)
            .Where(f => f.FlowPosition != null)
            .ToListAsync(ct);

        var items = new List<AttentionItem>();
        foreach (var feature in features)
        {
            if (ClosedReleases.Contains(feature.Release.Status))
                continue;
            if (workspacePath is not null
                && !string.Equals(feature.Release.WorkspacePath, workspacePath, StringComparison.OrdinalIgnoreCase))
                continue;

            // Only the stage the feature is on, and only its newest attempt: older attempts and
            // stages already passed are history, not something to ask about.
            var stageName = feature.FlowPosition!.CurrentStageName;
            var run = feature.StageRuns
                .Where(sr => sr.StageName == stageName)
                .OrderByDescending(sr => sr.Attempt)
                .ThenByDescending(sr => sr.StartedAt)
                .FirstOrDefault();
            if (run is null)
                continue;

            if (Describe(feature, run) is { } item)
                items.Add(item);
        }

        return items.OrderBy(i => i.Since).ToList();
    }

    /// <summary>Records the person's ruling on a test the checker challenged, in the feature's own workspace.</summary>
    public async Task<RulingResult> RecordRulingAsync(
        Guid featureId, string test, RulingDecision decision, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var feature = await db.ReleaseFeatures
            .AsNoTracking()
            .Include(f => f.Release)
            .FirstOrDefaultAsync(f => f.Id == featureId, ct);
        if (feature is null)
            return new RulingResult(false, "That feature no longer exists.", null);

        return TestRulings.Record(feature.Release.WorkspacePath, feature.Key, test, decision);
    }

    private static AttentionItem? Describe(ReleaseFeature feature, ReleaseStageRun run)
    {
        var stage = UserNotifier.FriendlyStage(run.StageName);
        var since = run.FinishedAt ?? run.StartedAt;

        if (run.Status == ReleaseStageStatus.BlockedSignoff)
        {
            return new AttentionItem(
                run.Id.ToString(), feature.ReleaseId, feature.Id, feature.Key, run.StageName, AttentionKind.Approval,
                "Your approval is needed",
                $"The {stage} step for \"{feature.Key}\" is finished. Look it over and approve it to let the work continue.",
                since);
        }

        if (run.Status == ReleaseStageStatus.BlockedGate && run.AutoRetrySuppressed)
        {
            // A test the checker thinks is wrong is a decision the person can make right here, with
            // the question and both answers - not something to go and find in a report file.
            var rulings = TestRulings.Pending(feature.Release.WorkspacePath, feature.Key);
            var reason = rulings.Count > 0
                ? $"The test checker believes {(rulings.Count == 1 ? "a test is" : $"{rulings.Count} tests are")} wrong and needs your ruling."
                : string.IsNullOrWhiteSpace(run.Summary)
                    ? "It cannot continue on its own."
                    : run.Summary.Trim();
            return new AttentionItem(
                run.Id.ToString(), feature.ReleaseId, feature.Id, feature.Key, run.StageName, AttentionKind.Decision,
                "The team needs a decision from you",
                $"\"{feature.Key}\" is paused at the {stage} step. {reason}",
                since,
                rulings);
        }

        return null;
    }
}
