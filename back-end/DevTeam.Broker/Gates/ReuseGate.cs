using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Deterministic gate that fails when the feature slice duplicates the shared core app instead
/// of extending it — same basename, same type, or a sibling scaffolded project (see
/// <see cref="CoreReuseChecker"/>). Deterministic file-system analysis, no LLM round-trip, so
/// it slots into the developer stage as both an exit step and the developer challenge's lint.
/// </summary>
public sealed class ReuseGate : IGate
{
    public string Name => BuiltinRegistry.ReuseGate;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return Task.FromResult(GateResult.Fail("reuse_gate requires a featureKey", "featureKey input missing"));

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(request.WorkspacePath, request.FeatureKey));
        if (manifest is null)
            return Task.FromResult(GateResult.Pass("No feature manifest — nothing to reuse-check", "manifest missing, skipping reuse gate"));

        var violations = CoreReuseChecker.FindViolations(request.WorkspacePath, manifest);
        if (violations.Count == 0)
        {
            var corePaths = CoreReuseChecker.CoreSourcePaths(request.WorkspacePath, manifest);
            return Task.FromResult(GateResult.Pass(
                "No duplication of the shared core",
                corePaths.Count == 0
                    ? "feature extends core (no core files present)"
                    : "feature extends core: " + string.Join(", ", corePaths)));
        }

        var evidence = new StringBuilder();
        foreach (var violation in violations)
            evidence.AppendLine(violation);
        return Task.FromResult(GateResult.Fail(
            $"{violations.Count} duplicate(s) against the shared core",
            evidence.ToString().TrimEnd()));
    }
}