using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

/// <summary>
/// Test convenience for the post-"no auto-feature" world: starting a release (or hotfix) creates
/// no features, so tests that exercise the pipeline/stage machinery need one explicitly. This
/// starts the release shell, adds a single feature keyed like the release, and returns the
/// refreshed release so CurrentFeatureId/Features/FlowPosition/Signoffs read exactly as they did
/// when StartReleaseAsync seeded that feature itself.
/// </summary>
internal static class WorkflowEngineTestExtensions
{
    public static async Task<DevTeamRelease> StartReleaseWithFeatureAsync(
        this WorkflowEngine engine, string key, string workspacePath, CancellationToken ct)
    {
        var release = await engine.StartReleaseAsync(key, workspacePath, ct);
        await engine.CreateFeatureAsync(release.Id, key, ct);
        return await engine.GetReleaseAsync(release.Id, ct);
    }
}
