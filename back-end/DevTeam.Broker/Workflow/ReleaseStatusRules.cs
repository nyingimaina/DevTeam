using DevTeam.Broker.Domain;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// "Ready" means every feature in a release is complete — a fact about the features, not
/// something to remember on the release. The stored value is set whenever a feature completes
/// (even with other features still open) and is never reset when a new feature is added, so it
/// goes stale; anything that shows or gates on release status should use this instead.
/// </summary>
public static class ReleaseStatusRules
{
    public static ReleaseStatus Effective(ReleaseStatus stored, IReadOnlyCollection<ReleaseFeatureStatus> featureStatuses)
        => stored == ReleaseStatus.Ready
            && (featureStatuses.Count == 0 || featureStatuses.Any(s => s != ReleaseFeatureStatus.Complete))
            ? ReleaseStatus.InProgress
            : stored;
}
