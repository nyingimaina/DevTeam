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

        // The developer has write access to its own manifest.yaml for legitimate reasons (e.g.
        // free-form hierarchies) — a live read here would let it self-expand codePaths/Shared to
        // wherever it already wrote, grading its own homework. Once the developer stage has
        // frozen a snapshot (see DeveloperScopeSnapshotIO), that boundary wins; the live manifest
        // is only consulted before one exists (e.g. this gate invoked outside the developer
        // stage, or in isolation as here).
        var frozen = DeveloperScopeSnapshotIO.TryRead(request.WorkspacePath, request.FeatureKey);
        IReadOnlyList<string> sharedFiles;
        IReadOnlyList<string> templates;
        string coreBack;
        string coreFront;
        if (frozen is not null)
        {
            sharedFiles = frozen.Shared;
            templates = frozen.CodePaths.ToList();
            coreBack = frozen.CoreBack;
            coreFront = frozen.CoreFront;
        }
        else
        {
            var manifestPath = ArtifactPaths.ManifestPath(request.WorkspacePath, request.FeatureKey);
            var manifest = SliceManifestIO.TryRead(manifestPath);
            sharedFiles = manifest?.Shared?.ToArray() ?? GateInputs.GetList(request.Inputs, "sharedFiles").ToArray();
            coreBack = CorePaths.Back(manifest);
            coreFront = CorePaths.Front(manifest);
            templates = (manifest is null
                ? GateInputs.GetList(request.Inputs, "codePaths")
                : manifest.EffectiveCodePaths).ToList();
        }
        var allTemplates = templates.ToList();
        allTemplates.Add(coreBack);
        allTemplates.Add(coreFront);

        var status = await _runner.RunAsync(
            new ProcessRunRequest("git", "status --porcelain --untracked-files=all", request.WorkspacePath),
            cancellationToken);

        if (status.ExitCode != 0)
            return GateResult.Fail("git status failed", status.StandardError.Trim());

        var violations = new List<string>();
        foreach (var filePath in ChangedFileParser.Parse(status.StandardOutput))
        {
            if (!SliceAllowlist.IsAllowed(filePath, request.FeatureKey, sharedFiles, allTemplates))
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