using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class SliceGuardGate : IGate
{
    private readonly IProcessRunner _runner;

    public SliceGuardGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.SliceGuard;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return GateResult.Fail("slice_guard requires a featureKey", "featureKey input missing");

        var manifestPath = ArtifactPaths.ManifestPath(request.WorkspacePath, request.FeatureKey);
        var manifest = SliceManifestIO.TryRead(manifestPath);
        var sharedFiles = manifest?.Shared?.ToArray() ?? GateInputs.GetList(request.Inputs, "sharedFiles").ToArray();
        var templates = manifest is null
            ? GateInputs.GetList(request.Inputs, "codePaths").ToArray()
            : new[] { manifest.CodePathBack, manifest.CodePathFront };

        var diff = await _runner.RunAsync(
            new ProcessRunRequest("git", "diff --name-only HEAD", request.WorkspacePath),
            cancellationToken);

        if (diff.ExitCode != 0)
            return GateResult.Fail("Failed to list changed files", diff.StandardError.Trim());

        var violations = new List<string>();
        foreach (var rawPath in diff.StandardOutput.Split('\n'))
        {
            var changePath = rawPath.Trim();
            if (changePath.Length == 0)
                continue;

            if (!SliceAllowlist.IsAllowed(changePath, request.FeatureKey, sharedFiles, templates))
                violations.Add(changePath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (violations.Count == 0)
            return GateResult.Pass("Only allowed files were modified", "git diff confined to slice + shared allowlist");

        var evidence = new StringBuilder();
        foreach (var violation in violations)
            evidence.Append("fail: out of slice → ").Append(violation).AppendLine();
        return GateResult.Fail(
            $"{violations.Count} changed file(s) outside the slice allowlist",
            evidence.ToString().TrimEnd());
    }
}