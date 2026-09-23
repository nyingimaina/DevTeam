using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Deterministic gate that fails when a feature slice ships its own build project (see
/// <see cref="ProjectStructureChecker"/>). Runs alongside the reuse gate in the developer stage
/// so "one app per feature" is rejected before it can reach a reviewer.
/// </summary>
public sealed class ProjectStructureGate : IGate
{
    public string Name => BuiltinRegistry.ProjectStructure;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return Task.FromResult(GateResult.Fail("project_structure requires a featureKey", "featureKey input missing"));

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(request.WorkspacePath, request.FeatureKey));
        if (manifest is null)
            return Task.FromResult(GateResult.Pass("No feature manifest — nothing to structure-check", "manifest missing, skipping project structure gate"));

        var violations = ProjectStructureChecker.FindViolations(request.WorkspacePath, manifest);
        if (violations.Count == 0)
            return Task.FromResult(GateResult.Pass(
                "Feature adds source files to the shared app",
                "no build project inside the feature slice"));

        var evidence = new StringBuilder();
        foreach (var violation in violations)
            evidence.AppendLine(violation);
        return Task.FromResult(GateResult.Fail(
            $"{violations.Count} feature slice(s) define their own build project",
            evidence.ToString().TrimEnd()));
    }
}
