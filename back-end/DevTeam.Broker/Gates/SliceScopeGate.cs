using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Post-commit scope guard that catches files outside the feature slice + shared core, including
/// untracked new files that <see cref="SliceGuardGate"/> misses (it uses <c>git diff</c> which
/// only reports tracked changes; new files added to a working tree are invisible to it until
/// <c>git add</c> happens, which is done in <see cref="WorkflowEngine.CommitStageWorkAsync"/>
/// <em>after</em> all gates have already passed). This gate uses <c>git status --porcelain</c>
/// which reports both tracked and untracked files that were committed in the current HEAD, giving
/// reliable end-of-step coverage against scope creep.
/// </summary>
public sealed class SliceScopeGate : IGate
{
    private readonly IProcessRunner _runner;

    public SliceScopeGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.SliceScope;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return GateResult.Fail("slice_scope requires a featureKey", "featureKey input missing");

        var manifestPath = ArtifactPaths.ManifestPath(request.WorkspacePath, request.FeatureKey);
        var manifest = SliceManifestIO.TryRead(manifestPath);
        var sharedFiles = manifest?.Shared?.ToArray() ?? GateInputs.GetList(request.Inputs, "sharedFiles").ToArray();
        var coreBack = CorePaths.Back(manifest);
        var coreFront = CorePaths.Front(manifest);
        var templates = (manifest is null
            ? GateInputs.GetList(request.Inputs, "codePaths")
            : [manifest.CodePathBack, manifest.CodePathFront]).ToList();
        templates.Add(coreBack);
        templates.Add(coreFront);

        var status = await _runner.RunAsync(
            new ProcessRunRequest("git", "status --porcelain", request.WorkspacePath),
            cancellationToken);

        if (status.ExitCode != 0)
            return GateResult.Fail("git status failed", status.StandardError.Trim());

        var violations = new List<string>();
        foreach (var filePath in ChangedFileParser.Parse(status.StandardOutput))
        {
            if (!SliceAllowlist.IsAllowed(filePath, request.FeatureKey, sharedFiles, templates))
                violations.Add(filePath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (violations.Count == 0)
            return GateResult.Pass("All committed files are within the allowed scope", "slice_scope + core ok");

        var evidence = new StringBuilder();
        foreach (var violation in violations)
            evidence.Append("fail: out of slice → ").Append(violation).AppendLine();
        return GateResult.Fail(
            $"{violations.Count} committed file(s) outside the slice + core allowlist",
            evidence.ToString().TrimEnd());
    }
}